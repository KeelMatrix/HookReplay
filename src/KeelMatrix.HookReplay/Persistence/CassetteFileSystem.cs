namespace KeelMatrix.HookReplay;

internal static class CassetteFileSystem
{
    internal const long MaxCassetteBytes = 32L * 1024 * 1024;
    internal const string MaxCassetteSizeMessage =
        "The total cassette size exceeds the 32 MiB safety limit.";

    internal static bool Exists(string path)
    {
        try
        {
            return File.Exists(CassettePath.GetFullPath(path));
        }
        catch
        {
            return false;
        }
    }

    internal static FileStream OpenRead(string path)
    {
        return new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    internal static async Task WriteAsync(
        string path,
        int schemaVersion,
        IReadOnlyList<CassetteInteraction> interactions,
        CancellationToken cancellationToken)
    {
        string fullPath = CassettePath.GetFullPath(path);
        string? directory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(directory))
            throw new HookReplayIOException(
                "The cassette path has no writable directory.",
                new IOException("Invalid cassette directory."));

        string tempPath = Path.Combine(directory, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            CassettePath.EnsureNoReparsePoints(fullPath);
            byte[] bytes = CassetteWriter.Serialize(schemaVersion, interactions);
            if (bytes.LongLength > MaxCassetteBytes)
                throw new HookReplaySizeLimitException(MaxCassetteSizeMessage);
            await Task.Run(() => File.WriteAllBytes(tempPath, bytes), cancellationToken)
                .ConfigureAwait(false);
            if (File.Exists(fullPath))
            {
                try
                {
                    File.Replace(tempPath, fullPath, null);
                }
                catch (PlatformNotSupportedException)
                {
                    File.Delete(fullPath);
                    File.Move(tempPath, fullPath);
                }
            }
            else
            {
                File.Move(tempPath, fullPath);
            }
        }
        catch (HookReplayException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new HookReplayIOException("The cassette could not be written.", exception);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }
}
