using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Cerberus.Authorization;

/// <summary>The content of one audit event (everything that is hashed).</summary>
public sealed record AuditRecord(
    long Sequence,
    string UserId,
    string? EngagementId,
    string Action,
    string? Target,
    string? Tier,
    string Outcome,
    string Detail,
    DateTime CreatedAt);

/// <summary>An audit record as stored: its content plus the chain links.</summary>
public sealed record ChainedAuditRecord(AuditRecord Record, string PreviousHash, string Hash);

public sealed record ChainVerification(bool Intact, int Checked, long? FirstBrokenSequence, string? Problem);

/// <summary>
/// Tamper evidence for the audit log. Each event's hash covers its own content
/// and the previous event's hash, so editing, removing or reordering any event
/// breaks every hash after it. This detects tampering; it cannot prevent
/// someone with database access from rewriting the whole chain.
/// </summary>
public static class AuditChain
{
    /// <summary>The "previous hash" of the first event in a chain.</summary>
    public static readonly string Genesis = new('0', 64);

    public static string ComputeHash(AuditRecord record, string previousHash)
    {
        // Length-prefix every field so no two different records share an encoding.
        var builder = new StringBuilder();
        void Field(string? value)
        {
            value ??= "\u0000";
            builder.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('|');
        }

        Field(previousHash);
        Field(record.Sequence.ToString(CultureInfo.InvariantCulture));
        Field(record.UserId);
        Field(record.EngagementId);
        Field(record.Action);
        Field(record.Target);
        Field(record.Tier);
        Field(record.Outcome);
        Field(record.Detail);
        // Ticks, not a formatted date: storage does not round-trip DateTimeKind.
        Field(record.CreatedAt.Ticks.ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    /// <summary>Verifies a user's whole chain, given in sequence order starting at sequence 1.</summary>
    public static ChainVerification Verify(IEnumerable<ChainedAuditRecord> chain)
    {
        var previous = Genesis;
        long expectedSequence = 1;
        var count = 0;

        foreach (var entry in chain)
        {
            var sequence = entry.Record.Sequence;
            if (sequence != expectedSequence)
            {
                return new ChainVerification(false, count, expectedSequence,
                    $"Expected event {expectedSequence} but found {sequence}: an event is missing or out of order.");
            }
            if (entry.PreviousHash != previous)
            {
                return new ChainVerification(false, count, sequence,
                    $"Event {sequence} does not link to the event before it.");
            }
            if (entry.Hash != ComputeHash(entry.Record, previous))
            {
                return new ChainVerification(false, count, sequence,
                    $"Event {sequence} has been altered: its content does not match its hash.");
            }

            previous = entry.Hash;
            expectedSequence++;
            count++;
        }

        return new ChainVerification(true, count, null, null);
    }
}
