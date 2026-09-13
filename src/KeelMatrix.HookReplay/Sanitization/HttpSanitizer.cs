using KeelMatrix.Redaction;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KeelMatrix.HookReplay;

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
                    SanitizeHeaderValue(pair.Key, value))),
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
        HttpHeaders? contentHeaders,
        bool dropBodyDependentHeaders = false)
    {
        var result = new List<CassetteHeader>();
        AddSanitizedHeaders(result, headers, dropBodyDependentHeaders);
        if (contentHeaders is not null)
            AddSanitizedHeaders(result, contentHeaders, dropBodyDependentHeaders);
        return result
            .OrderBy(header => header.Name, StringComparer.Ordinal)
            .ThenBy(header => header.Value, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Identifies headers whose value describes the exact body bytes or their transfer framing.
    /// </summary>
    /// <remarks>
    /// Persisted and replayed bodies are the sanitized representation, so a recorded length,
    /// integrity hash, or transfer-framing value is stale for the replayed message and must be
    /// neither persisted nor replayed alongside a changed body.
    /// </remarks>
    public static bool IsBodyDependentHeaderName(string name)
    {
        return name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-MD5", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Content-Digest", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Repr-Digest", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Digest", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase);
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
            catch (Exception)
            {
                throw new HookReplayUnsupportedContentException(
                    "A configured redactor failed; no cassette was written.");
            }
        }

        return sanitized;
    }

    public string SanitizeHeaderValue(string name, string value)
    {
        return IsSensitiveName(name) ? Redacted : ApplyTextRedactors(value);
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
            // Header maps feed matching identity, custom-matcher input, and cassette data, so a
            // value that describes the pre-sanitization body never enters them.
            if (IsBodyDependentHeaderName(pair.Key))
                continue;

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
        HttpHeaders source,
        bool dropBodyDependentHeaders)
    {
        foreach (KeyValuePair<string, IEnumerable<string>> pair in source)
        {
            if (dropBodyDependentHeaders && IsBodyDependentHeaderName(pair.Key))
                continue;

            foreach (string value in pair.Value)
            {
                destination.Add(new CassetteHeader(
                    pair.Key,
                    SanitizeHeaderValue(pair.Key, value)));
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
