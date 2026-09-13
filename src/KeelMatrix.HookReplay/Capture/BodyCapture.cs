using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace KeelMatrix.HookReplay;

internal sealed class BodyCapture
{
    private BodyCapture(
        byte[] rawBytes,
        string text,
        string? contentType,
        string fingerprint,
        IReadOnlyList<CassetteHeader> originalHeaders)
    {
        RawBytes = rawBytes;
        Text = text;
        ContentType = contentType;
        Fingerprint = fingerprint;
        OriginalHeaders = originalHeaders;
    }

    public byte[] RawBytes { get; }
    public string Text { get; }
    public string? ContentType { get; }
    public string Fingerprint { get; }
    public IReadOnlyList<CassetteHeader> OriginalHeaders { get; }

    public static async Task<BodyCapture?> CreateAsync(
        HttpContent? content,
        HookReplayOptions options,
        CancellationToken cancellationToken,
        HttpSanitizer? sanitizer = null)
    {
        if (content is null)
            return null;

        sanitizer ??= new HttpSanitizer(options.Redactors);
        List<CassetteHeader> originalHeaders = CaptureRawHeaders(content.Headers);
        byte[] bytes;
        try
        {
            using var stream = new LimitedMemoryStream(options.MaxBodyBytes);
#if NET8_0_OR_GREATER
            await content.CopyToAsync(stream, null, cancellationToken).ConfigureAwait(false);
#else
            await content.CopyToAsync(stream).WaitWithCancellationAsync(cancellationToken).ConfigureAwait(false);
#endif
            bytes = stream.ToArray();
        }
        catch (HookReplayException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (ObjectDisposedException exception)
        {
            throw new HookReplayUnsupportedContentException(
                "The HTTP content was disposed before HookReplay could capture it.", exception);
        }
        catch (Exception exception)
        {
            throw new HookReplayUnsupportedContentException(
                "The HTTP content could not be captured safely.", exception);
        }

        string? contentType;
        string mediaType;
        Encoding encoding;
        try
        {
            MediaTypeHeaderValue? contentTypeHeader = content.Headers.ContentType;
            mediaType = contentTypeHeader?.MediaType ?? "text/plain";
            encoding = DeclaredTextEncoding.Resolve(contentTypeHeader?.CharSet);
            contentType = contentTypeHeader is null
                ? null
                : sanitizer.SanitizeHeaderValue("Content-Type", contentTypeHeader.ToString());
        }
        catch (ObjectDisposedException exception)
        {
            throw new HookReplayUnsupportedContentException(
                "The HTTP content headers were disposed before capture.", exception);
        }

        if (!HttpSanitizer.IsSupportedMediaType(mediaType))
            throw new HookReplayUnsupportedContentException(
                "Content type '" + mediaType + "' is not supported. HookReplay supports text, JSON, form, and empty content.");

        if (bytes.Length == 0)
            return null;

        string rawText = DeclaredTextEncoding.Decode(encoding, bytes);

        string sanitized = sanitizer.SanitizeBody(rawText, mediaType);
        DeclaredTextEncoding.EnsureEncodable(encoding, sanitized, "The sanitized HTTP body");
        return new BodyCapture(
            bytes,
            sanitized,
            contentType,
            ComputeFingerprint(sanitized),
            originalHeaders);
    }

    private static List<CassetteHeader> CaptureRawHeaders(HttpContentHeaders headers)
    {
        return headers
            .SelectMany(pair => pair.Value.Select(value => new CassetteHeader(pair.Key, value)))
            .OrderBy(header => header.Name, StringComparer.Ordinal)
            .ThenBy(header => header.Value, StringComparer.Ordinal)
            .ToList();
    }

    internal static string ComputeFingerprint(string value)
    {
        using SHA256 sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte valueByte in bytes)
            builder.Append(valueByte.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}

/// <summary>
/// Resolves the text encoding used for captured and replayed bodies.
/// </summary>
/// <remarks>
/// A declared <c>charset</c> is honored when it names one of the bounded supported
/// encodings; undeclared text is UTF-8. Unsupported declared charsets fail closed
/// instead of being silently reinterpreted.
/// </remarks>
