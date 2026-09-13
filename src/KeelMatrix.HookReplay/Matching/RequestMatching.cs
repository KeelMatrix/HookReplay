namespace KeelMatrix.HookReplay;

internal static class RequestMatching
{
    internal readonly struct Difference
    {
        public Difference(int mismatchCount, string description)
        {
            MismatchCount = mismatchCount;
            Description = description;
        }

        public int MismatchCount { get; }
        public string Description { get; }
        public bool IsMatch => MismatchCount == 0;
    }

    public static bool IsDefaultMatch(
        HookReplayRequest request,
        CassetteRequest recorded,
        ISet<string> selectedHeaders)
    {
        return DescribeDifference(request, recorded, selectedHeaders).IsMatch;
    }

    public static Difference DescribeDifference(
        HookReplayRequest request,
        CassetteRequest recorded,
        ISet<string> selectedHeaders)
    {
        var differences = new List<string>();
        if (!string.Equals(request.Method, recorded.Method, StringComparison.OrdinalIgnoreCase))
            differences.Add("method");
        if (!string.Equals(request.NormalizedUri, recorded.NormalizedUri, StringComparison.Ordinal))
            differences.Add("normalized URI component");
        if (!string.Equals(request.BodyFingerprint, recorded.BodyFingerprint, StringComparison.Ordinal))
            differences.Add("body fingerprint");

        Dictionary<string, string> actual = request.Headers
            .Where(pair => selectedHeaders.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> expected = recorded.MatchHeaders
            .ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (string name in actual.Keys
            .Concat(expected.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (!actual.TryGetValue(name, out string? actualValue) ||
                !expected.TryGetValue(name, out string? expectedValue) ||
                !string.Equals(actualValue, expectedValue, StringComparison.Ordinal))
                differences.Add("selected header '" + name + "'");
        }
        return new Difference(
            differences.Count,
            differences.Count == 0 ? "custom matcher" : string.Join(", ", differences));
    }

    public static HookReplayRequest ToPublicRequest(CassetteRequest request)
    {
        Dictionary<string, string> headers = request.Headers
            .GroupBy(header => header.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => string.Join("\n", group.Select(header => header.Value)),
                StringComparer.OrdinalIgnoreCase);
        foreach (CassetteHeader header in request.MatchHeaders)
            headers[header.Name] = header.Value;
        return new HookReplayRequest(
            request.Method,
            request.NormalizedUri,
            request.BodyFingerprint,
            headers);
    }
}
