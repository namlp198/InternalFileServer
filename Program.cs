using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;

var builder = WebApplication.CreateBuilder(args);

var maxUploadSizeMb = builder.Configuration.GetValue("FileServer:MaxUploadSizeMB", 2048);
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

builder.Services.AddSingleton(services =>
{
    var store = new UserStore(ResolveUserStorePath(services.GetRequiredService<IConfiguration>(), services.GetRequiredService<IWebHostEnvironment>()));
    store.Load();
    return store;
});

builder.Services.AddSingleton(services =>
{
    var configuration = services.GetRequiredService<IConfiguration>();
    return new LoginThrottle(
        configuration.GetValue("FileServer:MaxFailedLogins", 5),
        TimeSpan.FromMinutes(configuration.GetValue("FileServer:LockoutMinutes", 15)));
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = Program.SessionCookieName;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context => WriteStatusAsync(context.Response, StatusCodes.Status401Unauthorized, "Sign in required.");
        options.Events.OnRedirectToAccessDenied = context => WriteStatusAsync(context.Response, StatusCodes.Status403Forbidden, "You do not have permission for this action.");
        options.Events.OnValidatePrincipal = async context =>
        {
            var store = context.HttpContext.RequestServices.GetRequiredService<UserStore>();
            var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = id is null ? null : store.FindById(id);
            if (user is null || user.Disabled || user.SecurityStamp != context.Principal?.FindFirstValue(Program.StampClaim))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            context.ReplacePrincipal(CreatePrincipal(user));
        };
    });

builder.Services
    .AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
    .Configure<IConfiguration>((options, configuration) =>
    {
        options.ExpireTimeSpan = TimeSpan.FromHours(configuration.GetValue("FileServer:SessionHours", 8));
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    options.AddPolicy(Program.AdminPolicy, policy => policy.RequireAuthenticatedUser().RequireRole(UserStore.AdminRole));
});

