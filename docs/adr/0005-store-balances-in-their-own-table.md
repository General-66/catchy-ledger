# 5. Store each account's balance in its own table

Date: 2026-11-25

## Status

Accepted

## Context

Until Phase 4, an account's balance was worked out on every read as the sum of its postings. That's always right,
but it has two problems:

- It gets slower as postings pile up. A busy wallet has thousands, and Open Bets has one for every bet ever placed.
- The insufficient-funds rule needs something to lock. With computed balances, stopping two bets from both seeing
  R100 means locking every posting on the wallet, and the gap where the next one would go: SERIALIZABLE range locks
  on the biggest, busiest table.

The options considered:

1. **Keep computing balances on read**, and enforce the rule with range locks on Postings. There's no copy to keep
   right, but every check reads all of an account's postings, and the locks cover a range of the busiest table.
2. **A BalanceMinor column on Accounts.** One row per account, nothing to join. But Accounts holds an account's
   details, which rarely change, and Phase 5 makes it a system-versioned temporal table that keeps every change to
   them. A balance there would add a history row for every posting, and mix money with metadata.
3. **A separate AccountBalances table**: AccountId, BalanceMinor and RowVersion. One row per account, changed only
   by the posting procedure, in the same database transaction as the postings.

## Decision

Option 3, in db/0008. The postings stay the record; AccountBalances is a copy kept in step with them:

- usp_WriteTransaction (db/0009) is the only code that changes a balance, always as BalanceMinor + change, in the
  same database transaction as the postings it adds.
- The app is denied INSERT, UPDATE and DELETE on AccountBalances, and INSERT on Accounts. Accounts are opened only
  through usp_CreateAccount, which adds the account's zero balance in the same database transaction.
- Script 0008 worked out every existing account's balance from its postings when it created the table.

## Consequences

- Reading a balance is a one-row seek, however many postings the account has.
- The insufficient-funds rule has exactly one row per account to lock. ADR 0006 says how it's locked.
- There's now a copy that could disagree with the truth. The tests check, after every race, that every stored
  balance equals the sum of its postings, and the same query can be run against production as a reconciliation. If
  the two ever differ, the postings win and the balance is rebuilt from them.
- AccountBalances is an ordinary table, not a ledger table, because its rows change with every posting. Tampering
  with it changes a copy, which the reconciliation would catch, not the record.
- A posting to an account that has no balance row fails with error 50021, instead of quietly creating one.
- RowVersion isn't used by the shipped procedure. The race lab's optimistic approach uses it, and it's there if a
  later phase needs a cheap "has this balance changed?" check.