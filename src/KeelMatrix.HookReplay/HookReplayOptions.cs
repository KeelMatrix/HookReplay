using KeelMatrix.Redaction;

namespace KeelMatrix.HookReplay;

/// <summary>Configures a HookReplay handler.</summary>
public sealed class HookReplayOptions
{
    /// <summary>Initializes options for the specified cassette path.</summary>
    /// <param name="cassettePath">A relative or absolute path selected by the caller.</param>
    public HookReplayOptions(string cassettePath)
    {
        if (string.IsNullOrWhiteSpace(cassettePath))
            throw new ArgumentException("A cassette path is required.", nameof(cassettePath));

        CassettePath = cassettePath;
    }

    /// <summary>Gets the caller-selected cassette path.</summary>
    public string CassettePath { get; }

    /// <summary>Gets or sets the explicit network mode. The safe default is replay.</summary>
    public HookReplayMode Mode { get; set; } = HookReplayMode.Replay;

    /// <summary>Gets or sets the maximum number of bytes buffered for each request or response body.</summary>
    public int MaxBodyBytes { get; set; } = 1024 * 1024;

    /// <summary>
    /// Gets the case-insensitive header names that participate in matching.
    /// Headers are ignored by default.
    /// </summary>
    public ISet<string> MatchHeaders { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Gets custom deterministic redactors applied after structural HTTP protection.
    /// Built-in redaction is always enabled.
    /// </summary>
    public IList<ITextRedactor> Redactors { get; } = new List<ITextRedactor>();

    /// <summary>Gets or sets an optional sanitized custom matcher.</summary>
    public IHookReplayRequestMatcher? RequestMatcher { get; set; }
}
