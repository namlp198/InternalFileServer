using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

var uploadDirectory = builder.Configuration["FileServer:UploadDirectory"] ?? "uploads";
var maxUploadSizeMb = builder.Configuration.GetValue("FileServer:MaxUploadSizeMB", 2048);
var adminPassword = builder.Configuration["FileServer:AdminPassword"] ?? "change-me";
var maxFolderDepth = builder.Configuration.GetValue("FileServer:MaxFolderDepth", 10);
var maxFolderNameLength = builder.Configuration.GetValue("FileServer:MaxFolderNameLength", 100);
var maxUploadSizeBytes = maxUploadSizeMb * 1024L * 1024L;
var fileTooLargeMessage = $"File exceeds {maxUploadSizeMb} MB.";

// Kestrel otherwise keeps its 30 MB default request-body limit and rejects
// large multipart requests before the upload endpoint can process them.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = maxUploadSizeBytes;
});

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

// Parameter binding failures throw so the error middleware can answer them with a JSON message.
builder.Services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);

var app = builder.Build();
var uploadRoot = Path.GetFullPath(Path.Combine(app.Environment.ContentRootPath, uploadDirectory));
var fileStore = new FileStore(uploadRoot, categories, maxFolderDepth, maxFolderNameLength);
fileStore.EnsureRoots();
var contentTypes = new FileExtensionContentTypeProvider();

app.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // The client disconnected; there is nobody left to answer.
    }
    catch (Exception exception) when (!context.Response.HasStarted)
    {
        var (statusCode, message) = exception switch
        {
            StorageException storage => (storage.StatusCode, storage.Message),
            BadHttpRequestException badRequest => (badRequest.StatusCode, badRequest.Message),
            DirectoryNotFoundException or FileNotFoundException => (StatusCodes.Status404NotFound, "Not found."),
            PathTooLongException => (StatusCodes.Status400BadRequest, "Path is too long."),
            IOException => (StatusCodes.Status409Conflict, "The file or folder is in use."),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error.")
        };

        if (statusCode == StatusCodes.Status500InternalServerError)
        {
            app.Logger.LogError(exception, "Unhandled error while processing {Method} {Path}.", context.Request.Method, context.Request.Path);
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        await context.Response.WriteAsJsonAsync(new { message });
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/categories", () =>
{
    return Results.Ok(categories.Select(category => new CategoryItem(category.Key, category.Value)));
});

app.MapGet("/api/files", (string? path, string? category) =>
{
    IEnumerable<FileItem> files;
    if (!string.IsNullOrEmpty(path))
    {
        files = fileStore.ListFiles(path);
    }
    else if (string.IsNullOrWhiteSpace(category) || category.Equals("all", StringComparison.OrdinalIgnoreCase))
    {
        files = categories.Keys.SelectMany(categoryKey => fileStore.ListFiles(categoryKey)).ToList();
    }
    else
    {
        files = fileStore.ListFiles(category);
    }

    return Results.Ok(files.OrderByDescending(file => file.Modified).ToList());
});

app.MapPost("/api/admin/verify", (AdminLogin login) =>
{
    return IsAdmin(login.Password, adminPassword)
        ? Results.Ok(new { isAdmin = true })
        : AdminRequired();
});

app.MapPost("/api/upload", async (HttpRequest request) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Content type must be multipart/form-data." });
    }

    IFormCollection form;
    try
    {
        form = await request.ReadFormAsync(request.HttpContext.RequestAborted);
    }
    catch (Exception exception) when (exception is InvalidDataException or BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
    {
        return Results.BadRequest(new { message = fileTooLargeMessage });
    }

    var path = form["path"].ToString();
    var folder = fileStore.ResolveFolder(string.IsNullOrEmpty(path) ? form["category"].ToString() : path);

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
            return Results.BadRequest(new { message = fileTooLargeMessage });
        }

        await using var content = file.OpenReadStream();
        uploaded.Add(await fileStore.SaveFileAsync(folder.RelativePath, Path.GetFileName(file.FileName), content, request.HttpContext.RequestAborted));
    }

    return uploaded.Count == 0
        ? Results.BadRequest(new { message = "Uploaded files are empty." })
        : Results.Ok(uploaded);
});

app.MapGet("/api/download", (string? path, string? name) => DownloadFile(path, name));

app.MapGet("/api/download/{category}/{filename}", (string category, string filename) => DownloadFile(category, filename));

app.MapDelete("/api/files", (string? path, string? name, HttpRequest request) => DeleteFile(request, path, name));

app.MapDelete("/api/files/{category}/{filename}", (string category, string filename, HttpRequest request) => DeleteFile(request, category, filename));

app.Run();

IResult DownloadFile(string? path, string? name)
{
    var file = fileStore.ResolveFile(path, name);
    if (!contentTypes.TryGetContentType(file.Name, out var contentType))
    {
        contentType = "application/octet-stream";
    }

    return Results.File(file.FullPath, contentType, file.Name);
}

IResult DeleteFile(HttpRequest request, string? path, string? name)
{
    var password = request.Headers["X-Admin-Password"].ToString();
    if (!IsAdmin(password, adminPassword))
    {
        return AdminRequired();
    }

    fileStore.DeleteFile(path, name);
    return Results.Ok(new { message = "Deleted." });
}

static IResult AdminRequired()
{
    return Results.Json(new { message = "Invalid admin password." }, statusCode: StatusCodes.Status401Unauthorized);
}

static bool IsAdmin(string? password, string adminPassword)
{
    if (string.IsNullOrWhiteSpace(adminPassword) || password is null)
    {
        return false;
    }

    // Hashing first gives both sides the same length, so the comparison time does not reveal the password length.
    var expected = SHA256.HashData(Encoding.UTF8.GetBytes(adminPassword));
    var actual = SHA256.HashData(Encoding.UTF8.GetBytes(password));
    return CryptographicOperations.FixedTimeEquals(actual, expected);
}

record CategoryItem(string Key, string Name);
record AdminLogin(string Password);
record FileItem(string Category, string CategoryName, string Name, long Size, DateTime Modified, string Path);
