using System.Net.Http;

namespace KeelMatrix.HookReplay;

internal sealed class ResponseCapture
{
    private ResponseCapture(
        HttpResponseMessage response,
        BodyCapture? body,
        HttpSanitizer sanitizer)
    {
        this.response = response;
        this.body = body;
        this.sanitizer = sanitizer;
    }

    private readonly HttpResponseMessage response;
    private readonly BodyCapture? body;
    private readonly HttpSanitizer sanitizer;

    public byte[]? RawBody => body?.RawBytes;
    public IReadOnlyList<CassetteHeader> OriginalContentHeaders =>
        body?.OriginalHeaders ?? Array.Empty<CassetteHeader>();

    public CassetteResponse ToCassetteResponse()
    {
        var responseHeaders = sanitizer.SanitizeHeaderList(
            response.Headers,
            null,
            dropBodyDependentHeaders: true);
        var bodyHeaders = response.Content is null
            ? new List<CassetteHeader>()
            : sanitizer.SanitizeHeaderList(
                response.Content.Headers,
                null,
                dropBodyDependentHeaders: true);
        return new CassetteResponse
        {
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase is null
                ? null
                : sanitizer.ApplyTextRedactors(response.ReasonPhrase),
            Version = response.Version,
            Headers = responseHeaders,
            BodyHeaders = bodyHeaders,
            Body = body is null
                ? null
                : new CassetteBody
                {
                    ContentType = body.ContentType,
                    Text = body.Text
                }
        };
    }

    public static async Task<ResponseCapture> CreateAsync(
        HttpResponseMessage response,
        HookReplayOptions options,
        CancellationToken cancellationToken)
    {
        var sanitizer = new HttpSanitizer(options.Redactors);
        BodyCapture? body = await BodyCapture.CreateAsync(
            response.Content,
            options,
            cancellationToken,
            sanitizer).ConfigureAwait(false);
        return new ResponseCapture(response, body, sanitizer);
    }
}
