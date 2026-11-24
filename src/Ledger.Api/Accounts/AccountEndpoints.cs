using Ledger.Api.Authentication;
using Ledger.Api.Errors;
using Ledger.Domain;
using Ledger.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Ledger.Api.Accounts;

public static class AccountEndpoints
{
    public static IEndpointRouteBuilder MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var accounts = app.MapGroup("/accounts").WithTags("Accounts");

        accounts.MapPost("", CreateAsync)
            .RequireAuthorization(LedgerPolicies.Write)
            .WithName("CreateAccount")
            .WithSummary("Open an account")
            .WithDescription("Adds an account to the chart of accounts. The code is the account's natural key: "
                + "retrying a request that timed out can't open a second account, it gets a 409 instead.")
            .Produces<AccountResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        accounts.MapGet("/{id:long}", GetAsync)
            .RequireAuthorization(LedgerPolicies.Read)
            .WithName("GetAccount")
            .WithSummary("An account and its current balance")
            .WithDescription("Until Phase 4 stores balances, the balance is the sum of the account's postings.")
            .Produces<AccountResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }

    private static async Task<IResult> CreateAsync(CreateAccountRequest request, SqlLedger ledger, CancellationToken cancellationToken)
    {
        var errors = request.Validate();
        if (errors.Any)
        {
            return Problems.Validation(errors);
        }

        try
        {
            var account = await ledger.CreateAccountAsync(
                request.Code, request.Name, request.Type, Currency.FromCode(request.Currency), request.AllowOverdraft, cancellationToken);

            return TypedResults.Created($"/accounts/{account.Id}", AccountResponse.From(account, Money.Zero(account.Currency)));
        }
        catch (SqlException error) when (error.Number == LedgerErrors.DuplicateKey && error.Message.Contains("UQ_Accounts_Code"))
        {
            return Problems.Result(StatusCodes.Status409Conflict, ProblemCodes.DuplicateAccountCode,
                "Account code already in use", $"An account with code '{request.Code}' already exists.");
        }
    }

    private static async Task<IResult> GetAsync(long id, SqlLedger ledger, CancellationToken cancellationToken)
    {
        var account = await ledger.GetAccountAsync(id, cancellationToken);
        if (account is null)
        {
            return Problems.Result(StatusCodes.Status404NotFound, ProblemCodes.AccountNotFound,
                "Account not found", $"Account {id} does not exist.");
        }

        var storedBalance = await ledger.GetStoredBalanceAsync(account, cancellationToken);
        return TypedResults.Ok(AccountResponse.From(account, storedBalance));
    }
}