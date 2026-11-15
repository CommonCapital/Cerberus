using System.Data;
using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cerberus.Data;

/// <summary>
/// Schema upkeep without migrations, for SQLite and PostgreSQL alike. <c>EnsureCreated</c> only builds
/// the schema for a brand-new database, so an existing one would miss tables and columns added later.
/// This brings such a database forward additively: it creates missing tables/indexes and adds missing
/// columns. It never drops, renames or retypes anything. Production (M7) is expected to use real
/// migrations instead.
/// </summary>
public static partial class SchemaUpgrader
{
    public static void EnsureUpToDate(CerberusDbContext db)
    {
        // Two copies started at the same moment on a new database both find it empty and both create it;
        // the second then fails on a table the first has just made. It tries again and finds the schema
        // there, so it only adds what is missing. (PostgreSQL also takes turns under a lock.)
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (db.IsPostgres) UnderPostgresLock(db, () => Upgrade(db));
                else Upgrade(db);
                return;
            }
            catch (DbException) when (attempt < 5)
            {
                Thread.Sleep(200 * attempt);
            }
        }
    }

    private static void Upgrade(CerberusDbContext db)
    {
        // Fresh database: EnsureCreated builds everything; otherwise upgrade additively.
        if (!db.Database.EnsureCreated())
        {
            // Order matters: an index may cover a column that is only added in step two.
            CreateMissing(db, "TABLE");
            AddMissingColumns(db);
            CreateMissing(db, "INDEX");
        }
        ProtectAuditLog(db);
    }

    private static void UnderPostgresLock(CerberusDbContext db, Action work)
    {
        // Taken while the schema is made or upgraded, so that copies of the service starting together take turns.
        // Its number is "cerberus" read as eight bytes. A session lock holds on one connection: the context
        // keeps this one open until it is released.
        db.Database.OpenConnection();
        try
        {
            db.Database.ExecuteSqlRaw("SELECT pg_advisory_lock(7162256549337003379)");
            try
            {
                work();
            }
            finally
            {
                db.Database.ExecuteSqlRaw("SELECT pg_advisory_unlock(7162256549337003379)");
            }
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    /// <summary>
    /// Makes the audit log append-only at the database level: any UPDATE or
    /// DELETE on it fails, whichever code path (or tool) attempts it.
    /// </summary>
    private static void ProtectAuditLog(CerberusDbContext db)
    {
        if (db.IsPostgres)
        {
            // TRUNCATE too: in PostgreSQL it empties a table without running row triggers.
            db.Database.ExecuteSqlRaw("""
                CREATE OR REPLACE FUNCTION cerberus_audit_append_only() RETURNS trigger LANGUAGE plpgsql AS
                $fn$ BEGIN RAISE EXCEPTION 'audit log is append-only'; END $fn$
                """);
            db.Database.ExecuteSqlRaw("""
                DO $do$ BEGIN
                  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditevents_no_change' AND tgrelid = '"AuditEvents"'::regclass) THEN
                    CREATE TRIGGER auditevents_no_change BEFORE UPDATE OR DELETE ON "AuditEvents"
                      FOR EACH ROW EXECUTE FUNCTION cerberus_audit_append_only();
                  END IF;
                  IF NOT EXISTS (SELECT 1 FROM pg_trigger WHERE tgname = 'auditevents_no_truncate' AND tgrelid = '"AuditEvents"'::regclass) THEN
                    CREATE TRIGGER auditevents_no_truncate BEFORE TRUNCATE ON "AuditEvents"
                      FOR EACH STATEMENT EXECUTE FUNCTION cerberus_audit_append_only();
                  END IF;
                END $do$
                """);
            return;
        }
        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS AuditEvents_NoUpdate BEFORE UPDATE ON "AuditEvents"
            BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END
            """);
        db.Database.ExecuteSqlRaw("""
            CREATE TRIGGER IF NOT EXISTS AuditEvents_NoDelete BEFORE DELETE ON "AuditEvents"
            BEGIN SELECT RAISE(ABORT, 'audit log is append-only'); END
            """);
    }

    /// <summary>
    /// Runs the model's CREATE statements of one kind ("TABLE" or "INDEX") for
    /// objects the database does not have yet.
    /// </summary>
    private static void CreateMissing(CerberusDbContext db, string kind)
    {
        var existing = ExistingObjects(db);

        // The create script holds one CREATE TABLE / CREATE INDEX per statement.
        var statements = db.Database.GenerateCreateScript()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var statement in statements)
        {
            var match = CreateStatement().Match(statement);
            if (!match.Success || !match.Groups["kind"].Value.Equals(kind, StringComparison.OrdinalIgnoreCase)) continue;
            if (existing.Contains(match.Groups["name"].Value)) continue;

            db.Database.ExecuteSqlRaw(statement);
        }
    }

    [GeneratedRegex(@"^CREATE\s+(?:UNIQUE\s+)?(?<kind>TABLE|INDEX)\s+""(?<name>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex CreateStatement();

    private static HashSet<string> ExistingObjects(CerberusDbContext db) =>
        Names(db, db.IsPostgres
            ? "SELECT tablename FROM pg_tables WHERE schemaname = current_schema() UNION ALL SELECT indexname FROM pg_indexes WHERE schemaname = current_schema()"
            : "SELECT name FROM sqlite_master WHERE type IN ('table', 'index')", column: 0);

    private static void AddMissingColumns(CerberusDbContext db)
    {
        foreach (var entity in db.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null) continue;

            var existing = ExistingColumns(db, table);
            var storeObject = StoreObjectIdentifier.Table(table, entity.GetSchema());

            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(storeObject);
                if (column is null || existing.Contains(column)) continue;

                var type = property.GetColumnType();
                var definition = property.IsNullable
                    ? $"{type} NULL"
                    : $"{type} NOT NULL DEFAULT {DefaultLiteral(type)}";

                // Identifiers come from the EF model, never from user input.
#pragma warning disable EF1002
                db.Database.ExecuteSqlRaw($"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}");
#pragma warning restore EF1002
            }
        }
    }

    // Table names come from the EF model, never from user input.
    private static HashSet<string> ExistingColumns(CerberusDbContext db, string table) =>
        db.IsPostgres
            ? Names(db, $"SELECT column_name FROM information_schema.columns WHERE table_schema = current_schema() AND table_name = '{table.Replace("'", "''")}'", column: 0)
            : Names(db, $"PRAGMA table_info(\"{table}\")", column: 1);

    private static HashSet<string> Names(CerberusDbContext db, string query, int column)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(column));
        }
        finally
        {
            if (wasClosed) connection.Close();
        }
        return names;
    }

    /// <summary>What a NOT NULL column added to rows that already exist starts as, by its type in either database.</summary>
    private static string DefaultLiteral(string columnType)
    {
        var type = columnType.ToLowerInvariant();
        return type switch
        {
            "integer" or "real" or "bigint" or "smallint" or "double precision" or "numeric" => "0",
            "boolean" => "false",
            "uuid" => "'00000000-0000-0000-0000-000000000000'",
            "bytea" => "'\\x'",
            _ when type.StartsWith("numeric", StringComparison.Ordinal) => "0",
            _ when type.StartsWith("timestamp", StringComparison.Ordinal) => "'0001-01-01 00:00:00+00'",
            _ => "''",
        };
    }
}
