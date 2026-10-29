/// <summary>In-memory lockout after consecutive failed logins, keyed by lower-cased username.</summary>
sealed class LoginThrottle
{
    private const int MaxEntries = 10_000;

    private sealed class Entry
    {
        public int Failures;
        public DateTimeOffset LastFailure;
        public DateTimeOffset? LockedUntil;
    }

    private readonly int maxFailures;
    private readonly TimeSpan lockoutDuration;
    private readonly TimeProvider time;
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> entries = new();

    public LoginThrottle(int maxFailures = 5, TimeSpan? lockoutDuration = null, TimeProvider? time = null)
    {
        this.maxFailures = maxFailures;
        this.lockoutDuration = lockoutDuration ?? TimeSpan.FromMinutes(15);
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Returns the remaining lockout time, or null when the user may attempt a login.</summary>
    public TimeSpan? GetLockout(string username)
    {
        lock (sync)
        {
            var key = Key(username);
            if (!entries.TryGetValue(key, out var entry) || entry.LockedUntil is null)
            {
                return null;
            }

            var remaining = entry.LockedUntil.Value - time.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                return remaining;
            }

            entries.Remove(key);
            return null;
        }
    }

    public void RecordFailure(string username)
    {
        lock (sync)
        {
            var now = time.GetUtcNow();
            var key = Key(username);
            if (!entries.TryGetValue(key, out var entry))
            {
                MakeRoom(now);
                entry = new Entry();
                entries[key] = entry;
            }
            else if (entry.LockedUntil is { } until && until <= now)
            {
                entry.Failures = 0;
                entry.LockedUntil = null;
            }

            entry.Failures++;
            entry.LastFailure = now;
            if (entry.Failures >= maxFailures)
            {
                entry.LockedUntil = now + lockoutDuration;
            }
        }
    }

    public void RecordSuccess(string username)
    {
        lock (sync)
        {
            entries.Remove(Key(username));
        }
    }

    private static string Key(string username) => username.ToLowerInvariant();

    private void MakeRoom(DateTimeOffset now)
    {
        if (entries.Count < MaxEntries)
        {
            return;
        }

        var stale = entries.Where(e => IsExpired(e.Value, now)).Select(e => e.Key).ToList();
        foreach (var key in stale)
        {
            entries.Remove(key);
        }

        if (entries.Count >= MaxEntries)
        {
            entries.Remove(entries.MinBy(e => e.Value.LastFailure).Key);
        }
    }

    private bool IsExpired(Entry entry, DateTimeOffset now) =>
        entry.LockedUntil is { } until ? until <= now : entry.LastFailure + lockoutDuration <= now;
}
