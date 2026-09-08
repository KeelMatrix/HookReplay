namespace KeelMatrix.HookReplay;

/// <summary>Creates ordinary <see cref="HttpClient"/> instances backed by HookReplay.</summary>
public static class HookReplayClient
{
    /// <summary>
    /// Creates a client using the supplied options and optional inner transport.
    /// </summary>
    /// <param name="options">The explicit record or replay configuration.</param>
    /// <param name="innerHandler">
    /// The transport used only in record mode. Replay never invokes this handler.
    /// </param>
    /// <returns>A disposable <see cref="HttpClient"/>.</returns>
    public static HttpClient Create(HookReplayOptions options, HttpMessageHandler? innerHandler = null)
    {
        if (options is null)
            throw new ArgumentNullException(nameof(options));
        innerHandler ??= new HttpClientHandler();
        return new HttpClient(new HookReplayHandler(options, innerHandler), disposeHandler: true);
    }
}
