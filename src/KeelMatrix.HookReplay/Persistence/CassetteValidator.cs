using System.Net.Http.Headers;
using System.Text.Json;

namespace KeelMatrix.HookReplay;

internal static class CassetteValidator
{
    internal static void ValidateRequestContent(CassetteRequest request)
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

    internal static void ValidateResponseContent(CassetteResponse response)
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
}
