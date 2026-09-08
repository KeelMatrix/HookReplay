namespace KeelMatrix.HookReplay;

/// <summary>Base exception for HookReplay failures.</summary>
public class HookReplayException : Exception
{
    /// <summary>Initializes an exception with an actionable message.</summary>
    public HookReplayException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes an exception with an actionable message and an underlying cause.</summary>
    public HookReplayException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Indicates that a cassette is not valid HookReplay JSON.</summary>
public sealed class HookReplayMalformedCassetteException : HookReplayException
{
    internal HookReplayMalformedCassetteException(string message, Exception? innerException = null)
        : base(message, innerException!)
    {
    }
}

/// <summary>Indicates that a cassette schema version is newer than this package supports.</summary>
public sealed class HookReplayUnsupportedCassetteVersionException : HookReplayException
{
    internal HookReplayUnsupportedCassetteVersionException(string message)
        : base(message)
    {
    }
}

/// <summary>Indicates that no unconsumed cassette entry matched a replay request.</summary>
public sealed class HookReplayMismatchException : HookReplayException
{
    internal HookReplayMismatchException(string message)
        : base(message)
    {
    }
}

/// <summary>Indicates that a request or response content type is not supported.</summary>
public sealed class HookReplayUnsupportedContentException : HookReplayException
{
    internal HookReplayUnsupportedContentException(string message, Exception? innerException = null)
        : base(message, innerException!)
    {
    }
}

/// <summary>Indicates that a request or response body exceeded the configured bound.</summary>
public sealed class HookReplaySizeLimitException : HookReplayException
{
    internal HookReplaySizeLimitException(string message)
        : base(message)
    {
    }
}

/// <summary>Indicates that a cassette could not be read or written.</summary>
public sealed class HookReplayIOException : HookReplayException
{
    internal HookReplayIOException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
