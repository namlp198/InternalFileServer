using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

var uploadDirectory = builder.Configuration["FileServer:UploadDirectory"] ?? "uploads";
var maxUploadSizeMb = builder.Configuration.GetValue("FileServer:MaxUploadSizeMB", 2048);
var adminPassword = builder.Configuration["FileServer:AdminPassword"] ?? "change-me";
var maxUploadSizeBytes = maxUploadSizeMb * 1024L * 1024L;

var categories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["documents"] = "Documents",
    ["tools"] = "Tools / Software"
};

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxUploadSizeBytes;
    options.ValueLengthLimit = int.MaxValue;
});

var app = builder.Build();
var uploadRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, uploadDirectory));
Directory.CreateDirectory(uploadRoot);
foreach (var category in categories.Keys)
{
    Directory.CreateDirectory(GetCategoryRoot(uploadRoot, categories, category));
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/categories", () =>
{
    return Results.Ok(categories.Select(category => new CategoryItem(category.Key, category.Value)));
});

app.MapGet("/api/files", (string? category) =>
{
    IEnumerable<string> selectedCategories = string.IsNullOrWhiteSpace(category) || category.Equals("all", StringComparison.OrdinalIgnoreCase)
        ? categories.Keys
        : new[] { category };

    var files = selectedCategories
        .SelectMany(categoryKey =>
        {
            var categoryRoot = GetCategoryRoot(uploadRoot, categories, categoryKey);
            return Directory.EnumerateFiles(categoryRoot)
                .Select(path =>
                {
                    var info = new FileInfo(path);
                    return new FileItem(categoryKey, categories[categoryKey], info.Name, info.Length, info.LastWriteTime);
                });
        })
        .OrderByDescending(file => file.Modified);

    return Results.Ok(files);
});

app.MapPost("/api/admin/verify", (AdminLogin login) =>
{
    return IsAdmin(login.Password, adminPassword)
        ? Results.Ok(new { isAdmin = true })
        : Results.Unauthorized();
});

app.MapPost("/api/upload", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Content type must be multipart/form-data." });
    }

    var form = await request.ReadFormAsync();
    var category = form["category"].ToString();
    var categoryRoot = GetCategoryRoot(uploadRoot, categories, category);

    if (form.Files.Count == 0)
    {
        return Results.BadRequest(new { message = "No file uploaded." });
    }

    var uploaded = new List<FileItem>();
    foreach (var file in form.Files)
    {
        if (file.Length == 0)
        {
            continue;
        }

        if (file.Length > maxUploadSizeBytes)
        {
            return Results.BadRequest(new { message = $"File '{file.FileName}' exceeds {maxUploadSizeMb} MB." });
        }

        var safeFileName = Path.GetFileName(file.FileName);
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            return Results.BadRequest(new { message = "Invalid file name." });
        }

        var targetPath = GetSafeUploadPath(categoryRoot, safeFileName);
        await using var stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await file.CopyToAsync(stream);

        var info = new FileInfo(targetPath);
        uploaded.Add(new FileItem(category, categories[category], info.Name, info.Length, info.LastWriteTime));
    }

    return uploaded.Count == 0
        ? Results.BadRequest(new { message = "Uploaded files are empty." })
        : Results.Ok(uploaded);
});

app.MapGet("/api/download/{category}/{filename}", (string category, string filename) =>
{
    var categoryRoot = GetCategoryRoot(uploadRoot, categories, category);
    var filePath = GetSafeUploadPath(categoryRoot, filename);
    if (!File.Exists(filePath))
    {
        return Results.NotFound(new { message = "File not found." });
    }

    var provider = new FileExtensionContentTypeProvider();
    if (!provider.TryGetContentType(filePath, out var contentType))
    {
        contentType = "application/octet-stream";
    }

    return Results.File(filePath, contentType, Path.GetFileName(filePath));
});

app.MapDelete("/api/files/{category}/{filename}", (string category, string filename, HttpRequest request) =>
{
    var password = request.Headers["X-Admin-Password"].ToString();
    if (!IsAdmin(password, adminPassword))
    {
        return Results.Unauthorized();
    }

    var categoryRoot = GetCategoryRoot(uploadRoot, categories, category);
    var filePath = GetSafeUploadPath(categoryRoot, filename);
    if (!File.Exists(filePath))
    {
        return Results.NotFound(new { message = "File not found." });
    }

    File.Delete(filePath);
    return Results.Ok(new { message = "Deleted." });
});

app.Run();

static string GetCategoryRoot(string uploadRoot, Dictionary<string, string> categories, string category)
{
    if (!categories.ContainsKey(category))
    {
        throw new BadHttpRequestException("Invalid category.");
    }

    return Path.GetFullPath(Path.Combine(uploadRoot, category));
}

static string GetSafeUploadPath(string categoryRoot, string filename)
{
    var safeFileName = Path.GetFileName(filename);
    if (string.IsNullOrWhiteSpace(safeFileName) || safeFileName != filename)
    {
        throw new BadHttpRequestException("Invalid file name.");
    }

    var fullPath = Path.GetFullPath(Path.Combine(categoryRoot, safeFileName));
    if (!fullPath.StartsWith(categoryRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
    {
        throw new BadHttpRequestException("Invalid file path.");
    }

    return fullPath;
}

static bool IsAdmin(string? password, string adminPassword)
{
    return !string.IsNullOrWhiteSpace(adminPassword)
        && string.Equals(password, adminPassword, StringComparison.Ordinal);
}

record CategoryItem(string Key, string Name);
record AdminLogin(string Password);
record FileItem(string Category, string CategoryName, string Name, long Size, DateTime Modified);
