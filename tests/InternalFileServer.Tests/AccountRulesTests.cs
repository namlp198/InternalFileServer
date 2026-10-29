using Xunit;

public class AccountRulesTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("a.b_c-D9")]
    public void ValidateUsername_AcceptsValid(string name) => AccountRules.ValidateUsername(name);

    [Fact]
    public void ValidateUsername_Accepts32Characters() => AccountRules.ValidateUsername(new string('a', 32));

    [Theory]
    [InlineData("ab")]
    [InlineData("has space")]
    [InlineData("bad/name")]
    [InlineData("<img src=x onerror=alert(1)>")]
    [InlineData("")]
    [InlineData(null)]
    public void ValidateUsername_Rejects(string? name) =>
        Assert.Equal(400, Assert.Throws<StorageException>(() => AccountRules.ValidateUsername(name)).StatusCode);

    [Fact]
    public void ValidateUsername_Rejects33Characters() =>
        Assert.Equal(400, Assert.Throws<StorageException>(() => AccountRules.ValidateUsername(new string('a', 33))).StatusCode);

    [Fact]
    public void ValidatePassword_AcceptsBoundaries()
    {
        AccountRules.ValidatePassword(new string('p', 8), new string('p', 8));
        AccountRules.ValidatePassword(new string('p', 128), new string('p', 128));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(129)]
    public void ValidatePassword_RejectsBadLength(int length) =>
        Assert.Equal(400, Assert.Throws<StorageException>(() =>
            AccountRules.ValidatePassword(new string('p', length), new string('p', length))).StatusCode);

    [Fact]
    public void ValidatePassword_RejectsMismatch() =>
        Assert.Equal(400, Assert.Throws<StorageException>(() => AccountRules.ValidatePassword("password1", "password2")).StatusCode);

    [Fact]
    public void ValidatePassword_HonoursConfiguredMinimum() =>
        Assert.Equal(400, Assert.Throws<StorageException>(() => AccountRules.ValidatePassword("password1", "password1", 12)).StatusCode);

    [Fact]
    public void HashPassword_UsesFormatAndFreshSalt()
    {
        var first = AccountRules.HashPassword("correct horse");
        var second = AccountRules.HashPassword("correct horse");

        Assert.NotEqual(first, second);
        var parts = first.Split('$');
        Assert.Equal(4, parts.Length);
        Assert.Equal("pbkdf2-sha256", parts[0]);
        Assert.Equal("600000", parts[1]);
        Assert.Equal(16, Convert.FromBase64String(parts[2]).Length);
        Assert.Equal(32, Convert.FromBase64String(parts[3]).Length);
    }

    [Fact]
    public void VerifyPassword_AcceptsCorrectAndRejectsWrongOrTampered()
    {
        var hash = AccountRules.HashPassword("correct horse");
        var parts = hash.Split('$');
        var key = Convert.FromBase64String(parts[3]);
        key[0] ^= 1;
        var tampered = $"{parts[0]}${parts[1]}${parts[2]}${Convert.ToBase64String(key)}";

        Assert.True(AccountRules.VerifyPassword("correct horse", hash));
        Assert.False(AccountRules.VerifyPassword("wrong horse", hash));
        Assert.False(AccountRules.VerifyPassword("correct horse", tampered));
        Assert.False(AccountRules.VerifyPassword("correct horse", "garbage"));
        Assert.False(AccountRules.VerifyPassword("correct horse", "pbkdf2-sha256$600000$!!$!!"));
    }

    [Fact]
    public void VerifyDummy_DoesNotThrow() => AccountRules.VerifyDummy("anything");
}
