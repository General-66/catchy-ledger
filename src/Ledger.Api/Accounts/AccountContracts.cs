using Ledger.Api.Errors;
using Ledger.Domain;

namespace Ledger.Api.Accounts;

/// <summary>The body of POST /accounts. Every field is required: a wallet must say out loud that it can't be overdrawn.</summary>
public sealed record CreateAccountRequest(string Code, string Name, AccountType Type, string Currency, bool AllowOverdraft);

/// <summary>An account, with its balance as stored and as a person would read it.</summary>
public sealed record AccountResponse(
    long Id,
    string Code,
    string Name,
    AccountType Type,
    NormalSide NormalSide,
    string Currency,
    bool AllowOverdraft,
    BalanceResponse Balance)
{
    public static AccountResponse From(Account account, Money storedBalance)
    {
        var onNormalSide = account.BalanceOnNormalSide(storedBalance);
        return new AccountResponse(
            account.Id,
            account.Code,
            account.Name,
            account.Type,
            account.NormalSide,
            account.Currency.Code,
            account.AllowOverdraft,
            new BalanceResponse(storedBalance.AmountMinor, onNormalSide.AmountMinor, onNormalSide.ToString()));
    }
}

/// <summary>
/// A balance three ways. StoredMinor is the sum of the postings, signs and all: a funded wallet is -50000.
/// OnNormalSideMinor and Formatted read it on the account's normal side: 50000 and "ZAR 500.00".
/// </summary>
public sealed record BalanceResponse(long StoredMinor, long OnNormalSideMinor, string Formatted);

public static class AccountValidation
{
    /// <summary>The longest code the Accounts table stores (varchar(20)).</summary>
    public const int MaxCodeLength = 20;

    /// <summary>The longest name the Accounts table stores (nvarchar(100)).</summary>
    public const int MaxNameLength = 100;

    public static ValidationErrors Validate(this CreateAccountRequest request)
    {
        var errors = new ValidationErrors();

        // varchar holds plain ASCII only: SQL Server would quietly turn other characters into '?'. So refuse them here.
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > MaxCodeLength
            || !request.Code.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            errors.Add("code", $"Use 1 to {MaxCodeLength} letters, digits or hyphens, such as 2000-0001.");
        }

        errors.RequireText("name", request.Name, MaxNameLength);
        if (!Enum.IsDefined(request.Type))
        {
            errors.Add("type", "Use Asset, Liability, Equity, Income or Expense.");
        }

        errors.RequireCurrency("currency", request.Currency);
        return errors;
    }
}