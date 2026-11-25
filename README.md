# Catchy Ledger

A double-entry ledger engine: a C# API that records money movements as balanced transactions, where every transaction's lines sum to exactly zero.

> **Status:** Phase 4 (concurrency). Every account has a stored balance, a wallet can't be spent below zero, and a hundred bets at once against one wallet let exactly one through. Postings lock the balances they move in ascending account Id order, and deadlocks are retried. Audit and reporting arrive in Phase 5.

## Why it exists

Real financial systems never "edit a balance". They record movements between accounts, keep every record forever, and prove the books balance. This project builds that from scratch to learn immutability, audit trails, idempotency and transaction isolation.

## How this system prevents double-spending

A wallet holds R100 and two R80 bets arrive at the same moment. A naive "read the balance, check it, then post" lets both through: both read R100 before either writes, and the wallet ends R60 overdrawn. It's a check-then-act race, and READ COMMITTED allows it.

Here, every posting and reversal goes through one stored procedure, `usp_WriteTransaction` ([db/0009](db/0009_lock_balances_when_posting.sql)), which:

1. **Locks** the stored balance of every account it moves, `WITH (UPDLOCK, HOLDLOCK)`, one at a time in ascending account Id order. A second posting to the same wallet waits until the first commits, and because everyone locks in the same order, two postings can't deadlock each other.
2. **Then checks** the insufficient-funds rule against the balance it now holds.
3. **Then writes** the transaction, its postings and the new balances, in one database transaction.

Reads never wait: with read-committed snapshot, `GET /accounts/{id}` sees the last committed balance.

The race lab ([tools/Ledger.RaceLab](tools/Ledger.RaceLab)) fired a hundred R80 bets at once against a R100 wallet with each approach:

- **Where it ran:** Laptop: SQL Server 2022 in Docker

| Approach | Accepted | Refused | Gave up | Other errors | Deadlocks | Conflicts | Time | Wallet ends at | Balances agree |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| READ COMMITTED | 27 | 73 | 0 | 0 | 0 | 0 | 0.10 s | ZAR -2,060.00, overdrawn | Yes |
| SERIALIZABLE | 1 | 99 | 0 | 0 | 249 | 0 | 13.01 s | ZAR 20.00 | Yes |
| UPDLOCK, HOLDLOCK | 1 | 99 | 0 | 0 | 0 | 0 | 0.03 s | ZAR 20.00 | Yes |
| rowversion + retry | 1 | 99 | 0 | 0 | 0 | 90 | 0.07 s | ZAR 20.00 | Yes |
| dbo.usp_PostTransaction | 1 | 99 | 0 | 0 | 0 | 0 | 0.18 s | ZAR 20.00 | Yes |

Every result, with where it ran, is in [docs/race-lab](docs/race-lab), and [ADR 0006](docs/adr/0006-lock-balances-in-ascending-account-order.md) explains the choice. The same race runs five times on every CI build, in `RaceTests`.

## Run it locally

You need the .NET 10 SDK, Docker Desktop and Git.

```powershell
git clone https://github.com/<your-username>/catchy-ledger.git
cd catchy-ledger
Copy-Item .env.example .env      # then edit the password in .env
docker compose up -d --wait      # starts SQL Server on port 14330 and waits until it's healthy
dotnet test                      # builds the solution and runs every test (Docker must be running)
```

To run the API itself:

1. Give `Ledger.Migrator` two connection strings in its User Secrets: `ConnectionStrings:LedgerAdmin` (the `sa` login from `.env`) and `ConnectionStrings:Ledger` (a new login for the API, such as `ledger_api`, with a password you choose). Run it once: it builds the `CatchyLedger` database and creates the API's login.
2. Give `Ledger.Api` the same `ConnectionStrings:Ledger` and at least one API key in its User Secrets, under `ApiKeys:<caller name>:Key` and `ApiKeys:<caller name>:Role` (`Read` or `Write`).
3. Run `Ledger.Api`. The docs open at `/scalar`, and `src/Ledger.Api/Ledger.Api.http` walks through the betting example.

## Project layout

| Path | What lives there |
| --- | --- |
| `src/Ledger.Domain` | Money, Account, Transaction. No dependencies on anything else. |
| `src/Ledger.Infrastructure` | Database access |
| `src/Ledger.Api` | HTTP endpoints, API-key authentication, idempotency, Problem Details |
| `tools/Ledger.RaceLab` | The race lab: the same race against four ways of handling concurrency, with the results written to `docs/race-lab` |
| `src/Ledger.Migrator` | Builds or upgrades the database and creates the API's login |
| `tests/Ledger.Tests.Unit` | Fast tests with no database |
| `tests/Ledger.Tests.Integration` | Tests that start the API or a real database |
| `db/` | SQL schema scripts and stored procedures |
| `docs/adr/` | Architecture decision records |

## Licence

MIT. See [LICENSE](LICENSE).