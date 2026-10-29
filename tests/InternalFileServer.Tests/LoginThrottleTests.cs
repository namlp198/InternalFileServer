using Xunit;

public class LoginThrottleTests
{
    private sealed class FakeTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static void Fail(LoginThrottle throttle, string user, int times)
    {
        for (var i = 0; i < times; i++)
        {
            throttle.RecordFailure(user);
        }
    }

    [Fact]
    public void FourFailuresDoNotLock()
    {
        var throttle = new LoginThrottle(time: new FakeTime());
        Fail(throttle, "alice", 4);
        Assert.Null(throttle.GetLockout("alice"));
    }

    [Fact]
    public void FiveFailuresLockUntilFifteenMinutesPass()
    {
        var time = new FakeTime();
        var throttle = new LoginThrottle(time: time);
        Fail(throttle, "Alice", 5);

        Assert.NotNull(throttle.GetLockout("alice"));
        time.Now += TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1);
        Assert.NotNull(throttle.GetLockout("ALICE"));
        time.Now += TimeSpan.FromSeconds(1);
        Assert.Null(throttle.GetLockout("alice"));
    }

    [Fact]
    public void CountStartsOverAfterLockoutExpires()
    {
        var time = new FakeTime();
        var throttle = new LoginThrottle(time: time);
        Fail(throttle, "alice", 5);
        time.Now += TimeSpan.FromMinutes(16);

        Fail(throttle, "alice", 4);

        Assert.Null(throttle.GetLockout("alice"));
    }

    [Fact]
    public void SuccessResetsCount()
    {
        var throttle = new LoginThrottle(time: new FakeTime());
        Fail(throttle, "alice", 4);
        throttle.RecordSuccess("alice");
        Fail(throttle, "alice", 4);
        Assert.Null(throttle.GetLockout("alice"));
    }

    [Fact]
    public void SuccessClearsLockout()
    {
        var throttle = new LoginThrottle(time: new FakeTime());
        Fail(throttle, "alice", 5);
        throttle.RecordSuccess("alice");
        Assert.Null(throttle.GetLockout("alice"));
    }

    [Fact]
    public void UsersAreIndependent()
    {
        var throttle = new LoginThrottle(time: new FakeTime());
        Fail(throttle, "alice", 5);
        Assert.Null(throttle.GetLockout("bob"));
    }

    [Fact]
    public void TableIsCappedAtTenThousandEntries()
    {
        var throttle = new LoginThrottle(time: new FakeTime());
        for (var i = 0; i < 10_050; i++)
        {
            throttle.RecordFailure("user" + i);
        }

        var field = typeof(LoginThrottle).GetField("entries", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        Assert.Equal(10_000, ((System.Collections.ICollection)field.GetValue(throttle)!).Count);
    }
}
