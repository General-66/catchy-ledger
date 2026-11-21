# Database scripts

The SQL Server schema, one numbered script per change. `LedgerMigrator` in Ledger.Infrastructure runs them
with DbUp, in order, each one once, and records every script it has run in `dbo.SchemaVersions`.
Every `.sql` file here is compiled into `Ledger.Infrastructure.dll`, so a new script is picked up on the next build.

| Script | What it does |
| --- | --- |
| `0001_database_settings.sql` | Turns on read-committed snapshot and snapshot isolation |
| `0002_create_accounts.sql` | The chart of accounts |
| `0003_create_transactions_and_postings.sql` | Transactions and Postings, as append-only ledger tables |
| `0004_create_usp_post_transaction.sql` | The only way to post: re-checks every rule, then inserts all lines or none |
| `0005_create_usp_reverse_transaction.sql` | Posts the mirror image of a transaction, once |
| `0006_create_app_role.sql` | The `ledger_app` role: read the books, write only through the procedures |

Rules:

- A script is never edited after it's merged into `main`. A change is a new script with the next number.
- Transactions and Postings are ledger tables: a column added to them must be nullable, and data types can't change.
- The procedures report broken rules with error numbers 50001 to 50020, listed in `LedgerErrors.cs`.