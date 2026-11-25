using Microsoft.Data.SqlClient;

namespace Ledger.Infrastructure;

/// <summary>
/// Runs a database call again when it failed only because of bad timing, such as being picked as a deadlock victim.
/// SQL Server rolls a victim's whole transaction back, so nothing was written and running it again is safe. A retried
/// posting also carries the same Idempotency-Key, so even a retry that raced the original can't post twice.
/// </summary>
public sealed class SqlRetryPolicy
{
    /// <summary>What SqlLedger uses unless it's given another: up to 5 attempts, retrying deadlocks only.</summary>
    public static SqlRetryPolicy Default { get; } = new();

    /// <summary>One attempt and no retries: an error goes straight to the caller. Tests use it to see raw deadlocks.</summary>
    public static SqlRetryPolicy None { get; } = new() { MaxAttempts = 1 };

    /// <summary>The most times a call is tried, counting the first. At least 1.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>About how long to wait before the second attempt. Each later wait is about twice the one before.</summary>
    public TimeSpan FirstDelay { get; init; } = TimeSpan.FromMilliseconds(20);

    /// <summary>The longest wait between two attempts, however many there have been.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The SQL Server error numbers worth another try. Anything else goes straight to the caller.</summary>
    public IReadOnlyList<int> RetryOn { get; init; } = [LedgerErrors.Deadlock];

    /// <summary>Called before each retry, with the error and the number of the attempt that failed. The race lab counts with it.</summary>
    public Action<SqlException, int>? OnRetry { get; init; }

    /// <summary>Runs <paramref name="operation"/>, and again after a short wait each time it fails with an error in <see cref="RetryOn"/>.</summary>
    public async Task<T> ExecuteAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxAttempts, 1);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await operation();
            }
            catch (SqlException error) when (attempt < MaxAttempts && RetryOn.Contains(error.Number) && !cancellationToken.IsCancellationRequested)
            {
                OnRetry?.Invoke(error, attempt);
                await Task.Delay(DelayBefore(attempt + 1), cancellationToken);
            }
        }
    }

    /// <summary>
    /// How long to wait before an attempt: about <see cref="FirstDelay"/> before the second, twice that before the third,
    /// and so on up to <see cref="MaxDelay"/>. Half of each wait is fixed and half is random ("jitter"), so callers that
    /// collided don't all come back at the same moment and collide again.
    /// </summary>
    public TimeSpan DelayBefore(int attempt)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(attempt, 2);

        var doubled = FirstDelay.TotalMilliseconds * Math.Pow(2, attempt - 2);
        var ceiling = Math.Min(doubled, MaxDelay.TotalMilliseconds);
        return TimeSpan.FromMilliseconds((ceiling / 2) + (Random.Shared.NextDouble() * ceiling / 2));
    }
}