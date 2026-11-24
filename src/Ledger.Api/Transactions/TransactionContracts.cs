using Ledger.Api.Errors;
using Ledger.Domain;
using Ledger.Infrastructure;

namespace Ledger.Api.Transactions;

/// <summary>Which column a line goes in. The amount itself is always positive; the side gives it its sign.</summary>
public enum PostingSide
{
    Debit,
    Credit,
}

/// <summary>One line of a transaction: debit or credit an account by a positive number of minor units (cents).</summary>
public sealed record PostingLineRequest(long AccountId, PostingSide Side, long AmountMinor);

/// <summary>
/// The body of POST /transactions. EffectiveDate is the day it counts for in the books; leave it out for today
/// in South Africa.
/// </summary>
public sealed record PostTransactionRequest(
    string Description,
    string Currency,
    IReadOnlyList<PostingLineRequest> Lines,
    DateOnly? EffectiveDate = null);

/// <summary>The optional body of POST /transactions/{id}/reverse. Leave EffectiveDate out for today in South Africa.</summary>
public sealed record ReverseTransactionRequest(DateOnly? EffectiveDate = null);

/// <summary>A transaction as stored, with its lines in the order they were posted.</summary>
public sealed record TransactionResponse(
    long Id,
    string CreatedBy,
    string Description,
    DateOnly EffectiveDate,
    DateTimeOffset CreatedAt,
    long? ReversesTransactionId,
    string Currency,
    IReadOnlyList<PostingLineResponse> Lines)
{
    public static TransactionResponse From(PostedTransaction transaction) => new(
        transaction.Id,
        transaction.CreatedBy,
        transaction.Description,
        transaction.EffectiveDate,
        // SQL Server stores CreatedAt in UTC but hands it back without a time zone. Say so in the JSON: +00:00.
        new DateTimeOffset(DateTime.SpecifyKind(transaction.CreatedAtUtc, DateTimeKind.Utc)),
        transaction.ReversesTransactionId,
        transaction.Lines[0].Currency, // every line is in the same currency
        [.. transaction.Lines.Select(PostingLineResponse.From)]);
}

/// <summary>One stored line, read the same way it was sent: a side and a positive amount.</summary>
public sealed record PostingLineResponse(long PostingId, long AccountId, PostingSide Side, long AmountMinor)
{
    public static PostingLineResponse From(PostedLine line) => new(
        line.PostingId,
        line.AccountId,
        line.AmountMinor > 0 ? PostingSide.Debit : PostingSide.Credit,
        Math.Abs(line.AmountMinor));
}

public static class TransactionValidation
{
    /// <summary>A sanity limit. A betting transaction has two or three lines; nothing real needs a hundred.</summary>
    public const int MaxLines = 100;

    /// <summary>
    /// Checks the shape of the request: 400 problems. The accounting rules (at least two lines, one currency,
    /// summing to zero) are the domain's job, and break with a 422.
    /// </summary>
    public static ValidationErrors Validate(this PostTransactionRequest request)
    {
        var errors = new ValidationErrors();
        errors.RequireText("description", request.Description, SqlLedger.MaxDescriptionLength);
        errors.RequireCurrency("currency", request.Currency);

        if (request.Lines.Count > MaxLines)
        {
            errors.Add("lines", $"At most {MaxLines} lines.");
        }

        for (var i = 0; i < request.Lines.Count; i++)
        {
            var line = request.Lines[i];
            if (line is null)
            {
                errors.Add($"lines[{i}]", "A line can't be null.");
                continue;
            }

            if (line.AccountId <= 0)
            {
                errors.Add($"lines[{i}].accountId", "An account Id is a positive number.");
            }

            if (!Enum.IsDefined(line.Side))
            {
                errors.Add($"lines[{i}].side", "Use Debit or Credit.");
            }

            if (line.AmountMinor <= 0)
            {
                errors.Add($"lines[{i}].amountMinor", "A positive number of minor units, such as 50000 for R500.00. The side gives the sign.");
            }
        }

        return errors;
    }

    /// <summary>The request's lines as domain postings. Every account Id must be in <paramref name="accounts"/>.</summary>
    public static IEnumerable<Posting> ToPostings(this PostTransactionRequest request, IReadOnlyDictionary<long, Account> accounts)
    {
        var currency = Currency.FromCode(request.Currency);
        return request.Lines.Select(line => line.Side == PostingSide.Debit
            ? Posting.Debit(accounts[line.AccountId], new Money(line.AmountMinor, currency))
            : Posting.Credit(accounts[line.AccountId], new Money(line.AmountMinor, currency)));
    }
}