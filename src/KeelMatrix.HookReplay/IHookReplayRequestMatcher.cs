namespace KeelMatrix.HookReplay;

/// <summary>
/// Adds a bounded, sanitized predicate to the default HookReplay request matching rules.
/// </summary>
public interface IHookReplayRequestMatcher
{
    /// <summary>
    /// Determines whether an incoming request and a recorded request are an application-level match.
    /// </summary>
    /// <param name="request">The sanitized incoming request.</param>
    /// <param name="recordedRequest">The sanitized recorded request.</param>
    /// <returns><see langword="true"/> when the custom predicate accepts the pair.</returns>
    bool Matches(HookReplayRequest request, HookReplayRequest recordedRequest);
}
