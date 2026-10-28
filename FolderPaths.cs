using System.Text.RegularExpressions;

/// <summary>Pure validation rules for client-supplied folder paths, folder names and file names. Performs no IO.</summary>
static class FolderPaths
{
    private static readonly Regex FolderNamePattern = new(@"^[\p{L}\p{M}\p{N} ._-]+$", RegexOptions.CultureInvariant);

    private static readonly HashSet<char> InvalidChars = new(Path.GetInvalidFileNameChars().Concat(new[] { '\\', '/', ':' }));

    private static readonly HashSet<string> WindowsDeviceNames = new(
        new[] { "CON", "PRN", "AUX", "NUL", "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³" }
            .Concat(Enumerable.Range(0, 10).SelectMany(digit => new[] { $"COM{digit}", $"LPT{digit}" })),
        StringComparer.OrdinalIgnoreCase);

    /// <summary>Splits a '/'-separated path into segments whose first segment is normalized to its category key.</summary>
    public static string[] ParseRelativePath(string? path, IEnumerable<string> categoryKeys, int maxDepth)
    {
        if (string.IsNullOrEmpty(path))
        {
            throw new StorageException(400, "Path is required.");
        }

        if (path.Any(c => c != '/' && IsInvalidChar(c)))
        {
            throw new StorageException(400, "Path contains invalid characters.");
        }

        var segments = path.Split('/');
        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                throw new StorageException(400, "Path must not start or end with '/' or contain empty segments.");
            }

            if (segment is "." or "..")
            {
                throw new StorageException(400, "Path must not contain '.' or '..' segments.");
            }

            if (char.IsWhiteSpace(segment[0]) || char.IsWhiteSpace(segment[^1]) || segment[^1] == '.')
            {
                throw new StorageException(400, "Path segments must not start or end with a space or end with '.'.");
            }
        }

        if (segments.Length > maxDepth)
        {
            throw new StorageException(400, $"Path exceeds the maximum folder depth of {maxDepth}.");
        }

        var categoryKey = categoryKeys.FirstOrDefault(key => string.Equals(key, segments[0], StringComparison.OrdinalIgnoreCase));
        if (categoryKey is null)
        {
            throw new StorageException(400, "Unknown top-level folder.");
        }

        segments[0] = categoryKey;
        return segments;
    }

    /// <summary>Validates a name for a folder that is about to be created or renamed and returns it.</summary>
    public static string ValidateFolderName(string? name, int maxLength)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new StorageException(400, "Folder name is required.");
        }

        if (name.Length > maxLength)
        {
            throw new StorageException(400, $"Folder name must be at most {maxLength} characters.");
        }

        if (!FolderNamePattern.IsMatch(name))
        {
            throw new StorageException(400, "Folder name may only contain letters, digits, spaces, '.', '_' and '-'.");
        }

        if (name.All(c => c == '.'))
        {
            throw new StorageException(400, "Folder name must not consist only of dots.");
        }

        if (name[0] == ' ' || name[^1] == ' ')
        {
            throw new StorageException(400, "Folder name must not start or end with a space.");
        }

        if (name[^1] == '.')
        {
            throw new StorageException(400, "Folder name must not end with '.'.");
        }

        if (IsWindowsDeviceName(name))
        {
            throw new StorageException(400, "Folder name is reserved by Windows.");
        }

        return name;
    }

    /// <summary>Validates a single file name (no directory parts, no stream or device names) and returns it.</summary>
    public static string ValidateFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name is "." or ".."
            || Path.GetFileName(name) != name
            || name.Any(IsInvalidChar)
            || IsWindowsDeviceName(name))
        {
            throw new StorageException(400, "Invalid file name.");
        }

        return name;
    }

    private static bool IsInvalidChar(char c) => char.IsControl(c) || InvalidChars.Contains(c);

    private static bool IsWindowsDeviceName(string name)
    {
        var dotIndex = name.IndexOf('.');
        var baseName = dotIndex < 0 ? name : name[..dotIndex];
        return WindowsDeviceNames.Contains(baseName.TrimEnd());
    }
}
