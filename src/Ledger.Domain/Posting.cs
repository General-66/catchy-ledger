namespace Ledger.Domain;

/// <summary>
/// One line of a transaction: an amount on one account.
/// The amount is signed: positive is a debit, negative is a credit.
/// </summary>
public sealed record Posting
{
    public Posting(Account account, Money amount)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(amount);
        Account = account;
        Amount = amount;
    }

    public Account Account { get; }

    public Money Amount { get; }

    public bool IsDebit => Amount.IsPositive;

    public bool IsCredit => Amount.IsNegative;

    /// <summary>Debits a positive amount to the account. It's stored with a + sign.</summary>
    public static Posting Debit(Account account, Money amount) => new(account, RequirePositive(amount));

    /// <summary>Credits a positive amount to the account. It's stored with a - sign.</summary>
    public static Posting Credit(Account account, Money amount) => new(account, -RequirePositive(amount));

    public override string ToString() => $"{(IsDebit ? "Dr" : IsCredit ? "Cr" : "--")} {Account.Code} {Amount}";

    private static Money RequirePositive(Money amount)
    {
        ArgumentNullException.ThrowIfNull(amount);
        if (!amount.IsPositive)
        {
            throw new ArgumentOutOfRangeException(
                nameof(amount), amount, "Pass a positive amount. Debit or Credit decides the sign.");
        }

        return amount;
    }
}