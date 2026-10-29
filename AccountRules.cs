using System.Security.Cryptography;
using System.Text.RegularExpressions;

/// <summary>Pure validation and password-hashing rules for user accounts. Performs no IO.</summary>
static class AccountRules
{
    public const int DefaultMinPasswordLength = 8;
    public const int MaxPasswordLength = 128;

    private const string Algorithm = "pbkdf2-sha256";
    private const int Iterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    private static readonly Regex UsernamePattern = new(@"^[A-Za-z0-9._-]{3,32}$", RegexOptions.CultureInvariant);

    private static readonly Lazy<string> DummyHash = new(() => HashPassword("dummy-password-for-timing"));

    /// <summary>Throws a 400 StorageException unless the username is 3-32 characters of letters, digits, '.', '_' or '-'.</summary>
    public static void ValidateUsername(string? username)
    {
        if (username is null || !UsernamePattern.IsMatch(username))
        {
            throw new StorageException(400, "Username must be 3-32 characters: letters, digits, '.', '_' or '-'.");
        }
    }

    /// <summary>Throws a 400 StorageException unless the password has an allowed length and equals its confirmation.</summary>
    public static void ValidatePassword(string? password, string? confirm, int minLength = DefaultMinPasswordLength)
    {
        if (string.IsNullOrEmpty(password) || password.Length < minLength)
        {
            throw new StorageException(400, $"Password must be at least {minLength} characters.");
        }

        if (password.Length > MaxPasswordLength)
        {
            throw new StorageException(400, $"Password must be at most {MaxPasswordLength} characters.");
        }

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            throw new StorageException(400, "Password confirmation does not match.");
        }
    }

    /// <summary>Hashes a password into `pbkdf2-sha256$iterations$salt$hash` with a fresh random salt.</summary>
    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, KeySize);
        return $"{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(key)}";
    }

    /// <summary>Returns true when the password matches the stored hash; malformed hashes never match.</summary>
    public static bool VerifyPassword(string password, string storedHash)
    {
        var parts = storedHash.Split('$');
        if (parts.Length != 4 || parts[0] != Algorithm || !int.TryParse(parts[1], out var iterations) || iterations <= 0)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (expected.Length == 0)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Spends the cost of a verification against a dummy hash so unknown usernames take as long as known ones.</summary>
    public static void VerifyDummy(string password) => VerifyPassword(password, DummyHash.Value);
}
