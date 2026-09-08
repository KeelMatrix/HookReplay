using KeelMatrix.Redaction;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

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
        BodyCapture? body = await BodyCapture.CreateAsync(
            request.Content,
            options,
            cancellationToken).ConfigureAwait(false);
        var sanitizer = new HttpSanitizer(options.Redactors);
        List<CassetteHeader> persistedHeaders = sanitizer.SanitizeHeaderList(
            request.Headers,
            request.Content?.Headers);
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
                UriNormalizer.Normalize(request.RequestUri),
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
        var responseHeaders = sanitizer.SanitizeHeaderList(response.Headers, null);
        var bodyHeaders = response.Content is null
            ? new List<CassetteHeader>()
            : sanitizer.SanitizeHeaderList(response.Content.Headers, null);
        return new CassetteResponse
        {
            StatusCode = (int)response.StatusCode,
            ReasonPhrase = response.ReasonPhrase,
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
        BodyCapture? body = await BodyCapture.CreateAsync(
            response.Content,
            options,
            cancellationToken).ConfigureAwait(false);
        return new ResponseCapture(response, body, new HttpSanitizer(options.Redactors));
    }
}

internal sealed class BodyCapture
{
    private BodyCapture(
        byte[] rawBytes,
        string text,
        string contentType,
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
    public string ContentType { get; }
    public string Fingerprint { get; }
    public IReadOnlyList<CassetteHeader> OriginalHeaders { get; }

    public static async Task<BodyCapture?> CreateAsync(
        HttpContent? content,
        HookReplayOptions options,
        CancellationToken cancellationToken)
    {
        if (content is null)
            return null;

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

        if (bytes.Length == 0)
            return null;

        string contentType;
        try
        {
            contentType = content.Headers.ContentType?.ToString()
                ?? "text/plain; charset=utf-8";
        }
        catch (ObjectDisposedException exception)
        {
            throw new HookReplayUnsupportedContentException(
                "The HTTP content headers were disposed before capture.", exception);
        }

        string mediaType = content.Headers.ContentType?.MediaType ?? "text/plain";
        if (!HttpSanitizer.IsSupportedMediaType(mediaType))
            throw new HookReplayUnsupportedContentException(
                "Content type '" + mediaType + "' is not supported. HookReplay supports text, JSON, form, and empty content.");

        string rawText;
        try
        {
            rawText = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (Exception exception)
        {
            throw new HookReplayUnsupportedContentException(
                "The HTTP content is not valid UTF-8 text.", exception);
        }

        var sanitizer = new HttpSanitizer(options.Redactors);
        string sanitized = sanitizer.SanitizeBody(rawText, mediaType);
        return new BodyCapture(
            bytes,
            sanitized,
            contentType,
            Hash(sanitized),
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

    private static string Hash(string value)
    {
        using SHA256 sha = SHA256.Create();
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(bytes.Length * 2);
        foreach (byte valueByte in bytes)
            builder.Append(valueByte.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}

internal sealed class LimitedMemoryStream : MemoryStream
{
    private readonly int limit;

    public LimitedMemoryStream(int limit)
    {
        this.limit = limit;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureCapacity(count);
        base.Write(buffer, offset, count);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        EnsureCapacity(count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    private void EnsureCapacity(int incoming)
    {
        if (Length > limit - incoming)
            throw new HookReplaySizeLimitException(
                "The HTTP body exceeds the configured MaxBodyBytes limit of " + limit + " bytes.");
    }
}

internal sealed class HttpSanitizer
{
    private const string Redacted = "[REDACTED]";
    private static readonly ITextRedactor[] BuiltInRedactors =
    {
        new AuthorizationRedactor(),
        new ApiKeyRedactor(),
        new CookieRedactor(),
        new JwtTokenRedactor(),
        new ConnectionStringPasswordRedactor(),
        new AzureKeyLikeRedactor(),
        new GoogleApiKeyRedactor(),
        new AwsAccessKeyRedactor(),
        new LongHexTokenRedactor(),
        new UrlQueryTokenRedactor()
    };
    private readonly IReadOnlyList<ITextRedactor> customRedactors;

    public HttpSanitizer(IEnumerable<ITextRedactor> customRedactors)
    {
        this.customRedactors = customRedactors.ToArray();
    }

    public static bool IsSupportedMediaType(string mediaType)
    {
        return mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
            || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase);
    }

    public string SanitizeBody(string text, string mediaType)
    {
        if (mediaType.Equals("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return SanitizeForm(text);

        if (mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
            mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(text);
                return WriteCanonicalJson(document.RootElement);
            }
            catch (JsonException exception)
            {
                throw new HookReplayUnsupportedContentException(
                    "The HTTP body declares JSON but is not valid JSON.", exception);
            }
        }

        return ApplyTextRedactors(text);
    }

    public Dictionary<string, string> SanitizeHeaders(
        HttpHeaders headers,
        HttpHeaders? contentHeaders)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        AddHeaders(values, headers);
        if (contentHeaders is not null)
            AddHeaders(values, contentHeaders);

        return values
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                pair => pair.Key,
                pair => string.Join("\n", pair.Value.Select(value =>
                    IsSensitiveName(pair.Key) ? Redacted : ApplyTextRedactors(value))),
                StringComparer.OrdinalIgnoreCase);
    }

    public static List<CassetteHeader> SanitizeHeaders(
        HttpHeaders headers,
        HttpHeaders? contentHeaders,
        HttpSanitizer sanitizer)
    {
        Dictionary<string, string> result = sanitizer.SanitizeHeaders(headers, contentHeaders);
        return result
            .Select(pair => new CassetteHeader(pair.Key, pair.Value))
            .OrderBy(header => header.Name, StringComparer.Ordinal)
            .ThenBy(header => header.Value, StringComparer.Ordinal)
            .ToList();
    }

    public List<CassetteHeader> SanitizeHeaderList(
        HttpHeaders headers,
        HttpHeaders? contentHeaders)
    {
        var result = new List<CassetteHeader>();
        AddSanitizedHeaders(result, headers);
        if (contentHeaders is not null)
            AddSanitizedHeaders(result, contentHeaders);
        return result
            .OrderBy(header => header.Name, StringComparer.Ordinal)
            .ThenBy(header => header.Value, StringComparer.Ordinal)
            .ToList();
    }

    public Dictionary<string, string> CreateMatchHeaders(
        HttpHeaders headers,
        HttpHeaders? contentHeaders,
        ISet<string> selectedNames)
    {
        Dictionary<string, List<string>> values = new(StringComparer.OrdinalIgnoreCase);
        AddHeaders(values, headers);
        if (contentHeaders is not null)
            AddHeaders(values, contentHeaders);

        return values
            .Where(pair => selectedNames.Contains(pair.Key))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(
                pair => pair.Key,
                pair => string.Join("\n", pair.Value.Select(value =>
                    IsSensitiveName(pair.Key)
                        ? "sha256:" + BodyHash(value)
                        : ApplyTextRedactors(value))),
                StringComparer.OrdinalIgnoreCase);
    }

    public string ApplyTextRedactors(string value)
    {
        string sanitized = value;
        foreach (ITextRedactor redactor in BuiltInRedactors.Concat(customRedactors))
        {
            try
            {
                sanitized = redactor.Redact(sanitized);
            }
            catch (Exception exception)
            {
                throw new HookReplayUnsupportedContentException(
                    "A configured redactor failed; no cassette was written.", exception);
            }
        }

        return sanitized;
    }

    public static bool IsSensitiveName(string name)
    {
        string normalized = name.Replace("-", string.Empty)
            .Replace("_", string.Empty)
            .ToLowerInvariant();
        return normalized.IndexOf("authorization", StringComparison.Ordinal) >= 0
            || normalized.IndexOf("proxyauth", StringComparison.Ordinal) >= 0
            || normalized.IndexOf("cookie", StringComparison.Ordinal) >= 0
            || normalized.IndexOf("token", StringComparison.Ordinal) >= 0
            || normalized.IndexOf("secret", StringComparison.Ordinal) >= 0
            || normalized.IndexOf("password", StringComparison.Ordinal) >= 0
            || normalized.IndexOf("apikey", StringComparison.Ordinal) >= 0
            || normalized.IndexOf("credential", StringComparison.Ordinal) >= 0
            || normalized.IndexOf("signature", StringComparison.Ordinal) >= 0
            || normalized.IndexOf("privatekey", StringComparison.Ordinal) >= 0;
    }

    private string SanitizeForm(string text)
    {
        var pairs = new List<KeyValuePair<string, string>>();
        foreach (string part in text.Split('&'))
        {
            if (part.Length == 0)
                continue;
            int equals = part.IndexOf('=');
            string rawName = equals < 0 ? part : part.Substring(0, equals);
            string rawValue = equals < 0 ? string.Empty : part.Substring(equals + 1);
            string name = WebUtility.UrlDecode(rawName) ?? string.Empty;
            string value = WebUtility.UrlDecode(rawValue) ?? string.Empty;
            pairs.Add(new KeyValuePair<string, string>(
                name,
                IsSensitiveName(name) ? Redacted : ApplyTextRedactors(value)));
        }

        var builder = new StringBuilder();
        foreach (KeyValuePair<string, string> pair in pairs
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal))
        {
            if (builder.Length > 0)
                builder.Append('&');
            builder.Append(WebUtility.UrlEncode(pair.Key));
            builder.Append('=');
            builder.Append(WebUtility.UrlEncode(pair.Value));
        }
        return builder.ToString();
    }

    private string WriteCanonicalJson(JsonElement element)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
            WriteJsonElement(writer, element, null);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private void WriteJsonElement(Utf8JsonWriter writer, JsonElement element, string? propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(
                    property => property.Name,
                    StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    if (IsSensitiveName(property.Name))
                        writer.WriteStringValue(Redacted);
                    else
                        WriteJsonElement(writer, property.Value, property.Name);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement child in element.EnumerateArray())
                    WriteJsonElement(writer, child, propertyName);
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(ApplyTextRedactors(element.GetString() ?? string.Empty));
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText());
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private static void AddHeaders(
        Dictionary<string, List<string>> destination,
        HttpHeaders source)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> pair in source)
        {
            if (!destination.TryGetValue(pair.Key, out List<string>? values))
            {
                values = new List<string>();
                destination.Add(pair.Key, values);
            }
            values.AddRange(pair.Value);
        }
    }

    private void AddSanitizedHeaders(
        List<CassetteHeader> destination,
        HttpHeaders source)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> pair in source)
        {
            foreach (string value in pair.Value)
            {
                destination.Add(new CassetteHeader(
                    pair.Key,
                    IsSensitiveName(pair.Key) ? Redacted : ApplyTextRedactors(value)));
            }
        }
    }

    private static string BodyHash(string value)
    {
        using SHA256 sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(value));
        var builder = new StringBuilder(hash.Length * 2);
        foreach (byte valueByte in hash)
            builder.Append(valueByte.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
        return builder.ToString();
    }
}

internal static class UriNormalizer
{
    private static readonly char[] QuerySeparators = { '&' };

