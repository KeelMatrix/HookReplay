namespace KeelMatrix.HookReplay;

internal static class StringCompatibility
{
    public static bool ContainsOrdinal(string value, string substring)
    {
#if NET8_0_OR_GREATER
        return value.Contains(substring, StringComparison.Ordinal);
#else
        return value.IndexOf(substring, StringComparison.Ordinal) >= 0;
#endif
    }

    public static bool Contains(string value, char character)
    {
#if NET8_0_OR_GREATER
        return value.Contains(character);
#else
        return value.IndexOf(character) >= 0;
#endif
    }
}
