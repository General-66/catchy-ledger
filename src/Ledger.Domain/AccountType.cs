namespace Ledger.Domain;

/// <summary>The five kinds of account every chart of accounts is built from.</summary>
public enum AccountType
{
    /// <summary>What the business owns or is owed, such as money in the bank.</summary>
    Asset,

    /// <summary>What the business owes to someone else, such as a customer's wallet balance.</summary>
    Liability,

    /// <summary>What the owners have put in or earned and kept.</summary>
    Equity,

    /// <summary>What the business earns, such as betting revenue from lost bets.</summary>
    Income,

    /// <summary>What the business spends, such as payouts on winning bets.</summary>
    Expense,
}