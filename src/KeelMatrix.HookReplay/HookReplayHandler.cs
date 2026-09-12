using KeelMatrix.Redaction;
using KeelMatrix.Telemetry;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("KeelMatrix.HookReplay.Tests")]

namespace KeelMatrix.HookReplay;

/// <summary>
/// Records successful HTTP exchanges or replays them from a deterministic cassette.
/// </summary>
/// <remarks>
/// The cassette path, body limit, selected headers, redactors, and custom matcher are
/// captured when the handler is constructed. The current mode is read and validated
/// at the start of every request so an invalid mutation fails closed.
/// </remarks>
public sealed class HookReplayHandler : DelegatingHandler
{
    private const int CassetteSchemaVersion = 1;
    private const string Redacted = "[REDACTED]";
    private readonly HookReplayOptions liveOptions;
    private readonly HookReplayOptions options;
    private readonly SemaphoreSlim cassetteGate = new(1, 1);
    private readonly IHookReplayTelemetry telemetry;
    private readonly List<CassetteInteraction> interactions = new();
    private bool loaded;
    private bool disposed;

    /// <summary>Initializes a handler with explicit options and an optional inner transport.</summary>
    /// <param name="options">The record or replay configuration.</param>
    /// <param name="innerHandler">The transport used only in record mode.</param>
    public HookReplayHandler(HookReplayOptions options, HttpMessageHandler? innerHandler = null)
        : this(options, innerHandler, CreateTelemetry(options))
    {
    }

