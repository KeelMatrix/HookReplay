using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace KeelMatrix.HookReplay;

internal static class CassetteFile
{
    internal const long MaxCassetteBytes = 32L * 1024 * 1024;
    private const string MaxCassetteSizeMessage =
        "The total cassette size exceeds the 32 MiB safety limit.";

    public static void ValidatePath(string path)
    {
        _ = GetFullPath(path);
    }

    public static bool Exists(string path)
    {
        try
        {
            return File.Exists(GetFullPath(path));
        }
        catch
        {
            return false;
        }
    }

    public static async Task<IReadOnlyList<CassetteInteraction>> ReadAsync(
        string path,
        int supportedVersion,
        HttpSanitizer sanitizer,
        CancellationToken cancellationToken,
        Func<string, Stream>? openRead = null)
    {
        string fullPath = GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new HookReplayIOException(
                "The cassette file does not exist: " + path,
                new FileNotFoundException("Cassette not found.", fullPath));

        try
        {
            using Stream source = (openRead ?? OpenRead)(fullPath);
            if (source.Length > MaxCassetteBytes)
                throw new HookReplaySizeLimitException(MaxCassetteSizeMessage);

            using var bounded = new LimitedMemoryStream(
                (int)MaxCassetteBytes,
                MaxCassetteSizeMessage);
            byte[] buffer = new byte[81920];
            while (true)
            {
#if NET8_0_OR_GREATER
                int read = await source.ReadAsync(buffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
#else
                int read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                    .ConfigureAwait(false);
#endif
                if (read == 0)
                    break;
                bounded.Write(buffer, 0, read);
            }

            if (source.Length > MaxCassetteBytes)
                throw new HookReplaySizeLimitException(MaxCassetteSizeMessage);

            byte[] bytes = bounded.ToArray();
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
                result.Add(ParseInteraction(element, sanitizer));
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

    private static FileStream OpenRead(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
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
            EnsureNoReparsePoints(fullPath);
            byte[] bytes = Serialize(schemaVersion, interactions);
            if (bytes.LongLength > MaxCassetteBytes)
                throw new HookReplaySizeLimitException(MaxCassetteSizeMessage);
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
        using var stream = new LimitedMemoryStream(
            (int)MaxCassetteBytes,
            MaxCassetteSizeMessage);
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
        stream.WriteByte((byte)'\n');
        return stream.ToArray();
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

    private static CassetteInteraction ParseInteraction(
        JsonElement element,
        HttpSanitizer sanitizer)
    {
        JsonElement request = RequiredProperty(element, "request");
        JsonElement response = RequiredProperty(element, "response");
        var interaction = new CassetteInteraction
        {
            Request = new CassetteRequest
            {
                Method = RequiredString(request, "method"),
                NormalizedUri = UriNormalizer.NormalizePersisted(
                    RequiredString(request, "normalizedUri"),
                    sanitizer),
                BodyFingerprint = OptionalString(request, "bodyFingerprint"),
                Body = OptionalString(request, "body"),
                BodyContentType = OptionalString(request, "bodyContentType"),
                Headers = ParseHeaders(request, "headers"),
                MatchHeaders = ParseHeaders(request, "matchHeaders")
            },
            Response = new CassetteResponse
            {
                StatusCode = ParseStatusCode(response),
                ReasonPhrase = OptionalString(response, "reasonPhrase"),
                Version = ParseVersion(RequiredString(response, "version")),
                Headers = ParseHeaders(response, "headers"),
                BodyHeaders = ParseHeaders(response, "bodyHeaders"),
                Body = ParseBody(response)
            }
        };
        ValidateRequestContent(interaction.Request);
        ValidateResponseContent(interaction.Response);
        return interaction;
    }

    private static CassetteBody? ParseBody(JsonElement response)
    {
        JsonElement body = RequiredProperty(response, "body");
        if (body.ValueKind == JsonValueKind.Null)
            return null;
        return new CassetteBody
        {
            ContentType = OptionalString(body, "contentType"),
            Text = RequiredString(body, "text")
        };
    }

    private static void ValidateRequestContent(CassetteRequest request)
    {
        MediaTypeHeaderValue? bodyContentType = ParsePersistedContentType(
            request.BodyContentType,
            "request body content type",
            "request");
        MediaTypeHeaderValue? headerContentType = ParseContentTypeHeader(
            request.Headers,
            "request",
            "request Content-Type header");
        ValidateContentTypePair(bodyContentType, headerContentType, "request body");
        ValidateMatchHeaders(request);

        if (request.Body is null)
        {
            if (request.BodyFingerprint is not null)
            {
                throw new HookReplayMalformedCassetteException(
                    "Cassette request body fingerprint requires a request body.");
            }

            if (bodyContentType is not null)
            {
                throw new HookReplayMalformedCassetteException(
                    "Cassette request body content type requires a request body.");
            }

            return;
        }

        if (request.BodyFingerprint is null)
        {
            throw new HookReplayMalformedCassetteException(
                "Cassette request body requires a body fingerprint.");
        }

        if (!IsFingerprint(request.BodyFingerprint))
        {
            throw new HookReplayMalformedCassetteException(
                "Cassette request body fingerprint is not a valid SHA-256 fingerprint.");
        }

        if (!string.Equals(
                request.BodyFingerprint,
                BodyCapture.ComputeFingerprint(request.Body),
                StringComparison.Ordinal))
        {
            throw new HookReplayMalformedCassetteException(
                "Cassette request body fingerprint does not match the persisted body.");
        }

        if (bodyContentType is not null)
        {
            ValidatePersistedBodyText(request.Body, bodyContentType.MediaType!, "request");
            ValidatePersistedTextEncoding(request.Body, bodyContentType, "request");
        }
    }

    private static void ValidateResponseContent(CassetteResponse response)
    {
        MediaTypeHeaderValue? bodyContentType = ParsePersistedContentType(
            response.Body?.ContentType,
            "response body content type",
            "response");
        MediaTypeHeaderValue? headerContentType = ParseContentTypeHeader(
            response.BodyHeaders,
            "response body",
            "response Content-Type header");
        ValidateContentTypePair(bodyContentType, headerContentType, "response body");

        if (response.Body is null || bodyContentType is null)
        {
            return;
        }

        ValidatePersistedBodyText(response.Body.Text, bodyContentType.MediaType!, "response");
        ValidatePersistedTextEncoding(response.Body.Text, bodyContentType, "response");
    }

    private static MediaTypeHeaderValue? ParseContentTypeHeader(
        IEnumerable<CassetteHeader> headers,
        string owner,
        string propertyName)
    {
        CassetteHeader[] contentTypeHeaders = headers
            .Where(header => string.Equals(header.Name, "Content-Type", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (contentTypeHeaders.Length > 1)
        {
            throw new HookReplayMalformedCassetteException(
                "Cassette " + owner + " headers must contain at most one Content-Type header.");
        }

        return contentTypeHeaders.Length == 0
            ? null
            : ParsePersistedContentType(contentTypeHeaders[0].Value, propertyName, owner);
    }

    private static void ValidateContentTypePair(
        MediaTypeHeaderValue? bodyContentType,
        MediaTypeHeaderValue? headerContentType,
        string owner)
    {
        if (bodyContentType is not null &&
            headerContentType is not null &&
            !string.Equals(
                bodyContentType.ToString(),
                headerContentType.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new HookReplayMalformedCassetteException(
                "Cassette " + owner + " content type does not match its Content-Type header.");
        }
    }

    private static void ValidateMatchHeaders(CassetteRequest request)
    {
        var headerNames = new HashSet<string>(
            request.Headers.Select(header => header.Name),
            StringComparer.OrdinalIgnoreCase);
        HashSet<string> matchHeaderNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (CassetteHeader header in request.MatchHeaders)
        {
            if (!matchHeaderNames.Add(header.Name))
            {
                throw new HookReplayMalformedCassetteException(
                    "Cassette request match headers must contain at most one entry per header name.");
            }

            if (!headerNames.Contains(header.Name))
            {
                throw new HookReplayMalformedCassetteException(
                    "Cassette request match headers must refer to persisted request headers.");
            }
        }
    }

    private static void ValidatePersistedBodyText(string text, string mediaType, string owner)
    {
        if (!mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using JsonDocument _ = JsonDocument.Parse(text);
        }
        catch (JsonException exception)
        {
            throw new HookReplayUnsupportedContentException(
                "The cassette " + owner + " body declares JSON but is not valid JSON.", exception);
        }
    }

    private static MediaTypeHeaderValue? ParsePersistedContentType(
        string? value,
        string propertyName,
        string owner)
    {
        if (value is null)
            return null;

        if (!MediaTypeHeaderValue.TryParse(value, out MediaTypeHeaderValue? contentType) ||
            contentType is null ||
            string.IsNullOrWhiteSpace(contentType.MediaType))
        {
            throw new HookReplayMalformedCassetteException(
                "Cassette " + propertyName + " is not a valid media type.");
        }

        if (!HttpSanitizer.IsSupportedMediaType(contentType.MediaType))
        {
            throw new HookReplayUnsupportedContentException(
                "Cassette " + owner + " content type '" + contentType.MediaType +
                "' is not supported. HookReplay supports text, JSON, form, and empty content.");
        }

        _ = DeclaredTextEncoding.Resolve(contentType.CharSet);
        return contentType;
    }

    private static void ValidatePersistedTextEncoding(
        string text,
        MediaTypeHeaderValue contentType,
        string owner)
    {
        DeclaredTextEncoding.EnsureEncodable(
            DeclaredTextEncoding.Resolve(contentType.CharSet),
            text,
            "The cassette " + owner + " body text");
    }

    private static bool IsFingerprint(string value)
    {
        return value.Length == 64 && value.All(IsLowerHexDigit);
    }

    private static bool IsLowerHexDigit(char value)
    {
        return (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f');
    }

    private static List<CassetteHeader> ParseHeaders(JsonElement parent, string name)
    {
        JsonElement headers = RequiredProperty(parent, name);
        if (headers.ValueKind != JsonValueKind.Array)
            throw new HookReplayMalformedCassetteException("Cassette property '" + name + "' must be an array.");
        var result = new List<CassetteHeader>();
        foreach (JsonElement header in headers.EnumerateArray())
        {
            string headerName = RequiredString(header, "name");
            string headerValue = RequiredString(header, "value");
            if (string.IsNullOrWhiteSpace(headerName) ||
                headerName.IndexOf('\r') >= 0 ||
                headerName.IndexOf('\n') >= 0 ||
                headerValue.IndexOf('\r') >= 0 ||
                headerValue.IndexOf('\n') >= 0)
            {
                throw new HookReplayMalformedCassetteException(
                    "Cassette headers must contain valid names and values without line breaks.");
            }

            result.Add(new CassetteHeader(headerName, headerValue));
        }

        return result;
    }

    private static Version ParseVersion(string value)
    {
        if (!Version.TryParse(value, out Version? version) ||
            version is null ||
            (version != new Version(1, 0) &&
             version != new Version(1, 1) &&
             version != new Version(2, 0) &&
             version != new Version(3, 0)))
        {
            throw new HookReplayMalformedCassetteException(
                "Cassette property 'version' must be a supported HTTP version (1.0, 1.1, 2.0, or 3.0).");
        }

        return version;
    }

    private static int ParseStatusCode(JsonElement response)
    {
        int statusCode = RequiredInt(response, "statusCode");
        if (statusCode < 100 || statusCode > 599)
            throw new HookReplayMalformedCassetteException(
                "Cassette property 'statusCode' must be an ordinary HTTP status code from 100 through 599.");

        return statusCode;
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
            if (string.IsNullOrWhiteSpace(path))
                throw new HookReplayIOException(
                    "The cassette path is invalid.",
                    new ArgumentException("A cassette path is required.", nameof(path)));

            if (ContainsParentTraversal(path))
                throw new HookReplayIOException(
                    "The cassette path must not contain parent-directory traversal.",
                    new ArgumentException("Parent-directory traversal is not allowed.", nameof(path)));

            string fullPath = Path.GetFullPath(path);
            if (string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
                throw new HookReplayIOException(
                    "The cassette path must name a file.",
                    new ArgumentException("The cassette path must name a file.", nameof(path)));

            EnsureNoReparsePoints(fullPath);
            return fullPath;
        }
        catch (HookReplayException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new HookReplayIOException("The cassette path is invalid.", exception);
        }
    }

    private static bool ContainsParentTraversal(string path)
    {
        char[] separators = Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar
            ? new[] { Path.DirectorySeparatorChar }
            : new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        return path.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal));
    }

    private static void EnsureNoReparsePoints(string fullPath)
    {
        RejectReparsePoint(fullPath);

        string? directory = Path.GetDirectoryName(fullPath);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        while (!string.IsNullOrWhiteSpace(directory) && !SamePath(directory, root))
        {
            RejectReparsePoint(directory);
            string? parent = Path.GetDirectoryName(directory);
            if (parent is null || SamePath(parent, directory))
                break;
            directory = parent;
        }
    }

    private static void RejectReparsePoint(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new HookReplayIOException(
                "Cassette paths through symbolic links or reparse points are not supported.",
                new IOException("The cassette path contains a reparse point."));
    }

    private static bool SamePath(string left, string right)
    {
        StringComparison comparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }
}
