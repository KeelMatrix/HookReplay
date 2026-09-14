namespace KeelMatrix.HookReplay;

internal static class ArgumentNullExceptionCompatibility
{
    public static void ThrowIfNull<T>(T? argument, string paramName)
        where T : class
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(argument, paramName);
#else
        if (argument is null)
            throw new ArgumentNullException(paramName);
#endif
    }
}
