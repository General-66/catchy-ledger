using Ledger.Infrastructure;
using Ledger.Tests.Integration.Support;
using Microsoft.Data.SqlClient;

namespace Ledger.Tests.Integration;

/// <summary>
/// The retry policy, with real SqlExceptions. A deadlock can't be made to order, so the tests ask SQL Server to
/// raise an error number of their own and tell the policy to treat it the way it treats a deadlock.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class SqlRetryPolicyTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    /// <summary>An error number no procedure uses, standing in for a deadlock.</summary>
    private const int Flaky = 50099;

    private LedgerDatabase _database = null!;

    public async Task InitializeAsync() => _database = await sqlServer.DatabaseForAsync(nameof(SqlRetryPolicyTests));

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_error_on_the_list_is_retried_until_the_call_succeeds()
    {
        var retried = new List<int>();
        var policy = new SqlRetryPolicy { RetryOn = [Flaky], FirstDelay = TimeSpan.FromMilliseconds(1), OnRetry = (_, attempt) => retried.Add(attempt) };
        var calls = 0;

        var result = await policy.ExecuteAsync(async () =>
        {
            calls++;
            if (calls < 3)
            {
                await RaiseAsync(Flaky);
            }

            return "posted";
        });

        Assert.Equal("posted", result);
        Assert.Equal(3, calls);
        Assert.Equal(new[] { 1, 2 }, retried);
    }

    [Fact]
    public async Task An_error_that_is_not_on_the_list_goes_straight_to_the_caller()
    {
        var policy = new SqlRetryPolicy { RetryOn = [Flaky] };
        var calls = 0;

        var error = await Assert.ThrowsAsync<SqlException>(() => policy.ExecuteAsync(async () =>
        {
            calls++;
            await RaiseAsync(LedgerErrors.InsufficientFunds);
            return 0;
        }));

        Assert.Equal(LedgerErrors.InsufficientFunds, error.Number);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task After_the_last_attempt_the_error_reaches_the_caller()
    {
        var policy = new SqlRetryPolicy { RetryOn = [Flaky], MaxAttempts = 3, FirstDelay = TimeSpan.FromMilliseconds(1) };
        var calls = 0;

        var error = await Assert.ThrowsAsync<SqlException>(() => policy.ExecuteAsync(async () =>
        {
            calls++;
            await RaiseAsync(Flaky);
            return 0;
        }));

        Assert.Equal(Flaky, error.Number);
        Assert.Equal(3, calls);
    }

    // Asks SQL Server to raise an error with this number, so the policy sees a real SqlException.
    private Task RaiseAsync(int number) =>
        _database.ExecuteAsAdminAsync("THROW @Number, N'Raised on purpose by a test.', 1;", new { Number = number });
}