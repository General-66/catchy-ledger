using System.Net;
using System.Text.Json;
using Ledger.Api.Accounts;
using Ledger.Api.Errors;
using Ledger.Domain;
using Ledger.Tests.Integration.Support;

namespace Ledger.Tests.Integration.Api;

/// <summary>POST /accounts and GET /accounts/{id}: opening accounts, reading balances both ways, and the seeded house accounts.</summary>
[Collection(SqlServerCollection.Name)]
public sealed class AccountEndpointTests(SqlServerFixture sqlServer) : IAsyncLifetime
{
    private HttpClient _writer = null!;

    public async Task InitializeAsync() =>
        _writer = (await sqlServer.ApiForAsync(nameof(AccountEndpointTests))).ClientWith(TestApiKeys.Writer);

    public Task DisposeAsync()
    {
        _writer.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Opening_an_account_returns_201_with_its_location_and_a_zero_balance()
    {
        var code = NewWalletCode();

        var response = await _writer.PostJsonAsync("/accounts",
            new CreateAccountRequest(code, "Customer Wallet", AccountType.Liability, "ZAR", AllowOverdraft: false));

        await response.ShouldBeAsync(HttpStatusCode.Created);
        var account = await response.ReadJsonAsync<AccountResponse>();
        Assert.Equal($"/accounts/{account.Id}", response.Headers.Location?.OriginalString);
        Assert.Equal(code, account.Code);
        Assert.Equal(AccountType.Liability, account.Type);
        Assert.Equal(NormalSide.Credit, account.NormalSide);
        Assert.False(account.AllowOverdraft);
        Assert.Equal(new BalanceResponse(0, 0, "ZAR 0.00"), account.Balance);
    }

    [Fact]
    public async Task A_funded_wallet_shows_its_balance_as_stored_and_on_its_normal_side()
    {
        var books = await ApiBooks.OpenAsync(_writer);
        await _writer.PostTransactionAsync(books.Deposit(500_00));

        var wallet = await _writer.GetAccountAsync(books.CustomerWallet);
        var bank = await _writer.GetAccountAsync(books.Bank);

        // The plan's Normal balances table: both read R500, but a liability is stored with a minus sign.
        Assert.Equal(new BalanceResponse(-50_000, 50_000, "ZAR 500.00"), wallet.Balance);
        Assert.Equal(new BalanceResponse(50_000, 50_000, "ZAR 500.00"), bank.Balance);
    }

    [Fact]
    public async Task A_second_account_with_the_same_code_is_rejected_with_409()
    {
        var request = new CreateAccountRequest(NewWalletCode(), "Customer Wallet", AccountType.Liability, "ZAR", AllowOverdraft: false);
        await (await _writer.PostJsonAsync("/accounts", request)).ShouldBeAsync(HttpStatusCode.Created);

        var retry = await _writer.PostJsonAsync("/accounts", request);

        await retry.ShouldBeAsync(HttpStatusCode.Conflict);
        Assert.Equal(ProblemCodes.DuplicateAccountCode, await retry.ProblemCodeAsync());
    }

    [Fact]
    public async Task An_invalid_account_lists_every_problem_in_one_400()
    {
        var response = await _writer.PostJsonAsync("/accounts",
            new CreateAccountRequest("wallet #1", " ", AccountType.Liability, "zar", AllowOverdraft: false));

        await response.ShouldBeAsync(HttpStatusCode.BadRequest);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(ProblemCodes.ValidationFailed, problem.RootElement.GetProperty("code").GetString());
        var fields = problem.RootElement.GetProperty("errors").EnumerateObject().Select(field => field.Name).Order();
        Assert.Equal(new[] { "code", "currency", "name" }, fields);
    }

    [Fact]
    public async Task An_unknown_account_type_is_rejected_with_400()
    {
        var response = await _writer.PostJsonAsync("/accounts",
            new { code = NewWalletCode(), name = "Wallet", type = "Wallet", currency = "ZAR", allowOverdraft = false });

        await response.ShouldBeAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ProblemCodes.MalformedRequest, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task A_missing_field_is_a_400_never_a_silent_default()
    {
        // No allowOverdraft. Whether an account may go negative is too important to leave to a default.
        var response = await _writer.PostJsonAsync("/accounts",
            new { code = NewWalletCode(), name = "Wallet", type = "Liability", currency = "ZAR" });

        await response.ShouldBeAsync(HttpStatusCode.BadRequest);
        Assert.Equal(ProblemCodes.MalformedRequest, await response.ProblemCodeAsync());
    }

    [Fact]
    public async Task An_account_that_does_not_exist_is_404()
    {
        var response = await _writer.GetAsync($"/accounts/{long.MaxValue}");

        await response.ShouldBeAsync(HttpStatusCode.NotFound);
        Assert.Equal(ProblemCodes.AccountNotFound, await response.ProblemCodeAsync());
    }

    [Theory]
    [InlineData(1, "1000", "Bank", AccountType.Asset)]
    [InlineData(2, "2100", "Open Bets", AccountType.Liability)]
    [InlineData(3, "4000", "Betting Revenue", AccountType.Income)]
    [InlineData(4, "5000", "Payouts", AccountType.Expense)]
    public async Task The_seeded_house_accounts_are_there(long id, string code, string name, AccountType type)
    {
        var account = await _writer.GetAccountAsync(id);

        Assert.Equal((code, name, type, "ZAR", true), (account.Code, account.Name, account.Type, account.Currency, account.AllowOverdraft));
    }

    private static string NewWalletCode() => $"2000-{Guid.NewGuid().ToString("N")[..8]}";
}