namespace KeelMatrix.HookReplay;

/// <summary>Controls whether HookReplay sends requests or serves persisted responses.</summary>
public enum HookReplayMode
{
    /// <summary>Send requests through the configured inner transport and persist successful exchanges.</summary>
    Record = 0,

    /// <summary>Serve responses from the cassette without invoking the inner transport.</summary>
    Replay = 1
}
