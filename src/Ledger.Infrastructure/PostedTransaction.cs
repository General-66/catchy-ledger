namespace Ledger.Infrastructure;

/// <summary>A transaction as it was stored: its database identity, who posted it, and its lines.</summary>
public sealed record PostedTransaction(
    long Id,
    string CreatedBy,
    string Description,
    DateOnly EffectiveDate,
    DateTime CreatedAtUtc,
    long? ReversesTransactionId,
    IReadOnlyList<PostedLine> Lines);

/// <summary>One stored posting: a signed amount in minor units on one account.</summary>
public sealed record PostedLine(long PostingId, long AccountId, long AmountMinor, string Currency);