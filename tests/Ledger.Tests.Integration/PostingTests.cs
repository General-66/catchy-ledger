using Ledger.Domain;
using Ledger.Infrastructure;
using Ledger.Tests.Integration.Support;
using Microsoft.Data.SqlClient;
using static Ledger.Tests.Integration.Support.TestBooks;

namespace Ledger.Tests.Integration;

/// <summary>
/// Posting through the app's restricted database user: the betting example end to end, and every rule
/// the database enforces on its own, without any help from the C# domain.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class PostingTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private LedgerDatabase _database = null!;
    private SqlLedger _ledger = null!;
    private TestBooks _books = null!;

    public async Task InitializeAsync()
    {
        _database = await sqlServer.DatabaseForAsync(nameof(PostingTests));
        _ledger = _database.AsApp();
        _books = await TestBooks.CreateAsync(_ledger);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task A_deposit_is_stored_as_one_transaction_with_two_signed_postings()
    {
        var id = await _ledger.PostAsync(_books.Deposit(500_00), TestRequests.NewRequest());

        var stored = await _ledger.GetTransactionAsync(id);

        Assert.NotNull(stored);
        Assert.Equal("Customer deposit", stored.Description);
        Assert.Equal(Day, stored.EffectiveDate);
        Assert.Equal(TestRequests.Caller, stored.CreatedBy);
        Assert.Null(stored.ReversesTransactionId);
        Assert.Collection(
            stored.Lines,
            line => Assert.Equal((_books.Bank.Id, 50_000L, "ZAR"), (line.AccountId, line.AmountMinor, line.Currency)),
            line => Assert.Equal((_books.CustomerWallet.Id, -50_000L, "ZAR"), (line.AccountId, line.AmountMinor, line.Currency)));
    }

    [Fact]
    public async Task The_betting_example_leaves_every_balance_right_and_the_trial_balance_at_zero()
    {
        await _ledger.PostAsync(_books.Deposit(500_00), TestRequests.NewRequest());
        await _ledger.PostAsync(_books.PlaceBet(100_00), TestRequests.NewRequest());
        await _ledger.PostAsync(_books.BetWins(stake: 100_00, payout: 250_00), TestRequests.NewRequest());

        // The same numbers as Phase 1's table for step 3b, now read back from SQL Server.
        Assert.Equal(Zar(500_00), await OnNormalSideAsync(_books.Bank));
        Assert.Equal(Zar(650_00), await OnNormalSideAsync(_books.CustomerWallet));
        Assert.Equal(Zar(0), await OnNormalSideAsync(_books.OpenBets));
        Assert.Equal(Zar(150_00), await OnNormalSideAsync(_books.Payouts));
        Assert.Equal(0L, await _database.TrialBalanceAsync());
    }

    [Fact]
    public async Task An_account_reads_back_exactly_as_it_was_opened()
    {
        var wallet = await _ledger.GetAccountAsync(_books.CustomerWallet.Id);

        Assert.NotNull(wallet);
        Assert.Equal(_books.CustomerWallet.Code, wallet.Code);
        Assert.Equal(AccountType.Liability, wallet.Type);
        Assert.Equal(NormalSide.Credit, wallet.NormalSide);
        Assert.Equal(Currency.ZAR, wallet.Currency);
        Assert.False(wallet.AllowOverdraft);
    }

    [Fact]
    public async Task The_database_rejects_lines_that_do_not_sum_to_zero_even_without_the_domain()
    {
        var postingsBefore = await _database.PostingCountAsync();

        var error = await Assert.ThrowsAsync<SqlException>(() => RawPostings.PostAsync(
            _database.AppConnectionString,
            (_books.Bank.Id, 500_00, "ZAR"),
            (_books.CustomerWallet.Id, -499_99, "ZAR")));

        Assert.Equal(LedgerErrors.Unbalanced, error.Number);
        Assert.Contains("sum to 1 minor units", error.Message);
        Assert.Equal(postingsBefore, await _database.PostingCountAsync()); // nothing was half-written
    }

    [Fact]
    public async Task The_database_rejects_a_single_line()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => RawPostings.PostAsync(
            _database.AppConnectionString,
            (_books.Bank.Id, 500_00, "ZAR")));

        Assert.Equal(LedgerErrors.TooFewPostings, error.Number);
    }

    [Fact]
    public async Task The_database_rejects_zero_lines_even_though_they_balance()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => RawPostings.PostAsync(
            _database.AppConnectionString,
            (_books.Bank.Id, 0, "ZAR"),
            (_books.CustomerWallet.Id, 0, "ZAR")));

        Assert.Equal(LedgerErrors.ZeroAmountPosting, error.Number);
    }

    [Fact]
    public async Task The_database_rejects_lines_in_two_currencies()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => RawPostings.PostAsync(
            _database.AppConnectionString,
            (_books.Bank.Id, 100_00, "ZAR"),
            (_books.CustomerWallet.Id, -100_00, "USD")));

        Assert.Equal(LedgerErrors.MixedCurrencies, error.Number);
    }

    [Fact]
    public async Task The_database_rejects_dollars_posted_to_rand_accounts()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => RawPostings.PostAsync(
            _database.AppConnectionString,
            (_books.Bank.Id, 100_00, "USD"),
            (_books.CustomerWallet.Id, -100_00, "USD")));

        Assert.Equal(LedgerErrors.AccountCurrencyMismatch, error.Number);
    }

    [Fact]
    public async Task The_database_rejects_an_account_that_does_not_exist()
    {
        var error = await Assert.ThrowsAsync<SqlException>(() => RawPostings.PostAsync(
            _database.AppConnectionString,
            (_books.Bank.Id, 100_00, "ZAR"),
            (long.MaxValue, -100_00, "ZAR")));

        Assert.Equal(LedgerErrors.UnknownAccount, error.Number);
    }

    [Fact]
    public async Task An_idempotency_key_can_post_only_once()
    {
        var request = TestRequests.NewRequest();
        await _ledger.PostAsync(_books.Deposit(500_00), request);

        var error = await Assert.ThrowsAsync<SqlException>(() => _ledger.PostAsync(_books.Deposit(500_00), request));

        Assert.Equal(LedgerErrors.DuplicateKey, error.Number);
        Assert.Equal(Zar(500_00), await OnNormalSideAsync(_books.CustomerWallet)); // deposited once, not twice
    }

    [Fact]
    public async Task Two_callers_can_use_the_same_idempotency_key()
    {
        var key = Guid.NewGuid().ToString();
        var hash = RequestInfo.HashOf("{}");

        await _ledger.PostAsync(_books.Deposit(100_00), new RequestInfo("caller-a", key, hash));
        await _ledger.PostAsync(_books.Deposit(100_00), new RequestInfo("caller-b", key, hash));

        Assert.Equal(Zar(200_00), await OnNormalSideAsync(_books.CustomerWallet));
    }

    private async Task<Money> OnNormalSideAsync(Account account) =>
        account.BalanceOnNormalSide(await _ledger.GetStoredBalanceAsync(account));
}