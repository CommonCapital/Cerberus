using System.Data;
using Microsoft.Data.Sqlite;
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
public static class SchemaUpgrader
{
    public static void EnsureUpToDate(CerberusDbContext db)
    {
        // Fresh database: builds everything and we are done.
        if (db.Database.EnsureCreated()) return;

        CreateMissingTablesAndIndexes(db);
        AddMissingColumns(db);
    }

    private static void CreateMissingTablesAndIndexes(CerberusDbContext db)
    {
        // The create script holds one CREATE TABLE / CREATE INDEX per statement.
        var statements = db.Database.GenerateCreateScript()
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var statement in statements)
        {
            if (!statement.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                db.Database.ExecuteSqlRaw(statement);
            }
            catch (SqliteException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                // Present already — nothing to do.
            }
            catch (SqliteException ex) when (ex.Message.Contains("no such column", StringComparison.OrdinalIgnoreCase))
            {
                // An index over a column that AddMissingColumns is about to add;
                // not needed by any index today, and safe to skip.
            }
        }
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
