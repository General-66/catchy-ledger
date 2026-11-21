using Ledger.Infrastructure;
using Ledger.Tests.Integration.Support;
using Microsoft.Data.SqlClient;
using static Ledger.Tests.Integration.Support.TestBooks;

namespace Ledger.Tests.Integration;

/// <summary>Mistakes are fixed by posting the mirror image, once, and never by editing what's there.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class ReversalTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private LedgerDatabase _database = null!;
    private SqlLedger _ledger = null!;
    private TestBooks _books = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.DatabaseForAsync(nameof(ReversalTests));
        _ledger = _database.AsApp();
        _books = await TestBooks.CreateAsync(_ledger);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_reversal_posts_the_mirror_image_and_leaves_the_original_untouched()
    {
        await _ledger.PostAsync(_books.Deposit(500_00), TestRequests.NewRequest());
        var betId = await _ledger.PostAsync(_books.PlaceBet(100_00), TestRequests.NewRequest());

        var reversalId = await _ledger.ReverseAsync(betId, Day, TestRequests.NewRequest());

        var original = await _ledger.GetTransactionAsync(betId);
        var reversal = await _ledger.GetTransactionAsync(reversalId);
        Assert.NotNull(original);
        Assert.NotNull(reversal);
        Assert.Equal(betId, reversal.ReversesTransactionId);
        Assert.Equal($"Reversal of {betId}: Bet placed", reversal.Description);
        Assert.Equal(new long[] { 10_000, -10_000 }, original.Lines.Select(line => line.AmountMinor));
        Assert.Equal(new long[] { -10_000, 10_000 }, reversal.Lines.Select(line => line.AmountMinor));
        Assert.Equal(original.Lines.Select(line => line.AccountId), reversal.Lines.Select(line => line.AccountId));

        // The bet and its reversal cancel out: the wallet is back to the R500 deposited, Open Bets to nothing.
        Assert.Equal(Zar(-500_00), await _ledger.GetStoredBalanceAsync(_books.CustomerWallet));
        Assert.Equal(Zar(0), await _ledger.GetStoredBalanceAsync(_books.OpenBets));
        Assert.Equal(0L, await _database.TrialBalanceAsync());
    }

    [Fact]
    public async Task A_transaction_can_be_reversed_only_once()
    {
        var depositId = await _ledger.PostAsync(_books.Deposit(500_00), TestRequests.NewRequest());
        await _ledger.ReverseAsync(depositId, Day, TestRequests.NewRequest());

        var error = await Assert.ThrowsAsync<SqlException>(
            () => _ledger.ReverseAsync(depositId, Day, TestRequests.NewRequest()));

        Assert.Equal(LedgerErrors.AlreadyReversed, error.Number);
    }

    [Fact]
    public async Task A_reversal_cannot_itself_be_reversed()
    {
        var depositId = await _ledger.PostAsync(_books.Deposit(500_00), TestRequests.NewRequest());
        var reversalId = await _ledger.ReverseAsync(depositId, Day, TestRequests.NewRequest());

        var error = await Assert.ThrowsAsync<SqlException>(
            () => _ledger.ReverseAsync(reversalId, Day, TestRequests.NewRequest()));

        Assert.Equal(LedgerErrors.ReversalOfReversal, error.Number);
    }

    [Fact]
    public async Task Reversing_a_transaction_that_does_not_exist_is_rejected()
    {
        var error = await Assert.ThrowsAsync<SqlException>(
            () => _ledger.ReverseAsync(long.MaxValue, Day, TestRequests.NewRequest()));

        Assert.Equal(LedgerErrors.TransactionNotFound, error.Number);
    }

    [Fact]
    public async Task Two_reversals_at_the_same_moment_only_one_gets_in()
    {
        var depositId = await _ledger.PostAsync(_books.Deposit(500_00), TestRequests.NewRequest());

        // Both may pass the "already reversed?" check before either inserts. The unique index decides.
        var attempts = await Task.WhenAll(
            TryReverseAsync(depositId),
            TryReverseAsync(depositId));

        Assert.Single(attempts, attempt => attempt is null);
        Assert.Single(attempts, attempt => attempt == LedgerErrors.AlreadyReversed);
        Assert.Equal(Zar(0), await _ledger.GetStoredBalanceAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task Even_the_administrator_cannot_insert_a_second_reversal()
    {
        var depositId = await _ledger.PostAsync(_books.Deposit(500_00), TestRequests.NewRequest());
        await _ledger.ReverseAsync(depositId, Day, TestRequests.NewRequest());

        // Skipping the procedure entirely still runs into the unique index on ReversesTransactionId.
        var error = await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsAdminAsync(
            """
            INSERT INTO dbo.Transactions (CreatedBy, IdempotencyKey, RequestHash, Description, EffectiveDate, ReversesTransactionId)
            VALUES (N'sa', NEWID(), HASHBYTES('SHA2_256', N'{}'), N'Second reversal', '2026-11-19', @Id);
            """,
            new { Id = depositId }));

        Assert.Equal(LedgerErrors.DuplicateIndexKey, error.Number);
    }

    // Returns null if the reversal went in, or the SQL Server error number if it was refused.
    private async Task<int?> TryReverseAsync(long transactionId)
    {
        try
        {
            await _ledger.ReverseAsync(transactionId, Day, TestRequests.NewRequest());
            return null;
        }
        catch (SqlException error)
        {
            return error.Number;
        }
    }
}