using System.Data;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Cerberus.Data;

/// <summary>
/// Local-mode schema upkeep without migrations. <c>EnsureCreated</c> only builds
/// the schema for a brand-new database file, so an existing <c>cerberus.db</c>
/// would miss tables and columns added later. This brings such a database
/// forward additively: it creates missing tables/indexes and adds missing
/// columns. It never drops, renames or retypes anything. Production (M7) is
/// expected to use real migrations instead.
/// </summary>
public static partial class SchemaUpgrader
{
    public static void EnsureUpToDate(CerberusDbContext db)
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

    /// <summary>
    /// Makes the audit log append-only at the database level: any UPDATE or
    /// DELETE on it fails, whichever code path (or tool) attempts it.
    /// </summary>
    private static void ProtectAuditLog(CerberusDbContext db)
    {
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

    private static HashSet<string> ExistingObjects(CerberusDbContext db)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type IN ('table', 'index')";
            using var reader = command.ExecuteReader();
            while (reader.Read()) names.Add(reader.GetString(0));
        }
        finally
        {
            if (wasClosed) connection.Close();
        }
        return names;
    }

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

    private static HashSet<string> ExistingColumns(CerberusDbContext db, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var connection = db.Database.GetDbConnection();
        var wasClosed = connection.State != ConnectionState.Open;
        if (wasClosed) connection.Open();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info(\"{table}\")";
            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        finally
        {
            if (wasClosed) connection.Close();
        }
        return columns;
    }

    private static string DefaultLiteral(string columnType) =>
        columnType.ToUpperInvariant() switch
        {
            "INTEGER" or "REAL" => "0",
            _ => "''",
        };
}
