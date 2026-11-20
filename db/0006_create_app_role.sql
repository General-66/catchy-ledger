-- 0006: what the application is allowed to do, as a database role.
-- The API's own database user joins this role in Phase 3. A role, not a user, lives in the scripts,
-- because the user belongs to a login with a password, and passwords never go in source control.

CREATE ROLE ledger_app;

-- Read everything: balances, statements and reports are all built from these tables.
GRANT SELECT ON dbo.Accounts TO ledger_app;
GRANT SELECT ON dbo.Transactions TO ledger_app;
GRANT SELECT ON dbo.Postings TO ledger_app;

-- Open new accounts directly. Accounts hold no money, so they don't need a procedure.
GRANT INSERT ON dbo.Accounts TO ledger_app;

-- Move money only through the two procedures. They can insert rows the role itself can't touch,
-- because SQL Server skips the table permission checks when a procedure and its tables have the
-- same owner (dbo). This is called ownership chaining.
GRANT EXECUTE ON dbo.usp_PostTransaction TO ledger_app;
GRANT EXECUTE ON dbo.usp_ReverseTransaction TO ledger_app;
GRANT EXECUTE ON TYPE::dbo.PostingLineList TO ledger_app; -- needed to pass the lines in at all

-- And never directly. DENY beats GRANT: even if someone later adds the app to a role such as
-- db_datawriter, these three stay refused.
DENY INSERT, UPDATE, DELETE ON dbo.Transactions TO ledger_app;
DENY INSERT, UPDATE, DELETE ON dbo.Postings TO ledger_app;