    public static string Normalize(Uri? uri)
    {
        if (uri is null)
            throw new HookReplayMismatchException("The HTTP request has no URI.");

        string scheme = uri.Scheme.ToLowerInvariant();
        string host = uri.Host.ToLowerInvariant();
        string port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string path = uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped);
        if (path.Length == 0)
            path = "/";
        else if (path[0] != '/')
            path = "/" + path;

        var builder = new StringBuilder(scheme)
            .Append("://")
            .Append(host)
            .Append(port)
            .Append(path);
        var query = new List<KeyValuePair<string, string>>();
        string rawQuery = uri.Query.TrimStart('?');
        foreach (string part in rawQuery.Split(QuerySeparators, StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=');
            string name = WebUtility.UrlDecode(equals < 0 ? part : part.Substring(0, equals)) ?? string.Empty;
            string value = WebUtility.UrlDecode(equals < 0 ? string.Empty : part.Substring(equals + 1)) ?? string.Empty;
            string normalizedValue = HttpSanitizer.IsSensitiveName(name)
                ? "[REDACTED]"
                : value;
            query.Add(new KeyValuePair<string, string>(name, normalizedValue));
        }

        foreach (KeyValuePair<string, string> pair in query
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ThenBy(pair => pair.Value, StringComparer.Ordinal))
        {
            if (builder.ToString().IndexOf('?') < 0)
                builder.Append('?');
            else
                builder.Append('&');
            builder.Append(WebUtility.UrlEncode(pair.Key));
            builder.Append('=');
            builder.Append(WebUtility.UrlEncode(pair.Value));
        }
        return builder.ToString();
    }
}

internal static class RequestMatching
{
    public static bool IsDefaultMatch(
        HookReplayRequest request,
        CassetteRequest recorded,
        ISet<string> selectedHeaders)
    {
        if (!string.Equals(request.Method, recorded.Method, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(request.NormalizedUri, recorded.NormalizedUri, StringComparison.Ordinal) ||
            !string.Equals(request.BodyFingerprint, recorded.BodyFingerprint, StringComparison.Ordinal))
            return false;

        Dictionary<string, string> selected = request.Headers
            .Where(pair => selectedHeaders.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> recordedSelected = recorded.MatchHeaders
            .ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (selected.Count != recordedSelected.Count)
            return false;
        return selected.All(pair =>
            recordedSelected.TryGetValue(pair.Key, out string? value) &&
            string.Equals(pair.Value, value, StringComparison.Ordinal));
    }

    public static string DescribeNearestDifference(
        HookReplayRequest request,
        CassetteRequest recorded,
        ISet<string> selectedHeaders)
    {
        var differences = new List<string>();
        if (!string.Equals(request.Method, recorded.Method, StringComparison.OrdinalIgnoreCase))
            differences.Add("method");
        if (!string.Equals(request.NormalizedUri, recorded.NormalizedUri, StringComparison.Ordinal))
            differences.Add("normalized URI component");
        if (!string.Equals(request.BodyFingerprint, recorded.BodyFingerprint, StringComparison.Ordinal))
            differences.Add("body fingerprint");

        Dictionary<string, string> actual = request.Headers
            .Where(pair => selectedHeaders.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> expected = recorded.MatchHeaders
            .ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string name in selectedHeaders.OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (!actual.TryGetValue(name, out string? actualValue) ||
                !expected.TryGetValue(name, out string? expectedValue) ||
                !string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
                differences.Add("selected header '" + name + "'");
        }
        return differences.Count == 0 ? "custom matcher" : string.Join(", ", differences);
    }

    public static HookReplayRequest ToPublicRequest(CassetteRequest request)
    {
        Dictionary<string, string> headers = request.Headers
            .GroupBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.Join("\n", group.Select(header => header.Value)),
                StringComparer.OrdinalIgnoreCase);
        return new HookReplayRequest(
            request.Method,
            request.NormalizedUri,
            request.BodyFingerprint,
            headers);
    }
}

internal sealed class CassetteHeader
{
    public CassetteHeader(string name, string value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public string Value { get; }
}

internal static class CassetteFile
{
    private const long MaxCassetteBytes = 32L * 1024 * 1024;

    public static bool Exists(string path)
    {
        try
        {
            return File.Exists(Path.GetFullPath(path));
        }
        catch
        {
            return false;
        }
    }

    public static async Task<IReadOnlyList<CassetteInteraction>> ReadAsync(
        string path,
        int supportedVersion,
        CancellationToken cancellationToken)
    {
        string fullPath = GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new HookReplayIOException(
                "The cassette file does not exist: " + path,
                new FileNotFoundException("Cassette not found.", fullPath));

        try
        {
            var info = new FileInfo(fullPath);
            if (info.Length > MaxCassetteBytes)
                throw new HookReplaySizeLimitException("The cassette file exceeds the 32 MiB safety limit.");
            byte[] bytes = await Task.Run(() => File.ReadAllBytes(fullPath), cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument document = JsonDocument.Parse(bytes);
            JsonElement root = document.RootElement;
            int version = RequiredInt(root, "schemaVersion");
            if (version != supportedVersion)
                throw new HookReplayUnsupportedCassetteVersionException(
                    "Cassette schema version " + version +
                    " is not supported; this package supports version " + supportedVersion + ".");
            JsonElement entries = RequiredProperty(root, "interactions");
            if (entries.ValueKind != JsonValueKind.Array)
                throw new HookReplayMalformedCassetteException("Cassette interactions must be a JSON array.");

            var result = new List<CassetteInteraction>();
            foreach (JsonElement element in entries.EnumerateArray())
                result.Add(ParseInteraction(element));
            return result;
        }
        catch (HookReplayException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new HookReplayMalformedCassetteException(
                "The cassette is not valid HookReplay JSON.", exception);
        }
        catch (Exception exception)
        {
            throw new HookReplayIOException("The cassette could not be read.", exception);
        }
    }

    public static async Task WriteAsync(
        string path,
        int schemaVersion,
        IReadOnlyList<CassetteInteraction> interactions,
        CancellationToken cancellationToken)
    {
        string fullPath = GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new HookReplayIOException(
                "The cassette path has no writable directory.",
                new IOException("Invalid cassette directory."));

        string tempPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            byte[] bytes = Serialize(schemaVersion, interactions);
            await Task.Run(() => File.WriteAllBytes(tempPath, bytes), cancellationToken)
                .ConfigureAwait(false);
            if (File.Exists(fullPath))
            {
                try
                {
                    File.Replace(tempPath, fullPath, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(fullPath);
                    File.Move(tempPath, fullPath);
                }
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        catch (HookReplayException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new HookReplayIOException("The cassette could not be written.", exception);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }

    private static byte[] Serialize(
        int schemaVersion,
        IReadOnlyList<CassetteInteraction> interactions)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions
            {
                Indented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", schemaVersion);
            writer.WritePropertyName("interactions");
            writer.WriteStartArray();
            foreach (CassetteInteraction interaction in interactions)
            {
                writer.WriteStartObject();
                WriteRequest(writer, interaction.Request);
                WriteResponse(writer, interaction.Response);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        byte[] json = stream.ToArray();
        byte[] newline = { (byte)'\n' };
        return json.Concat(newline).ToArray();
    }

    private static void WriteRequest(Utf8JsonWriter writer, CassetteRequest request)
    {
        writer.WritePropertyName("request");
        writer.WriteStartObject();
        writer.WriteString("method", request.Method);
        writer.WriteString("normalizedUri", request.NormalizedUri);
        writer.WriteString("bodyFingerprint", request.BodyFingerprint);
        writer.WriteString("bodyContentType", request.BodyContentType);
        writer.WriteString("body", request.Body);
        WriteHeaders(writer, "headers", request.Headers);
        WriteHeaders(writer, "matchHeaders", request.MatchHeaders);
        writer.WriteEndObject();
    }

    private static void WriteResponse(Utf8JsonWriter writer, CassetteResponse response)
    {
        writer.WritePropertyName("response");
        writer.WriteStartObject();
        writer.WriteNumber("statusCode", response.StatusCode);
        writer.WriteString("reasonPhrase", response.ReasonPhrase);
        writer.WriteString("version", response.Version.ToString());
        WriteHeaders(writer, "headers", response.Headers);
        WriteHeaders(writer, "bodyHeaders", response.BodyHeaders);
        writer.WritePropertyName("body");
        if (response.Body is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteStartObject();
            writer.WriteString("contentType", response.Body.ContentType);
            writer.WriteString("text", response.Body.Text);
            writer.WriteEndObject();
        }
        writer.WriteEndObject();
    }

    private static void WriteHeaders(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<CassetteHeader> headers)
    {
        writer.WritePropertyName(propertyName);
        writer.WriteStartArray();
        foreach (CassetteHeader header in headers
            .OrderBy(header => header.Name, StringComparer.Ordinal)
            .ThenBy(header => header.Value, StringComparer.Ordinal))
        {
            writer.WriteStartObject();
            writer.WriteString("name", header.Name);
            writer.WriteString("value", header.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static CassetteInteraction ParseInteraction(JsonElement element)
    {
        JsonElement request = RequiredProperty(element, "request");
        JsonElement response = RequiredProperty(element, "response");
        return new CassetteInteraction
        {
            Request = new CassetteRequest
            {
                Method = RequiredString(request, "method"),
                NormalizedUri = RequiredString(request, "normalizedUri"),
                BodyFingerprint = OptionalString(request, "bodyFingerprint"),
                Body = OptionalString(request, "body"),
                BodyContentType = OptionalString(request, "bodyContentType"),
                Headers = ParseHeaders(request, "headers"),
                MatchHeaders = ParseHeaders(request, "matchHeaders")
            },
            Response = new CassetteResponse
            {
                StatusCode = RequiredInt(response, "statusCode"),
                ReasonPhrase = OptionalString(response, "reasonPhrase"),
                Version = ParseVersion(OptionalString(response, "version")),
                Headers = ParseHeaders(response, "headers"),
                BodyHeaders = ParseHeaders(response, "bodyHeaders"),
                Body = ParseBody(response)
            }
        };
    }

    private static CassetteBody? ParseBody(JsonElement response)
    {
        JsonElement body = RequiredProperty(response, "body");
        if (body.ValueKind == JsonValueKind.Null)
            return null;
        return new CassetteBody
        {
            ContentType = RequiredString(body, "contentType"),
            Text = RequiredString(body, "text")
        };
    }

    private static List<CassetteHeader> ParseHeaders(JsonElement parent, string name)
    {
        JsonElement headers = RequiredProperty(parent, name);
        if (headers.ValueKind != JsonValueKind.Array)
            throw new HookReplayMalformedCassetteException("Cassette property '" + name + "' must be an array.");
        return headers.EnumerateArray()
            .Select(header => new CassetteHeader(
                RequiredString(header, "name"),
                RequiredString(header, "value")))
            .ToList();
    }

    private static Version ParseVersion(string? value)
    {
        return Version.TryParse(value, out Version? version) ? version : HttpVersion.Version11;
    }

    private static JsonElement RequiredProperty(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object ||
            !parent.TryGetProperty(name, out JsonElement value))
            throw new HookReplayMalformedCassetteException(
                "Cassette property '" + name + "' is missing.");
        return value;
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name);
        if (value.ValueKind != JsonValueKind.String)
            throw new HookReplayMalformedCassetteException(
                "Cassette property '" + name + "' must be a string.");
        return value.GetString() ?? string.Empty;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name);
        return value.ValueKind == JsonValueKind.Null ? null : RequiredString(parent, name);
    }

    private static int RequiredInt(JsonElement parent, string name)
    {
        JsonElement value = RequiredProperty(parent, name);
        if (!value.TryGetInt32(out int result))
            throw new HookReplayMalformedCassetteException(
                "Cassette property '" + name + "' must be an integer.");
        return result;
    }

    private static string GetFullPath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception exception)
        {
            throw new HookReplayIOException("The cassette path is invalid.", exception);
        }
    }
}

internal static class TaskExtensions
{
    public static async Task WaitWithCancellationAsync(
        this Task task,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancellation = new TaskCompletionSource<bool>();
        using (cancellationToken.Register(
            state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellation))
        {
            Task completed = await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false);
            if (completed == cancellation.Task)
                throw new OperationCanceledException(cancellationToken);
            await task.ConfigureAwait(false);
        }
    }
}
