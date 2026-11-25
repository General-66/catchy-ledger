using Ledger.Domain;
using Ledger.Infrastructure;
using Ledger.Tests.Integration.Support;
using Microsoft.Data.SqlClient;
using static Ledger.Tests.Integration.Support.TestBooks;

namespace Ledger.Tests.Integration;

/// <summary>
/// Stored balances and the insufficient-funds rule, one posting at a time: every balance moves with its postings,
/// a wallet can be spent to zero but no further, and nobody can change a balance except by posting.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class BalanceTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private LedgerDatabase _database = null!;
    private SqlLedger _ledger = null!;
    private TestBooks _books = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.DatabaseForAsync(nameof(BalanceTests));
        _ledger = _database.AsApp();
        _books = await TestBooks.CreateAsync(_ledger);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Opening_an_account_gives_it_a_stored_balance_of_zero()
    {
        var stored = await _database.ScalarAsync<long>(
            "SELECT BalanceMinor FROM dbo.AccountBalances WHERE AccountId = @Id;", new { Id = _books.CustomerWallet.Id });

        Assert.Equal(0L, stored);
        Assert.Equal(Zar(0), await _ledger.GetStoredBalanceAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task Every_posting_moves_the_stored_balances_and_they_agree_with_the_postings()
    {
        await _ledger.PostAsync(_books.Deposit(500_00), TestRequests.NewRequest());
        await _ledger.PostAsync(_books.PlaceBet(100_00), TestRequests.NewRequest());
        await _ledger.PostAsync(_books.BetWins(stake: 100_00, payout: 250_00), TestRequests.NewRequest());

        // Phase 1's table for step 3b once more, now read from AccountBalances instead of added up from the postings.
        Assert.Equal(Zar(500_00), await OnNormalSideAsync(_books.Bank));
        Assert.Equal(Zar(650_00), await OnNormalSideAsync(_books.CustomerWallet));
        Assert.Equal(Zar(0), await OnNormalSideAsync(_books.OpenBets));
        Assert.Equal(Zar(150_00), await OnNormalSideAsync(_books.Payouts));
        Assert.Empty(await _database.AccountsWithWrongBalancesAsync());
    }

    [Fact]
    public async Task A_bet_bigger_than_the_wallet_is_refused_and_nothing_is_written()
    {
        await _ledger.PostAsync(_books.Deposit(100_00), TestRequests.NewRequest());
        var transactionsBefore = await _database.TransactionCountAsync();
        var postingsBefore = await _database.PostingCountAsync();

        var error = await Assert.ThrowsAsync<SqlException>(
            () => _ledger.PostAsync(_books.PlaceBet(100_01), TestRequests.NewRequest()));

        Assert.Equal(LedgerErrors.InsufficientFunds, error.Number);
        Assert.Contains($"account {_books.CustomerWallet.Code}: it holds 10000 minor units and this transaction takes 10001", error.Message);
        Assert.Equal(transactionsBefore, await _database.TransactionCountAsync());
        Assert.Equal(postingsBefore, await _database.PostingCountAsync());
        Assert.Equal(Zar(100_00), await OnNormalSideAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task A_wallet_can_be_spent_down_to_exactly_zero_but_not_a_cent_further()
    {
        await _ledger.PostAsync(_books.Deposit(100_00), TestRequests.NewRequest());

        await _ledger.PostAsync(_books.PlaceBet(100_00), TestRequests.NewRequest());
        var error = await Assert.ThrowsAsync<SqlException>(
            () => _ledger.PostAsync(_books.PlaceBet(1), TestRequests.NewRequest()));

        Assert.Equal(LedgerErrors.InsufficientFunds, error.Number);
        Assert.Equal(Zar(0), await OnNormalSideAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task An_account_that_allows_overdrafts_may_go_below_zero()
    {
        // The operator pays R50 out of a Bank account that holds nothing yet. Bank allows overdrafts; wallets don't.
        var payout = Transaction.Create("Paid from an empty bank", Day, Posting.Debit(_books.Payouts, Zar(50_00)), Posting.Credit(_books.Bank, Zar(50_00)));

        await _ledger.PostAsync(payout, TestRequests.NewRequest());

        Assert.Equal(Zar(-50_00), await OnNormalSideAsync(_books.Bank));
    }

    [Fact]
    public async Task Reversing_a_deposit_the_customer_has_already_spent_is_refused()
    {
        var depositId = await _ledger.PostAsync(_books.Deposit(500_00), TestRequests.NewRequest());
        await _ledger.PostAsync(_books.PlaceBet(400_00), TestRequests.NewRequest());

        // Taking the R500 back would leave the wallet R400 overdrawn. A reversal follows the same rule as any posting.
        var error = await Assert.ThrowsAsync<SqlException>(
            () => _ledger.ReverseAsync(depositId, Day, TestRequests.NewRequest()));

        Assert.Equal(LedgerErrors.InsufficientFunds, error.Number);
        Assert.Equal(Zar(100_00), await OnNormalSideAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task The_app_cannot_change_a_stored_balance_directly()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsAppAsync(
            "UPDATE dbo.AccountBalances SET BalanceMinor = -1000000 WHERE AccountId = @Id;", new { Id = _books.CustomerWallet.Id }));

        Assert.Equal(LedgerErrors.PermissionDenied, error.Number);
        Assert.Equal(Zar(0), await OnNormalSideAsync(_books.CustomerWallet));
    }

    [Fact]
    public async Task The_app_cannot_open_an_account_without_its_balance()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsAppAsync(
            "INSERT INTO dbo.Accounts (Code, Name, Type, Currency, AllowOverdraft) VALUES ('9999-direct', N'Direct', 'Asset', 'ZAR', 1);"));

        Assert.Equal(LedgerErrors.PermissionDenied, error.Number);
    }

    [Fact]
    public async Task The_app_cannot_skip_the_checks_by_calling_usp_WriteTransaction_directly()
    {
        // usp_WriteTransaction trusts its two callers to have checked the lines. Here are lines that don't balance.
        var transactionsBefore = await _database.TransactionCountAsync();

        var error = await Assert.ThrowsAsync<SqlException>(() => _database.ExecuteAsAppAsync(
            """
            DECLARE @Lines dbo.PostingLineList, @Hash binary(32) = HASHBYTES('SHA2_256', N'{}'), @Id bigint;

            INSERT INTO @Lines (LineNumber, AccountId, AmountMinor, Currency)
            VALUES (1, @Bank, 1000000, 'ZAR'), (2, @Wallet, -1, 'ZAR');

            EXECUTE dbo.usp_WriteTransaction
                @CreatedBy = N'sneaky', @IdempotencyKey = N'sneaky-1', @RequestHash = @Hash,
                @Description = N'Unbalanced, skipping every check', @EffectiveDate = '2026-11-19',
                @ReversesTransactionId = NULL, @Lines = @Lines, @TransactionId = @Id OUTPUT;
            """,
            new { Bank = _books.Bank.Id, Wallet = _books.CustomerWallet.Id }));

        Assert.Equal(LedgerErrors.PermissionDenied, error.Number);
        Assert.Equal(transactionsBefore, await _database.TransactionCountAsync());
    }

    private async Task<Money> OnNormalSideAsync(Account account) =>
        account.BalanceOnNormalSide(await _ledger.GetStoredBalanceAsync(account));
}