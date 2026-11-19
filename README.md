# Catchy Ledger

A double-entry ledger engine: a C# API that records money movements as balanced transactions, where every transaction's lines sum to exactly zero.

> **Status:** Phase 0 (setup). The domain model, database and API arrive in later phases.

## Why it exists

Real financial systems never "edit a balance". They record movements between accounts, keep every record forever, and prove the books balance. This project builds that from scratch to learn immutability, audit trails, idempotency and transaction isolation.

## Run it locally

You need the .NET 10 SDK, Docker Desktop and Git.

```powershell
git clone https://github.com/<your-username>/catchy-ledger.git
cd catchy-ledger
Copy-Item .env.example .env      # then edit the password in .env
docker compose up -d --wait      # starts SQL Server and waits until it's healthy
dotnet test                      # builds the solution and runs every test
```

## Project layout

| Path | What lives there |
| --- | --- |
| `src/Ledger.Domain` | Money, Account, Transaction. No dependencies on anything else. |
| `src/Ledger.Infrastructure` | Database access |
| `src/Ledger.Api` | HTTP endpoints |
| `tests/Ledger.Tests.Unit` | Fast tests with no database |
| `tests/Ledger.Tests.Integration` | Tests that start the API or a real database |
| `db/` | SQL schema scripts and stored procedures |
| `docs/adr/` | Architecture decision records |

## Licence

MIT. See [LICENSE](LICENSE).