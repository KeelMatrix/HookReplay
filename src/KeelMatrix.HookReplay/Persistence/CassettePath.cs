namespace KeelMatrix.HookReplay;

internal static class CassettePath
{
    internal static void ValidatePath(string path)
    {
        _ = GetFullPath(path);
    }

    internal static string GetFullPath(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new HookReplayIOException(
                    "The cassette path is invalid.",
                    new ArgumentException("A cassette path is required.", nameof(path)));

            if (ContainsParentTraversal(path))
                throw new HookReplayIOException(
                    "The cassette path must not contain parent-directory traversal.",
                    new ArgumentException("Parent-directory traversal is not allowed.", nameof(path)));

            string fullPath = Path.GetFullPath(path);
            if (string.IsNullOrWhiteSpace(Path.GetFileName(fullPath)))
                throw new HookReplayIOException(
                    "The cassette path must name a file.",
                    new ArgumentException("The cassette path must name a file.", nameof(path)));

            EnsureNoReparsePoints(fullPath);
            return fullPath;
        }
        catch (HookReplayException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new HookReplayIOException("The cassette path is invalid.", exception);
        }
    }

    private static bool ContainsParentTraversal(string path)
    {
        char[] separators = Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar
            ? new[] { Path.DirectorySeparatorChar }
            : new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
        return path.Split(separators, StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => string.Equals(segment, "..", StringComparison.Ordinal));
    }

    internal static void EnsureNoReparsePoints(string fullPath)
    {
        RejectReparsePoint(fullPath);

        string? directory = Path.GetDirectoryName(fullPath);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        while (!string.IsNullOrWhiteSpace(directory) && !SamePath(directory, root))
        {
            RejectReparsePoint(directory);
            string? parent = Path.GetDirectoryName(directory);
            if (parent is null || SamePath(parent, directory))
                break;
            directory = parent;
        }
    }

    private static void RejectReparsePoint(string path)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return;
        }
        catch (DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
            throw new HookReplayIOException(
                "Cassette paths through symbolic links or reparse points are not supported.",
                new IOException("The cassette path contains a reparse point."));
    }

    private static bool SamePath(string left, string right)
    {
        StringComparison comparison = Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(left, right, comparison);
    }
}
