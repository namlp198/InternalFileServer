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

    private static readonly EnumerationOptions AllDirectEntries = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = false
    };

    private static readonly EnumerationOptions AllNestedEntries = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = true,
        RecurseSubdirectories = true
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
            .Select(info => ToFileItem(folder.Category, folder.RelativePath, info))
            .ToList();
    }

    /// <summary>Returns one node per top-level folder with its nested folders, skipping links and folders past MaxDepth.</summary>
    public IReadOnlyList<FolderNode> GetTree(CancellationToken cancellationToken)
    {
        return categoryRoots
            .Select(root => Walk(root.Value, root.Key, cancellationToken).Root.ToNode())
            .ToList();
    }

    /// <summary>Totals over every top-level folder including subfolders, plus the 10 most recent and 10 largest files.</summary>
    public StatsResult GetStats(CancellationToken cancellationToken)
    {
        var roots = new List<RootStats>();
        var files = new List<FileItem>();
        var totalFolders = 0;
        foreach (var (category, categoryRoot) in categoryRoots)
        {
            var walk = Walk(categoryRoot, category, cancellationToken);
            var rootFiles = walk.Files.Select(file => ToFileItem(category, file.FolderPath, file.Info)).ToList();
            roots.Add(new RootStats(category, categoryNames[category], rootFiles.Count, rootFiles.Sum(file => file.Size)));
            totalFolders += walk.SubfolderCount;
            files.AddRange(rootFiles);
        }

        return new StatsResult(
            files.Count,
            files.Sum(file => file.Size),
            totalFolders,
            roots,
            files.OrderByDescending(file => file.Modified).Take(10).ToList(),
            files.OrderByDescending(file => file.Size).Take(10).ToList());
    }

    /// <summary>Creates a subfolder; 400 for an invalid name or depth, 404 for a missing parent, 409 for a duplicate sibling.</summary>
    public FolderNode CreateFolder(string? parentPath, string? name)
    {
        var folderName = FolderPaths.ValidateFolderName(name, MaxNameLength);
        var parentSegments = FolderPaths.ParseRelativePath(parentPath, categoryNames.Keys, MaxDepth);
        if (parentSegments.Length >= MaxDepth)
        {
            throw new StorageException(400, $"Folders cannot be nested deeper than {MaxDepth} levels.");
        }

        var parent = ResolveFolder(parentPath);
        if (ListEntryNames(parent.FullPath).Any(entry => string.Equals(entry, folderName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new StorageException(409, "A file or folder with the same name already exists.");
        }

        Directory.CreateDirectory(ChildPath(parent.FullPath, folderName));
        return new FolderNode(folderName, $"{parent.RelativePath}/{folderName}", 0, Array.Empty<FolderNode>());
    }

    /// <summary>Renames a folder below a top-level folder; 400 for top-level folders, 409 for a duplicate sibling.</summary>
    public FolderNode RenameFolder(string? path, string? newName)
    {
        var folderName = FolderPaths.ValidateFolderName(newName, MaxNameLength);
        var segments = FolderPaths.ParseRelativePath(path, categoryNames.Keys, MaxDepth);
        if (segments.Length == 1)
        {
            throw new StorageException(400, "Top-level folders cannot be renamed.");
        }

        var folder = ResolveFolder(path);
        var parentFullPath = Path.GetDirectoryName(folder.FullPath)!;
        var parentRelativePath = string.Join('/', folder.RelativePath.Split('/')[..^1]);
        var siblings = ListEntryNames(parentFullPath);

        // On a case-insensitive disk the requested casing may differ from the stored one.
        var currentName = siblings.FirstOrDefault(entry => string.Equals(entry, segments[^1], StringComparison.Ordinal))
            ?? siblings.FirstOrDefault(entry => string.Equals(entry, segments[^1], StringComparison.OrdinalIgnoreCase))
            ?? segments[^1];

        if (!string.Equals(currentName, folderName, StringComparison.Ordinal))
        {
            if (siblings.Any(entry => !string.Equals(entry, currentName, StringComparison.Ordinal)
                && string.Equals(entry, folderName, StringComparison.OrdinalIgnoreCase)))
            {
                throw new StorageException(409, "A file or folder with the same name already exists.");
            }

            var source = ChildPath(parentFullPath, currentName);
            var target = ChildPath(parentFullPath, folderName);
            if (string.Equals(currentName, folderName, StringComparison.OrdinalIgnoreCase))
            {
                MoveThroughTemporaryName(source, target);
            }
            else
            {
                Directory.Move(source, target);
            }
        }

        return Walk(ChildPath(parentFullPath, folderName), $"{parentRelativePath}/{folderName}", CancellationToken.None).Root.ToNode();
    }

    /// <summary>Deletes a folder below a top-level folder; 409 when it is not empty and <paramref name="recursive"/> is false.</summary>
    public void DeleteFolder(string? path, bool recursive)
    {
        var segments = FolderPaths.ParseRelativePath(path, categoryNames.Keys, MaxDepth);
        if (segments.Length == 1)
        {
            throw new StorageException(400, "Top-level folders cannot be deleted.");
        }

        var folder = ResolveFolder(path);
        var directory = new DirectoryInfo(folder.FullPath);
        if (!recursive && directory.EnumerateFileSystemInfos("*", AllDirectEntries).Any())
        {
            throw new StorageException(409, "Folder is not empty.");
        }

        if (directory.EnumerateFileSystemInfos("*", AllNestedEntries).Any(entry => entry.Attributes.HasFlag(FileAttributes.ReparsePoint)))
        {
            throw new StorageException(400, "Folders containing links cannot be deleted.");
        }

        Directory.Delete(folder.FullPath, true);
    }

    /// <summary>Walks a folder once, skipping links and stopping at MaxDepth.</summary>
    private FolderWalk Walk(string fullPath, string relativePath, CancellationToken cancellationToken)
    {
        var depth = relativePath.Split('/').Length;
        var root = new FolderBuilder(relativePath[(relativePath.LastIndexOf('/') + 1)..], relativePath, depth);
        var files = new List<(string FolderPath, FileInfo Info)>();
        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists)
        {
            return new FolderWalk(root, 0, files);
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.ReparsePoint,
            MaxRecursionDepth = MaxDepth - depth
        };
        var folders = new Dictionary<string, FolderBuilder>(StringComparer.Ordinal) { [directory.FullName] = root };
        foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // A folder is yielded before its contents, so an unknown parent means the parent was skipped.
            if (!folders.TryGetValue(Path.GetDirectoryName(entry.FullName)!, out var parent))
            {
                continue;
            }

            if (entry is FileInfo file)
            {
                parent.FileCount++;
                files.Add((parent.Path, file));
            }
            else if (parent.Depth < MaxDepth)
            {
                var child = new FolderBuilder(entry.Name, $"{parent.Path}/{entry.Name}", parent.Depth + 1);
                parent.Children.Add(child);
                folders.Add(entry.FullName, child);
            }
        }

        return new FolderWalk(root, folders.Count - 1, files);
    }

    private static List<string> ListEntryNames(string folderFullPath)
    {
        return new DirectoryInfo(folderFullPath)
            .EnumerateFileSystemInfos("*", AllDirectEntries)
            .Select(entry => entry.Name)
            .ToList();
    }

    private static string ChildPath(string folderFullPath, string name)
    {
        var rawPath = folderFullPath + Path.DirectorySeparatorChar + name;
        if (!string.Equals(Path.GetFullPath(rawPath), rawPath, StringComparison.Ordinal))
        {
            throw new StorageException(400, "Invalid folder name.");
        }

        return rawPath;
    }

    /// <summary>Renames a folder in two steps so a case-only rename also works on case-insensitive disks.</summary>
    private static void MoveThroughTemporaryName(string source, string target)
    {
        var temporary = ChildPath(Path.GetDirectoryName(source)!, $"~rename-{Guid.NewGuid():N}");
        Directory.Move(source, temporary);
        try
        {
            Directory.Move(temporary, target);
        }
        catch
        {
            Directory.Move(temporary, source);
            throw;
        }
    }

    /// <summary>Writes the content to a file in an existing folder, replacing a file with the same name.</summary>
    public async Task<FileItem> SaveFileAsync(string? path, string? name, Stream content, CancellationToken cancellationToken)
    {
        var file = ResolveFile(path, name, mustExist: false);
        await using (var target = new FileStream(file.FullPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await content.CopyToAsync(target, cancellationToken);
        }

        return ToFileItem(file.Folder.Category, file.Folder.RelativePath, new FileInfo(file.FullPath));
    }

    public void DeleteFile(string? path, string? name)
    {
        File.Delete(ResolveFile(path, name).FullPath);
    }

    private FileItem ToFileItem(string category, string folderPath, FileInfo info)
    {
        return new FileItem(category, categoryNames[category], info.Name, info.Length, info.LastWriteTime, folderPath);
    }

    private sealed record FolderWalk(FolderBuilder Root, int SubfolderCount, List<(string FolderPath, FileInfo Info)> Files);

    private sealed class FolderBuilder(string name, string path, int depth)
    {
        public string Name { get; } = name;

        public string Path { get; } = path;

        public int Depth { get; } = depth;

        public int FileCount { get; set; }

        public List<FolderBuilder> Children { get; } = new();

        public FolderNode ToNode()
        {
            return new FolderNode(
                Name,
                Path,
                FileCount,
                Children.OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase).Select(child => child.ToNode()).ToList());
        }
    }
}
