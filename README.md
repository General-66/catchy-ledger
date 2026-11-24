# Catchy Ledger

A double-entry ledger engine: a C# API that records money movements as balanced transactions, where every transaction's lines sum to exactly zero.

> **Status:** Phase 3 (API and idempotency). An HTTP API with API-key authentication posts and reverses balanced transactions. Every write needs an Idempotency-Key, so a retried request never posts twice, and every error is Problem Details JSON. Concurrency and stored balances arrive in Phase 4.

## Why it exists

Real financial systems never "edit a balance". They record movements between accounts, keep every record forever, and prove the books balance. This project builds that from scratch to learn immutability, audit trails, idempotency and transaction isolation.

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
| `src/Ledger.Migrator` | Builds or upgrades the database and creates the API's login |
| `tests/Ledger.Tests.Unit` | Fast tests with no database |
| `tests/Ledger.Tests.Integration` | Tests that start the API or a real database |
| `db/` | SQL schema scripts and stored procedures |
| `docs/adr/` | Architecture decision records |

## Licence

MIT. See [LICENSE](LICENSE).