using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>Hosts the real server with temporary upload and account-store paths.</summary>
sealed class TestServer : WebApplicationFactory<Program>
{
    private const string Password = "correct-horse-1";

    private readonly string root = Path.Combine(Path.GetTempPath(), "ifs-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string userStoreFile;
    private readonly Dictionary<string, string?> settings;

    public TestServer(string? userStoreFile = null, Dictionary<string, string?>? extraSettings = null)
    {
        UploadRoot = Path.Combine(root, "uploads");
        this.userStoreFile = userStoreFile ?? Path.Combine(root, "data", "users.json");
        settings = new Dictionary<string, string?>
        {
            ["FileServer:UploadDirectory"] = UploadRoot,
            ["FileServer:UserStoreFile"] = this.userStoreFile,
        };
        foreach (var pair in extraSettings ?? new())
        {
            settings[pair.Key] = pair.Value;
        }
    }

    public string UploadRoot { get; }

    public string StoreFile => userStoreFile;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(settings));
        builder.ConfigureServices(services => services.AddTransient<IStartupFilter, RemoteIpFilter>());
    }

    /// <summary>A client with the CSRF header; the optional remote IP simulates a non-loopback caller.</summary>
    public HttpClient NewClient(string? remoteIp = null, bool csrfHeader = true)
    {
        var client = CreateClient();
        if (csrfHeader)
        {
            client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        }

        if (remoteIp is not null)
        {
            client.DefaultRequestHeaders.Add(RemoteIpFilter.Header, remoteIp);
        }

        return client;
    }

    /// <summary>Registers a user (the first loopback registration becomes Admin) and returns its signed-in client.</summary>
    public async Task<HttpClient> SignedInAsync(string username, string? remoteIp = null)
    {
        var client = NewClient(remoteIp);
        var response = await client.PostAsJsonAsync("/api/auth/register", new { username, password = Password, confirmPassword = Password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return client;
    }

    public static string ValidPassword => Password;

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RemoteIpFilter : IStartupFilter
    {
        public const string Header = "X-Test-Remote-Ip";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var value = context.Request.Headers[Header].ToString();
                context.Connection.RemoteIpAddress = IPAddress.Parse(value.Length > 0 ? value : "127.0.0.1");
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}

public class ServerIntegrationTests
{
    private static StringContent Json(object body) => new(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

    private static async Task<string?> MessageAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("message").GetString();
    }

    private static async Task<string> UserIdAsync(HttpClient adminClient, string username)
    {
        var users = await adminClient.GetFromJsonAsync<JsonElement>("/api/users");
        return users.EnumerateArray().First(u => u.GetProperty("username").GetString() == username).GetProperty("id").GetString()!;
    }

    private static void SeedDisk(TestServer server)
    {
        Directory.CreateDirectory(Path.Combine(server.UploadRoot, "documents", "sub"));
        File.WriteAllText(Path.Combine(server.UploadRoot, "documents", "a.txt"), "hello");
    }

    private static void AssertDiskUnchanged(TestServer server)
    {
        Assert.True(File.Exists(Path.Combine(server.UploadRoot, "documents", "a.txt")));
        Assert.True(Directory.Exists(Path.Combine(server.UploadRoot, "documents", "sub")));
        Assert.False(Directory.Exists(Path.Combine(server.UploadRoot, "documents", "new")));
    }

    private static IEnumerable<(string Name, Func<HttpClient, Task<HttpResponseMessage>> Call)> AdminCalls() => new (string, Func<HttpClient, Task<HttpResponseMessage>>)[]
    {
        ("delete file", c => c.DeleteAsync("/api/files?path=documents&name=a.txt")),
        ("delete file by category", c => c.DeleteAsync("/api/files/documents/a.txt")),
        ("create folder", async c => await c.PostAsync("/api/folders", Json(new { parentPath = "documents", name = "new" }))),
        ("rename folder", async c => await c.PatchAsync("/api/folders", Json(new { path = "documents/sub", newName = "renamed" }))),
        ("delete folder", c => c.DeleteAsync("/api/folders?path=documents/sub&recursive=true")),
        ("stats", c => c.GetAsync("/api/admin/stats")),
        ("list users", c => c.GetAsync("/api/users")),
        ("patch user", async c => await c.PatchAsync("/api/users/someone", Json(new { role = "Admin" }))),
    };

    public static IEnumerable<object[]> AdminCallNames() => AdminCalls().Select(call => new object[] { call.Name });

    private static Func<HttpClient, Task<HttpResponseMessage>> Call(string name) => AdminCalls().First(call => call.Name == name).Call;

    [Theory]
    [MemberData(nameof(AdminCallNames))]
    public async Task Anonymous_admin_calls_return_401_and_change_nothing(string name)
    {
        using var server = new TestServer();
        SeedDisk(server);

        var response = await Call(name)(server.NewClient());

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Sign in required.", await MessageAsync(response));
        AssertDiskUnchanged(server);
    }

    [Theory]
    [MemberData(nameof(AdminCallNames))]
    public async Task User_role_admin_calls_return_403_and_change_nothing(string name)
    {
        using var server = new TestServer();
        SeedDisk(server);
        await server.SignedInAsync("admin1");
        var user = await server.SignedInAsync("user1");

        var response = await Call(name)(user);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("You do not have permission for this action.", await MessageAsync(response));
        AssertDiskUnchanged(server);
    }

    [Fact]
    public async Task Admin_can_use_every_admin_route()
    {
        using var server = new TestServer();
        SeedDisk(server);
        var admin = await server.SignedInAsync("admin1");
        await server.SignedInAsync("user1");
        var userId = await UserIdAsync(admin, "user1");

        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/stats")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/users")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync($"/api/users/{userId}", Json(new { role = "Admin" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await admin.PostAsync("/api/folders", Json(new { parentPath = "documents", name = "new" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync("/api/folders", Json(new { path = "documents/new", newName = "renamed" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/folders?path=documents/renamed")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.DeleteAsync("/api/files?path=documents&name=a.txt")).StatusCode);
        Assert.False(File.Exists(Path.Combine(server.UploadRoot, "documents", "a.txt")));
    }

    [Fact]
    public async Task User_can_list_upload_and_download()
    {
        using var server = new TestServer();
        SeedDisk(server);
        await server.SignedInAsync("admin1");
        var user = await server.SignedInAsync("user1");

        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/folders/tree")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/files?category=all")).StatusCode);

        using var form = new MultipartFormDataContent
        {
            { new StringContent("documents"), "path" },
            { new ByteArrayContent(Encoding.UTF8.GetBytes("payload")), "files", "up.txt" },
        };
        Assert.Equal(HttpStatusCode.OK, (await user.PostAsync("/api/upload", form)).StatusCode);

        var download = await user.GetAsync("/api/download?path=documents&name=up.txt");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal("payload", await download.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Anonymous_cannot_list_files()
    {
        using var server = new TestServer();

        Assert.Equal(HttpStatusCode.Unauthorized, (await server.NewClient().GetAsync("/api/files")).StatusCode);
    }

    [Fact]
    public async Task Register_signs_in_and_me_returns_the_user_until_logout()
    {
        using var server = new TestServer();
        await server.SignedInAsync("admin1");
        var client = server.NewClient();

        var register = await client.PostAsJsonAsync("/api/auth/register", new { username = "alice", password = TestServer.ValidPassword, confirmPassword = TestServer.ValidPassword });
        Assert.Equal(HttpStatusCode.Created, register.StatusCode);
        var body = await register.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("alice", body.GetProperty("username").GetString());
        Assert.Equal("User", body.GetProperty("role").GetString());
        Assert.Contains(register.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ifs.session=") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("samesite=lax", StringComparison.OrdinalIgnoreCase));

        var me = await client.GetFromJsonAsync<JsonElement>("/api/auth/me");
        Assert.Equal("alice", me.GetProperty("username").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync("/api/auth/logout", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Register_rejects_invalid_input_and_case_insensitive_duplicates()
    {
        using var server = new TestServer();
        await server.SignedInAsync("admin1");
        var client = server.NewClient();

        var shortName = await client.PostAsJsonAsync("/api/auth/register", new { username = "ab", password = TestServer.ValidPassword, confirmPassword = TestServer.ValidPassword });
        var mismatch = await client.PostAsJsonAsync("/api/auth/register", new { username = "bob", password = TestServer.ValidPassword, confirmPassword = "different-pw" });
        var duplicate = await client.PostAsJsonAsync("/api/auth/register", new { username = "ADMIN1", password = TestServer.ValidPassword, confirmPassword = TestServer.ValidPassword });

        Assert.Equal(HttpStatusCode.BadRequest, shortName.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, mismatch.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("Username is already taken.", await MessageAsync(duplicate));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_return_the_same_401()
    {
        using var server = new TestServer();
        await server.SignedInAsync("admin1");
        var client = server.NewClient();

        var wrong = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin1", password = "wrong-password" });
        var unknown = await client.PostAsJsonAsync("/api/auth/login", new { username = "nobody", password = "wrong-password" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal(await wrong.Content.ReadAsStringAsync(), await unknown.Content.ReadAsStringAsync());
        Assert.Equal("Invalid username or password.", await MessageAsync(wrong));
    }

    [Fact]
    public async Task Sixth_login_after_five_failures_is_locked_even_with_the_right_password()
    {
        using var server = new TestServer();
        await server.SignedInAsync("admin1");
        var client = server.NewClient();

        for (var i = 0; i < 5; i++)
        {
            var failed = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin1", password = "wrong-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        var locked = await client.PostAsJsonAsync("/api/auth/login", new { username = "admin1", password = TestServer.ValidPassword });

        Assert.Equal(HttpStatusCode.TooManyRequests, locked.StatusCode);
        Assert.True(locked.Headers.Contains("Retry-After"));
    }

    [Fact]
    public async Task Login_succeeds_with_the_right_password()
    {
        using var server = new TestServer();
        await server.SignedInAsync("admin1");
        var client = server.NewClient();

        var login = await client.PostAsJsonAsync("/api/auth/login", new { username = "ADMIN1", password = TestServer.ValidPassword });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public async Task Promotion_applies_to_the_next_request_and_disable_kills_the_session()
    {
        using var server = new TestServer();
        var admin = await server.SignedInAsync("admin1");
        var user = await server.SignedInAsync("user1");
        var userId = await UserIdAsync(admin, "user1");
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/users")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync($"/api/users/{userId}", Json(new { role = "Admin" }))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await user.GetAsync("/api/users")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsync($"/api/users/{userId}", Json(new { disabled = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await user.GetAsync("/api/auth/me")).StatusCode);

        var login = await server.NewClient().PostAsJsonAsync("/api/auth/login", new { username = "user1", password = TestServer.ValidPassword });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("This account is disabled.", await MessageAsync(login));
    }

    [Fact]
    public async Task Patch_that_removes_the_last_admin_returns_409_and_invalid_role_400()
    {
        using var server = new TestServer();
        var admin = await server.SignedInAsync("admin1");
        var adminId = await UserIdAsync(admin, "admin1");

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsync($"/api/users/{adminId}", Json(new { role = "User" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsync($"/api/users/{adminId}", Json(new { disabled = true }))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PatchAsync($"/api/users/{adminId}", Json(new { role = "Root" }))).StatusCode);
    }

    [Fact]
    public async Task First_loopback_registration_is_admin_and_later_ones_are_users()
    {
        using var server = new TestServer();
        var first = await server.NewClient().PostAsJsonAsync("/api/auth/register", new { username = "first", password = TestServer.ValidPassword, confirmPassword = TestServer.ValidPassword });
        var second = await server.NewClient().PostAsJsonAsync("/api/auth/register", new { username = "second", password = TestServer.ValidPassword, confirmPassword = TestServer.ValidPassword });

        Assert.Equal("Admin", (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());
        Assert.Equal("User", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("role").GetString());
    }

    [Fact]
    public async Task Remote_registration_without_an_admin_is_refused()
    {
        using var server = new TestServer();

        var response = await server.NewClient("192.168.1.20").PostAsJsonAsync("/api/auth/register", new { username = "remote", password = TestServer.ValidPassword, confirmPassword = TestServer.ValidPassword });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("Setup is not complete", await MessageAsync(response));
    }

    [Fact]
    public async Task Restart_keeps_accounts_and_passwords()
    {
        using var server = new TestServer();
        await server.SignedInAsync("admin1");
        using var restarted = new TestServer(server.StoreFile);

        var login = await restarted.NewClient().PostAsJsonAsync("/api/auth/login", new { username = "admin1", password = TestServer.ValidPassword });

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Theory]
    [InlineData("/users.json")]
    [InlineData("/data/users.json")]
    public async Task Account_store_is_not_served(string url)
    {
        using var server = new TestServer();
        await server.SignedInAsync("admin1");

        var response = await server.NewClient().GetAsync(url);

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("pbkdf2", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Unsafe_request_without_csrf_header_returns_403_json()
    {
        using var server = new TestServer();

        var response = await server.NewClient(csrfHeader: false).PostAsJsonAsync("/api/auth/login", new { username = "a", password = "b" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.False(string.IsNullOrEmpty(await MessageAsync(response)));
    }

    [Fact]
    public async Task Removed_admin_verify_route_returns_404()
    {
        using var server = new TestServer();

        var response = await server.NewClient().PostAsync("/api/admin/verify", Json(new { password = "x" }));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Startup_fails_when_the_store_is_inside_wwwroot()
    {
        using var server = new TestServer("wwwroot/users.json");

        Assert.ThrowsAny<Exception>(() => server.CreateClient());
    }

    [Fact]
    public void Startup_fails_when_the_store_is_inside_the_upload_root()
    {
        var uploads = Path.Combine(Path.GetTempPath(), "ifs-tests-inside-" + Guid.NewGuid().ToString("N"));
        using var server = new TestServer(
            Path.Combine(uploads, "users.json"),
            new() { ["FileServer:UploadDirectory"] = uploads });

        Assert.ThrowsAny<Exception>(() => server.CreateClient());
    }

    [Fact]
    public void Startup_fails_when_the_store_file_is_corrupt()
    {
        var store = Path.Combine(Path.GetTempPath(), "ifs-tests-corrupt-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(store, "{ not json");
        try
        {
            using var server = new TestServer(store);

            Assert.ThrowsAny<Exception>(() => server.CreateClient());
        }
        finally
        {
            File.Delete(store);
        }
    }
}
