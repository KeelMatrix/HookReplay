namespace KeelMatrix.HookReplay;

/// <summary>
/// A sanitized request description supplied to custom matchers.
/// </summary>
/// <remarks>
/// This type contains no raw request or response body and no raw sensitive header value.
/// </remarks>
public sealed class HookReplayRequest
{
    internal HookReplayRequest(
        string method,
        string normalizedUri,
        string? bodyFingerprint,
        IReadOnlyDictionary<string, string> headers)
    {
        Method = method;
        NormalizedUri = normalizedUri;
        BodyFingerprint = bodyFingerprint;
        Headers = headers;
    }

    /// <summary>Gets the HTTP method.</summary>
    public string Method { get; }

    /// <summary>Gets the canonical, sanitized URI used for matching.</summary>
    public string NormalizedUri { get; }

    /// <summary>Gets the SHA-256 fingerprint of a supported body, or <see langword="null"/>.</summary>
    public string? BodyFingerprint { get; }

    /// <summary>Gets sanitized request headers keyed by case-insensitive header name.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }
}
