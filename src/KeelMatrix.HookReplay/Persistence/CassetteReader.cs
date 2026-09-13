using System.Text.Json;

namespace KeelMatrix.HookReplay;

internal static class CassetteReader
{
    internal static async Task<IReadOnlyList<CassetteInteraction>> ReadAsync(
        string path,
        int supportedVersion,
        HttpSanitizer sanitizer,
        CancellationToken cancellationToken,
        Func<string, Stream>? openRead = null)
    {
        string fullPath = CassettePath.GetFullPath(path);
        if (!File.Exists(fullPath))
            throw new HookReplayIOException(
                "The cassette file does not exist: " + path,
                new FileNotFoundException("Cassette not found.", fullPath));

        try
        {
            using Stream source = (openRead ?? CassetteFileSystem.OpenRead)(fullPath);
            if (source.Length > CassetteFileSystem.MaxCassetteBytes)
                throw new HookReplaySizeLimitException(CassetteFileSystem.MaxCassetteSizeMessage);

            using var bounded = new LimitedMemoryStream(
                (int)CassetteFileSystem.MaxCassetteBytes,
                CassetteFileSystem.MaxCassetteSizeMessage);
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

            if (source.Length > CassetteFileSystem.MaxCassetteBytes)
                throw new HookReplaySizeLimitException(CassetteFileSystem.MaxCassetteSizeMessage);

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
        CassetteValidator.ValidateRequestContent(interaction.Request);
        CassetteValidator.ValidateResponseContent(interaction.Response);
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
}
