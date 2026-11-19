namespace Ledger.Domain;

/// <summary>
/// One account in the chart of accounts, such as "Bank" or one customer's wallet.
/// An entity: it has an identity, so two accounts with the same name are still two accounts.
/// </summary>
public sealed class Account
{
    public Account(long id, string code, string name, AccountType type, Currency currency, bool allowOverdraft)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(currency);
        if (!Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown account type.");
        }

        Id = id;
        Code = code;
        Name = name;
        Type = type;
        Currency = currency;
        AllowOverdraft = allowOverdraft;
    }

    /// <summary>The database identity. Phase 1 tests make these up; from Phase 2, SQL Server assigns them.</summary>
    public long Id { get; }

    /// <summary>The chart-of-accounts code people use, such as "1000" for Bank.</summary>
    public string Code { get; }

    public string Name { get; }

    public AccountType Type { get; }

    /// <summary>Every amount posted to this account must be in this currency.</summary>
    public Currency Currency { get; }

    /// <summary>
    /// Whether the balance may go below zero on its normal side. Off for customer wallets, on for the
    /// operator's own accounts. Phase 4 enforces it, once balances are stored.
    /// </summary>
    public bool AllowOverdraft { get; }

    /// <summary>The side that makes this account grow. It follows from the type and can't be set.</summary>
    public NormalSide NormalSide => Type switch
    {
        AccountType.Asset or AccountType.Expense => NormalSide.Debit,
        AccountType.Liability or AccountType.Equity or AccountType.Income => NormalSide.Credit,
        _ => throw new InvalidOperationException($"Unknown account type {Type}."),
    };

    /// <summary>
    /// Reads a stored balance the way a person would. Debit-side accounts keep their sign; credit-side
    /// accounts are multiplied by -1. A wallet stored as -50000 reads as ZAR 500.00.
    /// </summary>
    public Money BalanceOnNormalSide(Money storedBalance)
    {
        ArgumentNullException.ThrowIfNull(storedBalance);
        if (storedBalance.Currency != Currency)
        {
            throw new LedgerRuleException(
                LedgerRule.AccountCurrencyMismatch,
                $"Account {Code} holds {Currency}, not {storedBalance.Currency}.");
        }

        return NormalSide == NormalSide.Debit ? storedBalance : -storedBalance;
    }

    public override string ToString() => $"{Code} {Name}";
}