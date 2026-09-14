using System.Security.Cryptography;

namespace KeelMatrix.HookReplay;

internal static class Sha256Compatibility
{
    public static byte[] HashData(byte[] data)
    {
#if NET8_0_OR_GREATER
        return SHA256.HashData(data);
#else
        using SHA256 sha = SHA256.Create();
        return sha.ComputeHash(data);
#endif
    }
}
