using Ledger.Api.Authentication;
using Ledger.Api.Errors;
using Ledger.Domain;
using Ledger.Infrastructure;
using Microsoft.AspNetCore.Mvc;

namespace Ledger.Api.Transactions;

public static class TransactionEndpoints
{
    public static IEndpointRouteBuilder MapTransactionEndpoints(this IEndpointRouteBuilder app)
    {
        var transactions = app.MapGroup("/transactions").WithTags("Transactions");

        transactions.MapPost("", PostAsync)
            .RequireAuthorization(LedgerPolicies.Write)
            .WithName("PostTransaction")
            .WithSummary("Post a balanced transaction")
            .WithDescription("Every line debits or credits one account; debits must equal credits. Send an Idempotency-Key: "
                + "retrying with the same key and body returns the original transaction instead of posting it again. "
                + "A transaction that would take an account that can't be overdrawn below zero, such as a wallet, "
                + "is refused with 422 InsufficientFunds.")
            .Produces<TransactionResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        transactions.MapGet("/{id:long}", GetAsync)
            .RequireAuthorization(LedgerPolicies.Read)
            .WithName("GetTransaction")
            .WithSummary("A transaction and its lines")
            .Produces<TransactionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        transactions.MapPost("/{id:long}/reverse", ReverseAsync)
            .RequireAuthorization(LedgerPolicies.Write)
            .WithName("ReverseTransaction")
            .WithSummary("Reverse a transaction")
            .WithDescription("Posts the mirror image of a transaction: the same lines with debits and credits swapped. "
                + "A transaction can be reversed once, and a reversal can't be reversed. Needs an Idempotency-Key. "
                + "Like any posting, it's refused with 422 InsufficientFunds if it would overdraw a wallet.")
            .Produces<TransactionResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return app;
    }

    private static async Task<IResult> PostAsync(
        PostTransactionRequest request,
        [FromHeader(Name = IdempotentPosting.KeyHeader)] string? idempotencyKey,
        HttpContext http,
        SqlLedger ledger,
        BusinessCalendar calendar,
        CancellationToken cancellationToken)
    {
        if (!IdempotentPosting.TryReadKey(idempotencyKey, out var key, out var keyProblem))
        {
            return keyProblem;
        }

        var errors = request.Validate();
        if (errors.Any)
        {
            return Problems.Validation(errors);
        }

        var accounts = await ledger.GetAccountsAsync(request.Lines.Select(line => line.AccountId), cancellationToken);
        var unknown = request.Lines.FirstOrDefault(line => !accounts.ContainsKey(line.AccountId));
        if (unknown is not null)
        {
            return Problems.Result(StatusCodes.Status422UnprocessableEntity, ProblemCodes.UnknownAccount,
                "Unknown account", $"Account {unknown.AccountId} does not exist.");
        }

        // The domain checks every accounting rule. A broken one throws LedgerRuleException, which becomes a 422.
        var transaction = Transaction.Create(request.Description, request.EffectiveDate ?? calendar.Today, request.ToPostings(accounts));

        var requestInfo = IdempotentPosting.Describe(http, "POST /transactions", key, request);
        return await IdempotentPosting.PostOnceAsync(http, ledger, requestInfo,
            () => ledger.PostAsync(transaction, requestInfo, cancellationToken), cancellationToken);
    }

    private static async Task<IResult> GetAsync(long id, SqlLedger ledger, CancellationToken cancellationToken)
    {
        var transaction = await ledger.GetTransactionAsync(id, cancellationToken);
        return transaction is null
            ? Problems.Result(StatusCodes.Status404NotFound, ProblemCodes.TransactionNotFound,
                "Transaction not found", $"Transaction {id} does not exist.")
            : TypedResults.Ok(TransactionResponse.From(transaction));
    }

    private static async Task<IResult> ReverseAsync(
        long id,
        ReverseTransactionRequest? request,
        [FromHeader(Name = IdempotentPosting.KeyHeader)] string? idempotencyKey,
        HttpContext http,
        SqlLedger ledger,
        BusinessCalendar calendar,
        CancellationToken cancellationToken)
    {
        if (!IdempotentPosting.TryReadKey(idempotencyKey, out var key, out var keyProblem))
        {
            return keyProblem;
        }

        // The body is optional. No body and {} are the same request, so they get the same fingerprint.
        var body = request ?? new ReverseTransactionRequest();
        var requestInfo = IdempotentPosting.Describe(http, $"POST /transactions/{id}/reverse", key, body);

        // The procedure refuses a missing transaction (404), a second reversal (409) and a reversal of a reversal (422).
        return await IdempotentPosting.PostOnceAsync(http, ledger, requestInfo,
            () => ledger.ReverseAsync(id, body.EffectiveDate ?? calendar.Today, requestInfo, cancellationToken), cancellationToken);
    }
}