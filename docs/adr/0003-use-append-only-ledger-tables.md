# 3. Make Transactions and Postings append-only ledger tables

Date: 2026-11-21

## Status

Accepted

## Context

The books must be immutable: a posting is never changed or deleted, and mistakes are fixed with reversing
transactions. Reviewers will also ask how we'd prove nobody changed the books.

The options considered:

1. **Permissions only.** DENY UPDATE and DELETE to the application's database user. This stops the app, but not
   an administrator, a second application with broader rights, or anyone who gains admin access.
2. **Triggers** that roll back any UPDATE or DELETE. An administrator can disable a trigger, change the data and
   enable it again, leaving no trace.
3. **A hash chain in application code**, where each transaction stores a hash of itself and the previous one.
   It detects tampering, but it's our own cryptography to get right and maintain. It stays a stretch goal.
4. **SQL Server append-only ledger tables** (SQL Server 2022 and Azure SQL), which refuse UPDATE and DELETE from
   everyone and hash every inserted row into a verifiable ledger.

## Decision

Create Transactions and Postings as append-only ledger tables in their first CREATE TABLE, and keep the DENY
permissions from option 1 as well. Mark only these two tables, not the whole database: a ledger database would
make every table a ledger table, including the AccountBalances table that Phase 4 updates on every posting.

Verify integrity with sys.sp_generate_database_ledger_digest and sys.sp_verify_database_ledger.

## Consequences

- UPDATE and DELETE on the books are refused for everyone, including sa (error 37359, raised before permissions are checked).
- SQL Server records which database user inserted every row, independently of the application.
- A digest stored outside the database lets us prove later that no row has changed.
- It needs SQL Server 2022 or later, or Azure SQL. Both are already the plan's databases.
- The decision can't be reversed: an existing table can't become a ledger table, and a ledger table can't stop
  being one.
- Columns added later must be nullable, and a column's data type can't change. Every planned column exists now.
- Ledger tables can't be truncated or have rows deleted, so each integration test class uses a fresh database.
- A digest only proves something if it's kept where the database can't change it, such as Azure immutable
  storage. Until then, verification shows the mechanism, not a guarantee.
- A ledger table doesn't stop a wrong row being inserted by someone allowed to insert. That's why the app can only
  insert through usp_PostTransaction, which checks every rule, and why the trial balance must stay at zero.