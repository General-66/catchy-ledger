using Microsoft.Data.SqlClient;

namespace Ledger.Tests.Integration.Support;

/// <summary>Helpers for firing many calls at exactly the same moment, the way a real race happens.</summary>
internal static class Race
{
    /// <summary>
    /// Lines up <paramref name="runners"/> calls, starts them all at once like runners at a starting gun, and waits
    /// for every one to finish. Each call is given its runner's number, from 0.
    /// </summary>
    public static async Task<T[]> AllAtOnceAsync<T>(int runners, Func<int, Task<T>> call)
    {
        var startingGun = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var calls = Enumerable.Range(0, runners).Select(async runner =>
        {
            await startingGun.Task;
            return await call(runner);
        }).ToArray();

        startingGun.SetResult();
        return await Task.WhenAll(calls);
    }

    /// <summary>How a database call ended: null if it went through, or the number of the SQL Server error that stopped it.</summary>
    public static async Task<int?> OutcomeOfAsync(Func<Task> call)
    {
        try
        {
            await call();
            return null;
        }
        catch (SqlException error)
        {
            return error.Number;
        }
    }
}