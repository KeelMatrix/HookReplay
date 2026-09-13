using KeelMatrix.Redaction;
using System.Net.Http;

namespace KeelMatrix.HookReplay;

internal sealed class RequestCapture
{
    private RequestCapture(
        HookReplayRequest request,
        byte[]? rawBody,
        IReadOnlyList<CassetteHeader> originalContentHeaders,
        IReadOnlyList<CassetteHeader> persistedHeaders,
        string? body,
        string? bodyContentType)
    {
        Request = request;
        RawBody = rawBody;
        OriginalContentHeaders = originalContentHeaders;
        this.persistedHeaders = persistedHeaders;
        this.body = body;
        this.bodyContentType = bodyContentType;
    }

    private readonly string? body;
    private readonly string? bodyContentType;
    private readonly IReadOnlyList<CassetteHeader> persistedHeaders;

    public HookReplayRequest Request { get; }
    public byte[]? RawBody { get; }
    public IReadOnlyList<CassetteHeader> OriginalContentHeaders { get; }

    public CassetteRequest ToCassetteRequest()
    {
        return new CassetteRequest
        {
            Method = Request.Method,
            NormalizedUri = Request.NormalizedUri,
            BodyFingerprint = Request.BodyFingerprint,
            Body = body,
            BodyContentType = bodyContentType,
            Headers = persistedHeaders.ToList(),
            MatchHeaders = RequestHeadersForMatching
        };
    }

    internal List<CassetteHeader> RequestHeadersForMatching { get; private set; } = new();

    public static async Task<RequestCapture> CreateAsync(
        HttpRequestMessage request,
        HookReplayOptions options,
        CancellationToken cancellationToken)
    {
        var sanitizer = new HttpSanitizer(options.Redactors);
        BodyCapture? body = await BodyCapture.CreateAsync(
            request.Content,
            options,
            cancellationToken,
            sanitizer).ConfigureAwait(false);
        List<CassetteHeader> persistedHeaders = sanitizer.SanitizeHeaderList(
            request.Headers,
            request.Content?.Headers,
            dropBodyDependentHeaders: true);
        Dictionary<string, string> headers = sanitizer.SanitizeHeaders(
            request.Headers,
            request.Content?.Headers);
        Dictionary<string, string> matchHeaders =
            sanitizer.CreateMatchHeaders(request.Headers, request.Content?.Headers, options.MatchHeaders);
        foreach (KeyValuePair<string, string> pair in matchHeaders)
            headers[pair.Key] = pair.Value;
        var capture = new RequestCapture(
            new HookReplayRequest(
                request.Method.Method,
                UriNormalizer.Normalize(request.RequestUri, sanitizer),
                body?.Fingerprint,
                headers),
            body?.RawBytes,
            body?.OriginalHeaders ?? Array.Empty<CassetteHeader>(),
            persistedHeaders,
            body?.Text,
            body?.ContentType);
        capture.RequestHeadersForMatching = matchHeaders
            .Select(pair => new CassetteHeader(pair.Key, pair.Value))
            .OrderBy(header => header.Name, StringComparer.Ordinal)
            .ThenBy(header => header.Value, StringComparer.Ordinal)
            .ToList();
        return capture;
    }
}
