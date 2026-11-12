namespace Cerberus.Data;

/// <summary>
/// Times are stored in UTC, and SQLite hands them back without saying so
/// (<see cref="DateTimeKind.Unspecified"/>). Converting such a value "to UTC"
/// would treat it as local time and shift it by the machine's offset; this
/// reads it as what it is.
/// </summary>
public static class StoredTime
{
    public static DateTime Utc(DateTime value) =>
        value.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.ToUniversalTime();
}