builder.Services.AddRateLimiter(options =>
{
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.HttpContext.Response.WriteAsJsonAsync(new { message = "Too many requests. Try again later." }, cancellationToken);
    };
    options.AddPolicy(Program.AuthRateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

var app = builder.Build();
var uploadRoot = ResolveUploadRoot(app.Configuration, app.Environment);
var minPasswordLength = app.Configuration.GetValue("FileServer:MinPasswordLength", AccountRules.DefaultMinPasswordLength);
if (!string.IsNullOrEmpty(app.Configuration["FileServer:AdminPassword"]))
{
    app.Logger.LogWarning("FileServer:AdminPassword is no longer used and is ignored. Remove it from the configuration.");
}

// Resolving the store here makes a corrupt file or an unsafe path fail startup instead of the first request.
var userStore = app.Services.GetRequiredService<UserStore>();
var loginThrottle = app.Services.GetRequiredService<LoginThrottle>();
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
            // Windows reports moving or deleting a folder with open files as access denied.
            IOException or UnauthorizedAccessException => (StatusCodes.Status409Conflict, "The file or folder is in use."),
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

// Cross-site pages cannot add this header without a CORS preflight, which the server never grants.
app.Use(async (context, next) =>
{
    if (RequiresCsrfHeader(context.Request) && context.Request.Headers["X-Requested-With"] != "XMLHttpRequest")
    {
        await WriteStatusAsync(context.Response, StatusCodes.Status403Forbidden, "Missing X-Requested-With header.");
        return;
    }

    await next(context);
});

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapPost("/api/auth/register", async (HttpContext context) =>
{
    var request = await ReadJsonAsync<RegisterRequest>(context.Request);
    AccountRules.ValidateUsername(request.Username);
    AccountRules.ValidatePassword(request.Password, request.ConfirmPassword, minPasswordLength);

    var firstAdmin = !userStore.HasActiveAdmin();
    if (firstAdmin && !IsLoopback(context))
    {
        return Results.Json(
            new { message = "Setup is not complete. Register the first account on the server machine." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var account = userStore.Register(request.Username!, request.Password!, firstAdmin ? UserStore.AdminRole : UserStore.UserRole);
    await SignInAsync(context, account);
    app.Logger.LogInformation("Registered user {Username} as {Role}.", account.Username, account.Role);
    return Results.Json(new { username = account.Username, role = account.Role }, statusCode: StatusCodes.Status201Created);
}).AllowAnonymous().RequireRateLimiting(Program.AuthRateLimitPolicy);

app.MapPost("/api/auth/login", async (HttpContext context) =>
{
    var request = await ReadJsonAsync<LoginRequest>(context.Request);
    var username = request.Username ?? string.Empty;
    var password = request.Password ?? string.Empty;

    if (loginThrottle.GetLockout(username) is { } remaining)
    {
        var minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        context.Response.Headers.RetryAfter = ((int)Math.Ceiling(remaining.TotalSeconds)).ToString();
        return Results.Json(
            new { message = $"Too many failed attempts. Try again in {minutes} minute(s)." },
            statusCode: StatusCodes.Status429TooManyRequests);
    }

    var account = userStore.FindByUsername(username);
    if (account is null)
    {
        AccountRules.VerifyDummy(password);
    }

    if (account is null || !AccountRules.VerifyPassword(password, account.PasswordHash))
    {
        loginThrottle.RecordFailure(username);
        return Results.Json(new { message = "Invalid username or password." }, statusCode: StatusCodes.Status401Unauthorized);
    }

    if (account.Disabled)
    {
        return Results.Json(new { message = "This account is disabled." }, statusCode: StatusCodes.Status403Forbidden);
    }

    loginThrottle.RecordSuccess(username);
    await SignInAsync(context, account);
    return Results.Ok(new { username = account.Username, role = account.Role });
}).AllowAnonymous().RequireRateLimiting(Program.AuthRateLimitPolicy);

// Rotating the stamp ends every session of the user, not just this one.
app.MapPost("/api/auth/logout", async (HttpContext context) =>
{
    var id = context.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (id is not null && userStore.FindById(id) is not null)
    {
        userStore.RotateStamp(id);
    }

    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).AllowAnonymous();

app.MapGet("/api/auth/me", (ClaimsPrincipal user) =>
    Results.Ok(new { username = user.Identity?.Name, role = user.FindFirstValue(ClaimTypes.Role) }));

app.MapGet("/api/users", () => Results.Ok(userStore.List())).RequireAuthorization(Program.AdminPolicy);

app.MapPatch("/api/users/{id}", async (string id, HttpRequest request) =>
{
    var update = await ReadJsonAsync<UpdateUserRequest>(request);
    return Results.Ok(userStore.Update(id, update.Role, update.Disabled));
}).RequireAuthorization(Program.AdminPolicy);

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

app.MapDelete("/api/files", (string? path, string? name) => DeleteFile(path, name)).RequireAuthorization(Program.AdminPolicy);

app.MapDelete("/api/files/{category}/{filename}", (string category, string filename) => DeleteFile(category, filename)).RequireAuthorization(Program.AdminPolicy);

app.MapGet("/api/folders/tree", (CancellationToken cancellationToken) => Results.Ok(fileStore.GetTree(cancellationToken)));

// Authorization runs before the handler, so a 401 or 403 never reads input or touches the disk.
app.MapPost("/api/folders", async (HttpRequest request) =>
{
    var folder = await ReadJsonAsync<CreateFolderRequest>(request);
    var created = fileStore.CreateFolder(folder.ParentPath, folder.Name);
    return Results.Created($"/api/files?path={Uri.EscapeDataString(created.Path)}", created);
}).RequireAuthorization(Program.AdminPolicy);

app.MapPatch("/api/folders", async (HttpRequest request) =>
{
    var rename = await ReadJsonAsync<RenameFolderRequest>(request);
    return Results.Ok(fileStore.RenameFolder(rename.Path, rename.NewName));
}).RequireAuthorization(Program.AdminPolicy);

app.MapDelete("/api/folders", (HttpRequest request) =>
{
    var recursiveValue = request.Query["recursive"].ToString();
    var recursive = false;
    if (recursiveValue.Length > 0 && !bool.TryParse(recursiveValue, out recursive))
    {
        return Results.BadRequest(new { message = "Parameter 'recursive' must be true or false." });
    }

    fileStore.DeleteFolder(request.Query["path"].ToString(), recursive);
    return Results.Ok(new { message = "Folder deleted." });
}).RequireAuthorization(Program.AdminPolicy);

app.MapGet("/api/admin/stats", (CancellationToken cancellationToken) => Results.Ok(fileStore.GetStats(cancellationToken)))
    .RequireAuthorization(Program.AdminPolicy);

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

IResult DeleteFile(string? path, string? name)
{
    fileStore.DeleteFile(path, name);
    return Results.Ok(new { message = "Deleted." });
}

static async Task<T> ReadJsonAsync<T>(HttpRequest request) where T : class
{
    if (!request.HasJsonContentType())
    {
        throw new BadHttpRequestException("Content type must be application/json.", StatusCodes.Status400BadRequest);
    }

    try
    {
        return await request.ReadFromJsonAsync<T>(request.HttpContext.RequestAborted)
            ?? throw new BadHttpRequestException("Request body is required.", StatusCodes.Status400BadRequest);
    }
    catch (JsonException)
    {
        throw new BadHttpRequestException("Request body is not valid JSON.", StatusCodes.Status400BadRequest);
    }
}

static bool RequiresCsrfHeader(HttpRequest request)
{
    return request.Path.StartsWithSegments("/api")
        && (HttpMethods.IsPost(request.Method) || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method) || HttpMethods.IsDelete(request.Method));
}

static bool IsLoopback(HttpContext context)
{
    return context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
}

static ClaimsPrincipal CreatePrincipal(UserAccount user)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.NameIdentifier, user.Id),
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Role),
        new Claim(Program.StampClaim, user.SecurityStamp)
    };
    return new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
}

static Task SignInAsync(HttpContext context, UserAccount user)
{
    return context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, CreatePrincipal(user));
}

