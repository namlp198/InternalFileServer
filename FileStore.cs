/// <summary>Storage error carrying the HTTP status code and a client-safe message (never an absolute path).</summary>
sealed class StorageException : Exception
{
    public StorageException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}

/// <summary>Folder resolved from a client path: category key, normalized relative path and absolute location.</summary>
sealed record ResolvedFolder(string Category, string RelativePath, string FullPath);

/// <summary>File resolved from a client path and file name.</summary>
sealed record ResolvedFile(ResolvedFolder Folder, string Name, string FullPath);

/// <summary>The only place that maps client folder paths and file names to locations under the upload root.</summary>
sealed class FileStore
{
    private static readonly EnumerationOptions DirectFiles = new()
    {
        AttributesToSkip = FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    private readonly string uploadRoot;
    private readonly Dictionary<string, string> categoryNames;
    private readonly Dictionary<string, string> categoryRoots;

    public FileStore(string uploadRoot, IReadOnlyDictionary<string, string> categories, int maxDepth, int maxNameLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadRoot);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDepth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxNameLength, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxNameLength, 255);

        this.uploadRoot = Path.GetFullPath(uploadRoot);
        categoryNames = new Dictionary<string, string>(categories, StringComparer.OrdinalIgnoreCase);
        categoryRoots = categoryNames.Keys.ToDictionary(
            key => key,
            key => Path.GetFullPath(Path.Combine(this.uploadRoot, key)),
            StringComparer.OrdinalIgnoreCase);
        MaxDepth = maxDepth;
        MaxNameLength = maxNameLength;
    }

    public int MaxDepth { get; }

    public int MaxNameLength { get; }

    /// <summary>Creates the upload root and every top-level category folder.</summary>
    public void EnsureRoots()
    {
        Directory.CreateDirectory(uploadRoot);
        foreach (var categoryRoot in categoryRoots.Values)
        {
            Directory.CreateDirectory(categoryRoot);
        }
    }

    /// <summary>Resolves an existing folder; 400 for invalid paths or links, 404 when it does not exist.</summary>
    public ResolvedFolder ResolveFolder(string? path)
    {
        var segments = FolderPaths.ParseRelativePath(path, categoryNames.Keys, MaxDepth);
        var categoryRoot = categoryRoots[segments[0]];
        var rawPath = string.Join(Path.DirectorySeparatorChar, segments.Skip(1).Prepend(categoryRoot));
        var fullPath = Path.GetFullPath(rawPath);
        if (!string.Equals(fullPath, rawPath, StringComparison.Ordinal)
            || (segments.Length > 1 && !fullPath.StartsWith(categoryRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
        {
            throw new StorageException(400, "Invalid folder path.");
        }

        if (!Directory.Exists(categoryRoot))
        {
            throw new StorageException(404, "Folder not found.");
        }

        var current = categoryRoot;
        foreach (var segment in segments.Skip(1))
        {
            current = current + Path.DirectorySeparatorChar + segment;
            var directory = new DirectoryInfo(current);
            if (!directory.Exists)
            {
                throw new StorageException(404, "Folder not found.");
            }

            if (directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new StorageException(400, "Links are not allowed in folder paths.");
            }
        }

        return new ResolvedFolder(segments[0], string.Join('/', segments), fullPath);
    }

    /// <summary>Resolves a file inside an existing folder; 404 when <paramref name="mustExist"/> is set and the file is missing.</summary>
    public ResolvedFile ResolveFile(string? path, string? name, bool mustExist = true)
    {
        var folder = ResolveFolder(path);
        var fileName = FolderPaths.ValidateFileName(name);
        var rawPath = folder.FullPath + Path.DirectorySeparatorChar + fileName;
        var fullPath = Path.GetFullPath(rawPath);
        if (!string.Equals(fullPath, rawPath, StringComparison.Ordinal)
            || !fullPath.StartsWith(folder.FullPath + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new StorageException(400, "Invalid file path.");
        }

        if (Directory.Exists(fullPath))
        {
            throw mustExist
                ? new StorageException(404, "File not found.")
                : new StorageException(409, "A folder with the same name already exists.");
        }

        var file = new FileInfo(fullPath);
        if (file.Exists && file.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new StorageException(400, "Links are not allowed.");
        }

        if (mustExist && !file.Exists)
        {
            throw new StorageException(404, "File not found.");
        }

        return new ResolvedFile(folder, fileName, fullPath);
    }

    /// <summary>Lists the direct files of a folder (no subfolders, no links).</summary>
    public IReadOnlyList<FileItem> ListFiles(string? path)
    {
        var folder = ResolveFolder(path);
        return new DirectoryInfo(folder.FullPath)
            .EnumerateFiles("*", DirectFiles)
            .Select(info => ToFileItem(folder, info))
            .ToList();
    }

    /// <summary>Writes the content to a file in an existing folder, replacing a file with the same name.</summary>
    public async Task<FileItem> SaveFileAsync(string? path, string? name, Stream content, CancellationToken cancellationToken)
    {
        var file = ResolveFile(path, name, mustExist: false);
        await using (var target = new FileStream(file.FullPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(target, cancellationToken);
        }

        return ToFileItem(file.Folder, new FileInfo(file.FullPath));
    }

    public void DeleteFile(string? path, string? name)
    {
        File.Delete(ResolveFile(path, name).FullPath);
    }

    private FileItem ToFileItem(ResolvedFolder folder, FileInfo info)
    {
        return new FileItem(folder.Category, categoryNames[folder.Category], info.Name, info.Length, info.LastWriteTime, folder.RelativePath);
    }
}
