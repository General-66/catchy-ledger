namespace Ledger.Domain;

/// <summary>The side that makes an account grow. It follows from the account's type.</summary>
public enum NormalSide
{
    /// <summary>Grows with debits, so its balance is normally stored as a positive number. Assets and expenses.</summary>
    Debit,

    /// <summary>Grows with credits, so its balance is normally stored as a negative number. Liabilities, equity, income.</summary>
    Credit,
}