static Task WriteStatusAsync(HttpResponse response, int statusCode, string message)
{
    response.StatusCode = statusCode;
    return response.WriteAsJsonAsync(new { message });
}

static string ResolveUploadRoot(IConfiguration configuration, IHostEnvironment environment)
{
    var uploadDirectory = configuration["FileServer:UploadDirectory"] ?? "uploads";
    return Path.GetFullPath(Path.Combine(environment.ContentRootPath, uploadDirectory));
}

static string ResolveUserStorePath(IConfiguration configuration, IWebHostEnvironment environment)
{
    var configured = configuration["FileServer:UserStoreFile"];
    var storePath = Path.GetFullPath(Path.Combine(environment.ContentRootPath, string.IsNullOrWhiteSpace(configured) ? "data/users.json" : configured));
    var forbiddenRoots = new[] { environment.WebRootPath, ResolveUploadRoot(configuration, environment) };
    foreach (var root in forbiddenRoots.Where(root => !string.IsNullOrEmpty(root)))
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (storePath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FileServer:UserStoreFile must not be inside '{root}'.");
        }
    }

    return storePath;
}

public partial class Program
{
    internal const string SessionCookieName = "ifs.session";
    internal const string StampClaim = "stamp";
    internal const string AdminPolicy = "Admin";
    internal const string AuthRateLimitPolicy = "auth";
}

record CategoryItem(string Key, string Name);
record RegisterRequest(string? Username, string? Password, string? ConfirmPassword);
record LoginRequest(string? Username, string? Password);
record UpdateUserRequest(string? Role, bool? Disabled);
record FileItem(string Category, string CategoryName, string Name, long Size, DateTime Modified, string Path);
record FolderNode(string Name, string Path, int FileCount, IReadOnlyList<FolderNode> Children);
record RootStats(string Key, string Name, int FileCount, long TotalBytes);
record StatsResult(int TotalFiles, long TotalBytes, int TotalFolders, IReadOnlyList<RootStats> Roots, IReadOnlyList<FileItem> RecentFiles, IReadOnlyList<FileItem> LargestFiles);
record CreateFolderRequest(string? ParentPath, string? Name);
record RenameFolderRequest(string? Path, string? NewName);
