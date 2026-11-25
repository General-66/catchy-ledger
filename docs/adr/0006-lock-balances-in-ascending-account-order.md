# 6. Lock balances with UPDLOCK, HOLDLOCK, in ascending account Id order

Date: 2026-11-24

## Status

Accepted

## Context

A wallet holds R100 and two R80 bets arrive at once. Under READ COMMITTED, with read-committed snapshot as this
database runs, a posting that reads the balance, checks it and then writes lets both through: both read R100 before
either commits. The insufficient-funds rule needs the balance it checked to stay true until the posting commits.

The plan named three fixes. The race lab (tools/Ledger.RaceLab) runs each, plus the unprotected version and the
shipped procedure, in two scenarios: a hundred R80 bets at once against one R100 wallet, and a busy evening of 500
R10 bets from many wallets, every one of them also moving the one Open Bets account.

The run after the fix, 25 November 2026, 14:32:

- **Where it ran:** Laptop: SQL Server on this computer, at 127.0.0.1,14330
- **SQL Server:** 16.0.4265.3 CU26 (Developer Edition (64-bit))
- **Optimized locking:** Not available (SQL Server 2022 doesn't have it)

| Approach | Accepted | Refused | Gave up | Other errors | Deadlocks | Conflicts | Time | Wallet ends at | Balances agree |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| READ COMMITTED | 27 | 73 | 0 | 0 | 0 | 0 | 0.10 s | ZAR -2,060.00, overdrawn | Yes |
| SERIALIZABLE | 1 | 99 | 0 | 0 | 249 | 0 | 13.01 s | ZAR 20.00 | Yes |
| UPDLOCK, HOLDLOCK | 1 | 99 | 0 | 0 | 0 | 0 | 0.03 s | ZAR 20.00 | Yes |
| rowversion + retry | 1 | 99 | 0 | 0 | 0 | 90 | 0.07 s | ZAR 20.00 | Yes |
| dbo.usp_PostTransaction | 1 | 99 | 0 | 0 | 0 | 0 | 0.18 s | ZAR 20.00 | Yes |

| Approach | Bets per second | Time | Accepted | Gave up | Other errors | Deadlocks | Conflicts | Overdrawn wallets | Balances agree |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| READ COMMITTED | 632 | 0.79 s | 500 | 0 | 0 | 0 | 0 | 0 | Yes |
| SERIALIZABLE | 101 | 4.94 s | 500 | 0 | 0 | 40 | 0 | 0 | Yes |
| UPDLOCK, HOLDLOCK | 698 | 0.72 s | 500 | 0 | 0 | 0 | 0 | 0 | Yes |
| rowversion + retry | 547 | 0.91 s | 500 | 0 | 0 | 0 | 183 | 0 | Yes |
| dbo.usp_PostTransaction | 478 | 1.05 s | 500 | 0 | 0 | 0 | 0 | 0 | Yes |

The run before the fix is in docs/race-lab too. In it, READ COMMITTED accepted 27 of the 100 bets and overdrew
the wallet, and the Phase 3 procedure, which had no funds rule, accepted all 100.

## Options

1. **SERIALIZABLE.** Correct: exactly one bet accepted. But bets that read the same balance under shared locks
   can't all turn them into the exclusive lock they need to write, so they deadlock. In the race SQL Server rolled
   back 99 victims and the run took 13.01s, the slowest of the correct approaches. Every victim's work is done again.
2. **UPDLOCK, HOLDLOCK on the balance row (pessimistic).** Correct, with 0 deadlocks and 0 conflicts: bets on the
   same wallet queue, and each reads the balance the one before it left. No work is thrown away.
3. **rowversion + retry (optimistic).** Correct, and nobody waits, but under contention most attempts are wasted:
   0 conflicts in the race. It suits rows that are rarely fought over. A wallet just before kick-off is the
   opposite.
4. **A conditional UPDATE**, adding the change only WHERE the result stays within the rule, then checking that a row
   changed. Atomic, and hint-free for one account. But a transaction moves two or more accounts, the rule depends on
   each account's type and AllowOverdraft flag, and the order the rows are updated in would be up to the query plan.

## Decision

Option 2, for every account a transaction moves, in ascending account Id order, in one procedure: usp_WriteTransaction
(db/0009). It locks each balance row WITH (UPDLOCK, HOLDLOCK), one at a time, lowest Id first. Only then does it check
the insufficient-funds rule and, for a reversal, whether the original was reversed while it waited. Then it writes the
transaction, its postings and the new balances, and commits. Both public procedures call it, and the app can't call
it directly.

SqlLedger also retries a deadlock victim (error 1205) up to 5 attempts in all, waiting about 20 ms, then twice as long
each time up to 1 s, half of it random (SqlRetryPolicy). With every posting locking in the same order, postings
shouldn't deadlock each other; the retry covers a deadlock with anything else. The Idempotency-Key makes a retried
posting safe.

## Consequences

- Of a hundred racing bets, exactly one gets through, every time. RaceTests checks it, five rounds on every CI run,
  and checks that bets and wins listing the same accounts in opposite orders never deadlock.
- Bets on the same wallet wait for each other. A wallet belongs to one customer, so the queue is short.
- Open Bets is a hot account. It's account 2, lower than every wallet, so every bet and every settlement locks it
  first and holds it until COMMIT. In the busy evening the shipped procedure managed 478 bets per second against
  698 for the lab's UPDLOCK, HOLDLOCK procedure, which locks Open Bets only at the end. If that ever limits throughput,
  two known fixes: lock the accounts the funds rule checks first and the house accounts last, each group in Id order;
  or split Open Bets into several sub-accounts that the reports add up.
- Reads never wait. Under read-committed snapshot, GET /accounts/{id} and reports read the last committed balance.
- Azure SQL Database always uses optimized locking, which SQL Server 2022 doesn't have, so lock waits and deadlock
  counts there will differ. When Phase 6 deploys to Azure SQL, run the lab there and add its results next to these.