using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Cerberus.Data;

/// <summary>
/// What PostgreSQL needs that SQLite does not, so that the same entities and the same code work on both.
/// </summary>
public static class PostgresStorage
{
    public const string Provider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>Stands in for a NUL character, which PostgreSQL does not accept in text (SQLite does).</summary>
    public const char NulStandIn = '�';

    /// <summary>
    /// Times are UTC throughout (<see cref="StoredTime"/>) and are stored as <c>timestamp with time zone</c>.
    /// PostgreSQL refuses to write a time whose kind is not UTC, and a time read back from SQLite or parsed
    /// from text has no kind: it is written as the UTC time it is, not refused.
    /// </summary>
    public static void Apply(ModelBuilder modelBuilder)
    {
        var asUtc = new ValueConverter<DateTime, DateTime>(value => AsUtc(value), value => value);
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            if (property.ClrType == typeof(DateTime) || property.ClrType == typeof(DateTime?)) property.SetValueConverter(asUtc);
        }
    }

    public static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    /// <summary>
    /// A NUL in text being written (a scanned binary file, a tool's raw output) becomes U+FFFD: PostgreSQL would
    /// refuse the whole row, and the run or reply with it, for one character nobody reads.
    /// </summary>
    public static void CleanText(ChangeTracker tracker)
    {
        foreach (var entry in tracker.Entries())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            foreach (var property in entry.Properties)
            {
                if (property.CurrentValue is string text && text.Contains('\0'))
                    property.CurrentValue = text.Replace('\0', NulStandIn);
            }
        }
    }
}
