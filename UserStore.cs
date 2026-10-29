using System.Text.Json;

sealed record UserAccount(string Id, string Username, string Role, bool Disabled, DateTime CreatedUtc, string PasswordHash, string SecurityStamp);

sealed record UserSummary(string Id, string Username, string Role, bool Disabled, DateTime CreatedUtc);

/// <summary>File-backed user accounts cached in memory. Every public method is safe to call concurrently.</summary>
sealed class UserStore
{
    public const string AdminRole = "Admin";
    public const string UserRole = "User";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string path;
    private readonly object sync = new();
    private List<UserAccount> users = new();

    public UserStore(string path)
    {
        this.path = path;
    }

    /// <summary>Loads accounts from disk; a missing file is empty and an unreadable file throws.</summary>
    public void Load()
    {
        lock (sync)
        {
            if (!File.Exists(path))
            {
                users = new List<UserAccount>();
                return;
            }

            try
            {
                var loaded = JsonSerializer.Deserialize<List<UserAccount>>(File.ReadAllText(path));
                if (loaded is null || loaded.Any(u => u is null || u.Id is null || u.Username is null || u.Role is null || u.PasswordHash is null || u.SecurityStamp is null))
                {
                    throw new InvalidDataException($"User store '{path}' has an invalid structure.");
                }

                users = loaded;
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException($"User store '{path}' is corrupt.", ex);
            }
        }
    }

    public UserAccount? FindById(string id)
    {
        lock (sync)
        {
            return users.FirstOrDefault(u => u.Id == id);
        }
    }

    public UserAccount? FindByUsername(string username)
    {
        lock (sync)
        {
            return users.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
        }
    }

    public bool HasActiveAdmin()
    {
        lock (sync)
        {
            return users.Any(IsActiveAdmin);
        }
    }

    /// <summary>Validates the username, hashes the password and stores a new account.</summary>
    public UserAccount Register(string username, string password, string role)
    {
        AccountRules.ValidateUsername(username);
        ValidateRole(role);
        var hash = AccountRules.HashPassword(password);

        lock (sync)
        {
            if (users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
            {
                throw new StorageException(409, "Username is already taken.");
            }

            var account = new UserAccount(Guid.NewGuid().ToString("N"), username, role, false, DateTime.UtcNow, hash, NewStamp());
            Persist(users.Append(account).ToList());
            return account;
        }
    }

    /// <summary>Changes the role and/or disabled flag; disabling rotates the security stamp.</summary>
    public UserSummary Update(string id, string? role, bool? disabled)
    {
        if (role is not null)
        {
            ValidateRole(role);
        }

        lock (sync)
        {
            var index = IndexOf(id);
            var current = users[index];
            var updated = current with
            {
                Role = role ?? current.Role,
                Disabled = disabled ?? current.Disabled,
            };

            if (updated.Disabled && !current.Disabled)
            {
                updated = updated with { SecurityStamp = NewStamp() };
            }

            var next = users.ToList();
            next[index] = updated;
            if (IsActiveAdmin(current) && !next.Any(IsActiveAdmin))
            {
                throw new StorageException(409, "At least one active admin is required.");
            }

            Persist(next);
            return ToSummary(updated);
        }
    }

    /// <summary>Replaces the security stamp, which ends every session of the user.</summary>
    public void RotateStamp(string id)
    {
        lock (sync)
        {
            var index = IndexOf(id);
            var next = users.ToList();
            next[index] = users[index] with { SecurityStamp = NewStamp() };
            Persist(next);
        }
    }

    public IReadOnlyList<UserSummary> List()
    {
        lock (sync)
        {
            return users.Select(ToSummary).ToList();
        }
    }

    private static bool IsActiveAdmin(UserAccount user) => user.Role == AdminRole && !user.Disabled;

    private static UserSummary ToSummary(UserAccount user) =>
        new(user.Id, user.Username, user.Role, user.Disabled, user.CreatedUtc);

    private static string NewStamp() => Guid.NewGuid().ToString("N");

    private static void ValidateRole(string role)
    {
        if (role != AdminRole && role != UserRole)
        {
            throw new StorageException(400, "Role must be Admin or User.");
        }
    }

    private int IndexOf(string id)
    {
        var index = users.FindIndex(u => u.Id == id);
        return index >= 0 ? index : throw new StorageException(404, "User not found.");
    }

    private void Persist(List<UserAccount> next)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(next, JsonOptions));
        File.Move(tmp, path, overwrite: true);
        users = next;
    }
}