    internal HookReplayHandler(
        HookReplayOptions options,
        HttpMessageHandler? innerHandler,
        IHookReplayTelemetry telemetry)
        : base(innerHandler ?? new HttpClientHandler())
    {
        this.liveOptions = options ?? throw new ArgumentNullException(nameof(options));
        this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        if (!Enum.IsDefined(typeof(HookReplayMode), options.Mode))
            throw new ArgumentOutOfRangeException(nameof(options), "The HookReplay mode is not supported.");
        if (options.MaxBodyBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxBodyBytes must be greater than zero.");
        CassetteFile.ValidatePath(options.CassettePath);
        this.options = SnapshotOptions(options);
    }

    private static HookReplayTelemetry CreateTelemetry(HookReplayOptions options)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));

        CassetteFile.ValidatePath(options.CassettePath);
        return new HookReplayTelemetry();
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));

        switch (ValidateCurrentMode())
        {
            case HookReplayMode.Replay:
                return await ReplayAsync(request, cancellationToken).ConfigureAwait(false);
            case HookReplayMode.Record:
                return await RecordAsync(request, cancellationToken).ConfigureAwait(false);
            default:
                throw new InvalidOperationException("HookReplay mode validation did not produce a supported mode.");
        }
    }

    private HookReplayMode ValidateCurrentMode()
    {
        HookReplayMode mode = liveOptions.Mode;
        if (!Enum.IsDefined(typeof(HookReplayMode), mode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(HookReplayOptions.Mode),
                mode,
                "The current HookReplay mode is not supported. Set Mode to Record or Replay.");
        }

        return mode;
    }

    private static HookReplayOptions SnapshotOptions(HookReplayOptions source)
    {
        var snapshot = new HookReplayOptions(source.CassettePath)
        {
            Mode = source.Mode,
            MaxBodyBytes = source.MaxBodyBytes,
            RequestMatcher = source.RequestMatcher
        };
        foreach (string header in source.MatchHeaders)
            snapshot.MatchHeaders.Add(header);
        foreach (ITextRedactor redactor in source.Redactors)
            snapshot.Redactors.Add(redactor);
        return snapshot;
    }

    private async Task<HttpResponseMessage> ReplayAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCapture capture = await RequestCapture.CreateAsync(
            request,
            options,
            cancellationToken).ConfigureAwait(false);

        await EnsureLoadedAsync(cancellationToken).ConfigureAwait(false);

        CassetteInteraction? selected = null;
        string? mismatch = null;
        int closestMismatchCount = int.MaxValue;
        int closestCandidateIndex = int.MaxValue;
        await cassetteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            for (int candidateIndex = 0; candidateIndex < interactions.Count; candidateIndex++)
            {
                CassetteInteraction candidate = interactions[candidateIndex];
                if (candidate.Consumed)
                    continue;

                RequestMatching.Difference difference = RequestMatching.DescribeDifference(
                    capture.Request,
                    candidate.Request,
                    options.MatchHeaders);
                if (!difference.IsMatch)
                {
                    if (difference.MismatchCount < closestMismatchCount ||
                        (difference.MismatchCount == closestMismatchCount &&
                         candidateIndex < closestCandidateIndex))
                    {
                        closestMismatchCount = difference.MismatchCount;
                        closestCandidateIndex = candidateIndex;
                        mismatch = difference.Description;
                    }
                    continue;
                }

                var recorded = RequestMatching.ToPublicRequest(candidate.Request);
                if (options.RequestMatcher is not null &&
                    !options.RequestMatcher.Matches(capture.Request, recorded))
                {
                    if (1 < closestMismatchCount ||
                        (closestMismatchCount == 1 && candidateIndex < closestCandidateIndex))
                    {
                        closestMismatchCount = 1;
                        closestCandidateIndex = candidateIndex;
                        mismatch = "custom matcher";
                    }
                    continue;
                }

                candidate.Consumed = true;
                selected = candidate;
                break;
            }
        }
        finally
        {
            cassetteGate.Release();
        }

        if (selected is null)
        {
            string suffix = mismatch is null
                ? "The cassette contains no unconsumed interactions."
                : "The nearest interaction differs in " + mismatch + ".";
            throw new HookReplayMismatchException(
                "No cassette interaction matched the request. " + suffix +
                " Replay never falls back to the network.");
        }

        TrackTelemetry();
        return ReplayResponse(request, selected.Response);
    }

    private async Task<HttpResponseMessage> RecordAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCapture capture = await RequestCapture.CreateAsync(
            request,
            options,
            cancellationToken).ConfigureAwait(false);
        ReplaceRequestContent(request, capture.RawBody, capture.OriginalContentHeaders);

        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        ResponseCapture responseCapture;
        try
        {
            responseCapture = await ResponseCapture.CreateAsync(
                response,
                options,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        CassetteResponse cassetteResponse;
        try
        {
            cassetteResponse = responseCapture.ToCassetteResponse();
        }
        catch
        {
            // Sanitizing the captured response can fail, so the response and its content are
            // owned here until the cassette data has been built from them.
            response.Dispose();
            throw;
        }

        ReplaceResponseContent(response, responseCapture.RawBody, responseCapture.OriginalContentHeaders);
        var interaction = new CassetteInteraction
        {
            Request = capture.ToCassetteRequest(),
            Response = cassetteResponse
        };

        try
        {
            await AppendAsync(interaction, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }

        TrackTelemetry();
        return response;
    }

    private void TrackTelemetry()
    {
        try
        {
            telemetry.TrackActivation();
        }
        catch
        {
            // Telemetry is best-effort and must never affect record or replay.
        }

        try
        {
            telemetry.TrackHeartbeat();
        }
        catch
        {
            // Telemetry is best-effort and must never affect record or replay.
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (loaded)
            return;

        await cassetteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (loaded)
                return;

            IReadOnlyList<CassetteInteraction> existing =
                await CassetteFile.ReadAsync(
                    options.CassettePath,
                    CassetteSchemaVersion,
                    new HttpSanitizer(options.Redactors),
                    cancellationToken).ConfigureAwait(false);
            interactions.AddRange(existing);
            loaded = true;
        }
        finally
        {
            cassetteGate.Release();
        }
    }

    private async Task AppendAsync(CassetteInteraction interaction, CancellationToken cancellationToken)
    {
        await cassetteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!loaded)
            {
                if (CassetteFile.Exists(options.CassettePath))
                {
                    IReadOnlyList<CassetteInteraction> existing =
                        await CassetteFile.ReadAsync(
                            options.CassettePath,
                            CassetteSchemaVersion,
                            new HttpSanitizer(options.Redactors),
                            cancellationToken).ConfigureAwait(false);
                    interactions.AddRange(existing);
                }
                loaded = true;
            }

            interactions.Add(interaction);
            try
            {
                await CassetteFile.WriteAsync(
                    options.CassettePath,
                    CassetteSchemaVersion,
                    interactions,
                    cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The interaction is only committed once the durable cassette write succeeds.
                // Rolling back here keeps a failed append from becoming a replayable ghost.
                interactions.Remove(interaction);
                throw;
            }
        }
        finally
        {
            cassetteGate.Release();
        }
    }

    private static HttpResponseMessage ReplayResponse(
        HttpRequestMessage request,
        CassetteResponse recorded)
    {
        var response = new HttpResponseMessage((HttpStatusCode)recorded.StatusCode)
        {
            ReasonPhrase = recorded.ReasonPhrase,
            RequestMessage = request,
            Version = recorded.Version
        };

        byte[] body = recorded.Body is null
            ? Array.Empty<byte>()
            : DeclaredTextEncoding
                .Resolve(DeclaredTextEncoding.GetCharset(recorded.Body.ContentType))
                .GetBytes(recorded.Body.Text);
        var content = new ByteArrayContent(body);
        response.Content = content;
        AddReplayHeaders(response.Headers, recorded.Headers);
        AddReplayHeaders(content.Headers, recorded.BodyHeaders);
        return response;
    }

    private static void AddHeaders(HttpHeaders target, IEnumerable<CassetteHeader> headers)
    {
        foreach (CassetteHeader header in headers)
            target.TryAddWithoutValidation(header.Name, header.Value);
    }

    /// <summary>
    /// Adds replayed headers while dropping values that described the pre-sanitization body.
    /// </summary>
    private static void AddReplayHeaders(HttpHeaders target, IEnumerable<CassetteHeader> headers)
    {
        AddHeaders(
            target,
            headers.Where(header => !HttpSanitizer.IsBodyDependentHeaderName(header.Name)));
    }

    private static void ReplaceRequestContent(
        HttpRequestMessage request,
        byte[]? body,
        IReadOnlyList<CassetteHeader> originalHeaders)
    {
        if (body is null)
            return;

        HttpContent? superseded = request.Content;
        var replacement = new ByteArrayContent(body);
        AddHeaders(replacement.Headers, originalHeaders);
        request.Content = replacement;
        superseded?.Dispose();
    }

    private static void ReplaceResponseContent(
        HttpResponseMessage response,
        byte[]? body,
        IReadOnlyList<CassetteHeader> originalHeaders)
    {
        if (body is null)
            return;

        HttpContent? superseded = response.Content;
        var replacement = new ByteArrayContent(body);
        AddHeaders(replacement.Headers, originalHeaders);
        response.Content = replacement;
        superseded?.Dispose();
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed)
        {
            disposed = true;
            cassetteGate.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal sealed class CassetteInteraction
{
    public CassetteRequest Request { get; set; } = null!;
    public CassetteResponse Response { get; set; } = null!;
    public bool Consumed { get; set; }
}

internal sealed class CassetteRequest
{
    public string Method { get; set; } = string.Empty;
    public string NormalizedUri { get; set; } = string.Empty;
    public string? BodyFingerprint { get; set; }
    public string? Body { get; set; }
    public string? BodyContentType { get; set; }
    public List<CassetteHeader> Headers { get; set; } = new();
    public List<CassetteHeader> MatchHeaders { get; set; } = new();
}

internal sealed class CassetteResponse
{
    public int StatusCode { get; set; }
    public string? ReasonPhrase { get; set; }
    public Version Version { get; set; } = HttpVersion.Version11;
    public List<CassetteHeader> Headers { get; set; } = new();
    public List<CassetteHeader> BodyHeaders { get; set; } = new();
    public CassetteBody? Body { get; set; }
}

internal sealed class CassetteBody
{
    /// <summary>
    /// The declared content type, or <see langword="null"/> when the captured content had none.
    /// </summary>
    /// <remarks>
    /// A null value means "no content type was declared", and replay must not synthesize one.
    /// </remarks>
    public string? ContentType { get; set; }

    public string Text { get; set; } = string.Empty;
}

internal interface IHookReplayTelemetry
{
    void TrackActivation();

    void TrackHeartbeat();
}

internal sealed class HookReplayTelemetry : IHookReplayTelemetry
{
    private readonly Client? client;

    public HookReplayTelemetry()
    {
        try
        {
            client = new Client("HookReplay", typeof(HookReplayHandler));
        }
        catch
        {
            client = null;
        }
    }

    public void TrackActivation()
    {
        try
        {
            client?.TrackActivation();
        }
        catch
        {
        }
    }

    public void TrackHeartbeat()
    {
        try
        {
            client?.TrackHeartbeat();
        }
        catch
        {
        }
    }
}
