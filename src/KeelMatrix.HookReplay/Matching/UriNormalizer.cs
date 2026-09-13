using System.Text;

namespace KeelMatrix.HookReplay;

internal static class UriNormalizer
{
    private static readonly char[] QuerySeparators = { '&' };
    private static readonly char[] SegmentSeparators = { '/' };

    public static string Normalize(Uri? uri, HttpSanitizer sanitizer)
    {
        if (uri is null)
            throw new HookReplayMismatchException("The HTTP request has no URI.");

        string scheme = uri.Scheme.ToLowerInvariant();
        string host = uri.Host.ToLowerInvariant();
        string port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string path = NormalizePath(
            uri.GetComponents(UriComponents.Path, UriFormat.UriEscaped),
            sanitizer);

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
            string name = Uri.UnescapeDataString(equals < 0 ? part : part.Substring(0, equals));
            string value = Uri.UnescapeDataString(equals < 0 ? string.Empty : part.Substring(equals + 1));
            string normalizedValue = HttpSanitizer.IsSensitiveName(name)
                ? "[REDACTED]"
                : sanitizer.ApplyTextRedactors(value);
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
            builder.Append(Uri.EscapeDataString(pair.Key));
            builder.Append('=');
            builder.Append(Uri.EscapeDataString(pair.Value));
        }
        return builder.ToString();
    }

    /// <summary>
    /// Sanitizes each path segment before the URI becomes matching identity or cassette data.
    /// </summary>
    /// <remarks>
    /// Segments are decoded before redaction so a percent-encoded protected value cannot
    /// hide from the redaction boundary, then re-encoded with the canonical escape used for
    /// query values so equivalent raw and escaped inputs normalize identically.
    /// </remarks>
    private static string NormalizePath(string escapedPath, HttpSanitizer sanitizer)
    {
        if (escapedPath.Length == 0)
            return "/";

        string[] segments = escapedPath.Split(SegmentSeparators);
        var builder = new StringBuilder(escapedPath.Length);
        for (int index = 0; index < segments.Length; index++)
        {
            if (index > 0)
                builder.Append('/');

            if (segments[index].Length == 0)
                continue;

            string decoded = Uri.UnescapeDataString(segments[index]);
            builder.Append(Uri.EscapeDataString(sanitizer.ApplyTextRedactors(decoded)));
        }

        if (builder.Length == 0)
            return "/";

        if (builder[0] != '/')
            builder.Insert(0, '/');

        return builder.ToString();
    }

    public static string NormalizePersisted(string value, HttpSanitizer sanitizer)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
            throw new HookReplayMalformedCassetteException(
                "Cassette property 'normalizedUri' must be an absolute URI.");

        return Normalize(uri, sanitizer);
    }
}
