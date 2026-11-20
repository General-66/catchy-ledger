-- 0001: database-wide settings. Runs once, before any table exists.

-- Readers see the last committed version of a row instead of waiting for a writer to finish, so balance and report queries never block postings. 
-- Azure SQL Database has this on by default;
-- SQL Server has it off. Setting it here makes your laptop, CI and Azure behave the same.
IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND is_read_committed_snapshot_on = 0)
    ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE;

-- Lets a session ask for SNAPSHOT isolation. Ledger verification (sp_verify_database_ledger) needs it, and Phase 4 compares isolation levels. 
-- Also on by default in Azure SQL Database.
IF EXISTS (SELECT 1 FROM sys.databases WHERE database_id = DB_ID() AND snapshot_isolation_state = 0)
    ALTER DATABASE CURRENT SET ALLOW_SNAPSHOT_ISOLATION ON;