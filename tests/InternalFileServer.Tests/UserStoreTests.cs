using Xunit;

public sealed class UserStoreTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ifs-users-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(directory, "users.json");

    public UserStoreTests() => Directory.CreateDirectory(directory);

    public void Dispose() => Directory.Delete(directory, recursive: true);

    private UserStore NewStore()
    {
        var store = new UserStore(FilePath);
        store.Load();
        return store;
    }

    [Fact]
    public void Load_MissingFileIsEmpty()
    {
        var store = NewStore();
        Assert.Empty(store.List());
        Assert.False(store.HasActiveAdmin());
    }

    [Fact]
    public void Load_CorruptFileThrows()
    {
        File.WriteAllText(FilePath, "{ not json");
        Assert.ThrowsAny<Exception>(() => new UserStore(FilePath).Load());
    }

    [Fact]
    public void Register_DuplicateInDifferentCaseIs409()
    {
        var store = NewStore();
        store.Register("alice", "password1", UserStore.UserRole);

        var ex = Assert.Throws<StorageException>(() => store.Register("Alice", "password1", UserStore.UserRole));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("Username is already taken.", ex.Message);
    }

    [Fact]
    public void Register_PersistsAcrossInstancesWithoutPlainPassword()
    {
        NewStore().Register("alice", "s3cret-pass", UserStore.AdminRole);

        var reloaded = NewStore();
        var user = reloaded.FindByUsername("ALICE");
        Assert.NotNull(user);
        Assert.True(AccountRules.VerifyPassword("s3cret-pass", user!.PasswordHash));
        Assert.DoesNotContain("s3cret-pass", File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Register_InvalidUsernameOrRoleIs400()
    {
        var store = NewStore();
        Assert.Equal(400, Assert.Throws<StorageException>(() => store.Register("a", "password1", "User")).StatusCode);
        Assert.Equal(400, Assert.Throws<StorageException>(() => store.Register("alice", "password1", "Root")).StatusCode);
    }

    [Fact]
    public void Update_LastActiveAdminCannotBeDemotedOrDisabled()
    {
        var store = NewStore();
        var admin = store.Register("admin", "password1", UserStore.AdminRole);
        var before = File.ReadAllText(FilePath);

        Assert.Equal(409, Assert.Throws<StorageException>(() => store.Update(admin.Id, UserStore.UserRole, null)).StatusCode);
        var ex = Assert.Throws<StorageException>(() => store.Update(admin.Id, null, true));
        Assert.Equal(409, ex.StatusCode);
        Assert.Equal("At least one active admin is required.", ex.Message);

        Assert.Equal(before, File.ReadAllText(FilePath));
        Assert.Equal(UserStore.AdminRole, store.FindById(admin.Id)!.Role);
        Assert.False(store.FindById(admin.Id)!.Disabled);
    }

    [Fact]
    public void Update_AllowsDemotionWhenAnotherAdminExists()
    {
        var store = NewStore();
        var first = store.Register("admin1", "password1", UserStore.AdminRole);
        store.Register("admin2", "password1", UserStore.AdminRole);

        Assert.Equal(UserStore.UserRole, store.Update(first.Id, UserStore.UserRole, null).Role);
    }

    [Fact]
    public void Update_DisablingRotatesStamp()
    {
        var store = NewStore();
        store.Register("admin", "password1", UserStore.AdminRole);
        var user = store.Register("bob", "password1", UserStore.UserRole);

        var summary = store.Update(user.Id, null, true);

        Assert.True(summary.Disabled);
        Assert.NotEqual(user.SecurityStamp, store.FindById(user.Id)!.SecurityStamp);
    }

    [Fact]
    public void Update_UnknownIdIs404()
    {
        var store = NewStore();
        Assert.Equal(404, Assert.Throws<StorageException>(() => store.Update("missing", "User", null)).StatusCode);
        Assert.Equal(404, Assert.Throws<StorageException>(() => store.RotateStamp("missing")).StatusCode);
    }

    [Fact]
    public void RotateStamp_ChangesStampAndPersists()
    {
        var store = NewStore();
        var user = store.Register("bob", "password1", UserStore.UserRole);

        store.RotateStamp(user.Id);

        var stamp = store.FindById(user.Id)!.SecurityStamp;
        Assert.NotEqual(user.SecurityStamp, stamp);
        Assert.Equal(stamp, NewStore().FindById(user.Id)!.SecurityStamp);
    }

    [Fact]
    public void FailedWriteLeavesMemoryUnchanged()
    {
        var store = NewStore();
        Directory.CreateDirectory(FilePath + ".tmp");

        Assert.ThrowsAny<Exception>(() => store.Register("alice", "password1", UserStore.UserRole));
        Assert.Empty(store.List());
    }

    [Fact]
    public void UserSummary_HasNoHashMember()
    {
        var names = typeof(UserSummary).GetProperties().Select(p => p.Name).ToArray();
        Assert.DoesNotContain(names, n => n.Contains("Hash") || n.Contains("Stamp"));
    }
}
