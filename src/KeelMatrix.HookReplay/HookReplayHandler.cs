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
public sealed class HookReplayHandler : DelegatingHandler
{
    private const int CassetteSchemaVersion = 1;
    private const string Redacted = "[REDACTED]";
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
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.telemetry = telemetry ?? throw new ArgumentNullException(nameof(telemetry));
        if (!Enum.IsDefined(typeof(HookReplayMode), options.Mode))
            throw new ArgumentOutOfRangeException(nameof(options), "The HookReplay mode is not supported.");
        if (options.MaxBodyBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxBodyBytes must be greater than zero.");
        CassetteFile.ValidatePath(options.CassettePath);
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

        if (options.Mode == HookReplayMode.Replay)
            return await ReplayAsync(request, cancellationToken).ConfigureAwait(false);

        return await RecordAsync(request, cancellationToken).ConfigureAwait(false);
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
        await cassetteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (CassetteInteraction candidate in interactions)
            {
                if (candidate.Consumed)
                    continue;

                if (!RequestMatching.IsDefaultMatch(capture.Request, candidate.Request, options.MatchHeaders))
                {
                    mismatch ??= RequestMatching.DescribeNearestDifference(
                        capture.Request,
                        candidate.Request,
                        options.MatchHeaders);
                    continue;
                }

                var recorded = RequestMatching.ToPublicRequest(candidate.Request);
                if (options.RequestMatcher is not null &&
                    !options.RequestMatcher.Matches(capture.Request, recorded))
                {
                    mismatch ??= "custom matcher";
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

        ReplaceResponseContent(response, responseCapture.RawBody, responseCapture.OriginalContentHeaders);

        var interaction = new CassetteInteraction
        {
            Request = capture.ToCassetteRequest(),
            Response = responseCapture.ToCassetteResponse()
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
            await CassetteFile.WriteAsync(
                options.CassettePath,
                CassetteSchemaVersion,
                interactions,
                cancellationToken).ConfigureAwait(false);
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

        var content = new ByteArrayContent(
            recorded.Body is null
                ? Array.Empty<byte>()
                : Encoding.UTF8.GetBytes(recorded.Body.Text));
        response.Content = content;
        AddHeaders(response.Headers, recorded.Headers);
        AddHeaders(content.Headers, recorded.BodyHeaders);
        if (recorded.Body is not null && !string.IsNullOrWhiteSpace(recorded.Body.ContentType))
            content.Headers.TryAddWithoutValidation("Content-Type", recorded.Body.ContentType);
        return response;
    }

    private static void AddHeaders(HttpHeaders target, IReadOnlyList<CassetteHeader> headers)
    {
        foreach (CassetteHeader header in headers)
            target.TryAddWithoutValidation(header.Name, header.Value);
    }

    private static void ReplaceRequestContent(
        HttpRequestMessage request,
        byte[]? body,
        IReadOnlyList<CassetteHeader> originalHeaders)
    {
        if (body is null)
            return;

        var replacement = new ByteArrayContent(body);
        AddHeaders(replacement.Headers, originalHeaders);
        request.Content = replacement;
    }

    private static void ReplaceResponseContent(
        HttpResponseMessage response,
        byte[]? body,
        IReadOnlyList<CassetteHeader> originalHeaders)
    {
        if (body is null)
            return;

        var replacement = new ByteArrayContent(body);
        AddHeaders(replacement.Headers, originalHeaders);
        response.Content = replacement;
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
    public string ContentType { get; set; } = "text/plain; charset=utf-8";
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
