using Cerberus.Authorization;

namespace Cerberus.Tests;

public class ScopePolicyTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

    private static EngagementScope Engagement(
        EngagementStatus status = EngagementStatus.Active, CapabilityTier maxTier = CapabilityTier.T0,
        DateTime? startsAt = null, DateTime? endsAt = null, params ScopedTarget[] targets) =>
        new(status, maxTier, startsAt, endsAt,
            targets.Length > 0 ? targets : [new ScopedTarget(TargetType.Path, "services/api", VerificationStatus.Attested)]);

    private static ScopeDecision Check(EngagementScope engagement, string path, CapabilityTier tier = CapabilityTier.T0) =>
        ScopePolicy.Check(engagement, tier, TargetType.Path, path, Now);

    [Theory]
    [InlineData("services/api")]
    [InlineData("services/api/")]
    [InlineData("./services/api/src")]
    [InlineData("services\\api\\src\\main.py")]
    public void Paths_at_or_below_a_target_are_in_scope(string path)
    {
        Assert.True(Check(Engagement(), path).Allowed);
    }

    [Theory]
    [InlineData("services")]              // parent of the target
    [InlineData("services/api2")]         // sibling sharing a prefix
    [InlineData("services/web")]
    [InlineData(".")]                     // the whole scan root
    [InlineData("")]
    public void Paths_outside_the_target_are_denied(string path)
    {
        var decision = Check(Engagement(), path);

        Assert.False(decision.Allowed);
        Assert.Contains("outside the engagement's scope", decision.Reason);
    }

    [Theory]
    [InlineData("services/api/../../secrets")]
    [InlineData("services/api/../api")]   // would resolve inside, still refused
    [InlineData("/etc/passwd")]
    [InlineData("C:\\Windows")]
    [InlineData("~/code")]
    public void Paths_that_are_absolute_or_climb_upwards_are_rejected(string path)
    {
        var decision = Check(Engagement(), path);

        Assert.False(decision.Allowed);
        Assert.Contains("not a valid path target", decision.Reason);
    }

    [Fact]
    public void A_target_of_the_scan_root_covers_everything_beneath_it()
    {
        var engagement = Engagement(targets: new ScopedTarget(TargetType.Path, ".", VerificationStatus.Attested));

        Assert.True(Check(engagement, ".").Allowed);
        Assert.True(Check(engagement, "anything/at/all").Allowed);
    }

    [Theory]
    [InlineData(EngagementStatus.Aborted, "aborted")]
    [InlineData(EngagementStatus.Closed, "closed")]
    public void An_ended_engagement_authorizes_nothing(EngagementStatus status, string word)
    {
        var decision = Check(Engagement(status), "services/api");

        Assert.False(decision.Allowed);
        Assert.Contains(word, decision.Reason);
    }

    [Fact]
    public void Runs_outside_the_time_window_are_denied()
    {
        Assert.False(Check(Engagement(startsAt: Now.AddMinutes(1)), "services/api").Allowed);
        Assert.False(Check(Engagement(endsAt: Now), "services/api").Allowed);
        Assert.True(Check(Engagement(startsAt: Now.AddHours(-1), endsAt: Now.AddHours(1)), "services/api").Allowed);
    }

    [Fact]
    public void A_tier_above_the_engagements_maximum_is_denied()
    {
        var decision = Check(Engagement(maxTier: CapabilityTier.T0), "services/api", CapabilityTier.T1);

        Assert.False(decision.Allowed);
        Assert.Contains("exceeds", decision.Reason);
        Assert.True(Check(Engagement(maxTier: CapabilityTier.T1), "services/api", CapabilityTier.T1).Allowed);
    }

    [Theory]
    [InlineData(CapabilityTier.T2)]
    [InlineData(CapabilityTier.T3)]
    public void Intrusive_and_destructive_tiers_are_never_authorized_yet(CapabilityTier tier)
    {
        // Even if an engagement somehow carries a higher maximum.
        var decision = Check(Engagement(maxTier: CapabilityTier.T3), "services/api", tier);

        Assert.False(decision.Allowed);
        Assert.Contains("not available", decision.Reason);
    }

    [Fact]
    public void Target_types_do_not_cross_match()
    {
        var engagement = Engagement(targets: new ScopedTarget(TargetType.Domain, "example.com", VerificationStatus.Pending));

        Assert.False(Check(engagement, "example.com").Allowed);
    }

    [Theory]
    [InlineData("example.com", true)]
    [InlineData("API.Example.com.", true)]
    [InlineData("notexample.com", false)]
    [InlineData("example.com.evil.org", false)]
    public void A_domain_target_covers_itself_and_its_subdomains_only(string host, bool expected)
    {
        var engagement = Engagement(targets: new ScopedTarget(TargetType.Domain, "example.com", VerificationStatus.Pending));

        Assert.Equal(expected, ScopePolicy.Check(engagement, CapabilityTier.T0, TargetType.Domain, host, Now).Allowed);
    }

    [Theory]
    [InlineData(TargetType.Path, "./a//b/", "a/b")]
    [InlineData(TargetType.Path, ".", "")]
    [InlineData(TargetType.Path, "a/../b", null)]
    [InlineData(TargetType.Host, " Web-01.Internal ", "web-01.internal")]
    [InlineData(TargetType.Host, "bad host!", null)]
    [InlineData(TargetType.Host, "", null)]
    public void Locators_are_normalized(TargetType type, string input, string? expected)
    {
        Assert.Equal(expected, ScopePolicy.Normalize(type, input));
    }
}

public class AuditChainTests
{
    private static List<ChainedAuditRecord> Chain(int count)
    {
        var chain = new List<ChainedAuditRecord>();
        var previous = AuditChain.Genesis;
        for (var i = 1; i <= count; i++)
        {
            var record = new AuditRecord(i, "user-1", "eng-1", "scan.run", "services/api", "T0", "ok", $"event {i}",
                new DateTime(2026, 11, 10, 12, 0, i, DateTimeKind.Utc));
            var hash = AuditChain.ComputeHash(record, previous);
            chain.Add(new ChainedAuditRecord(record, previous, hash));
            previous = hash;
        }
        return chain;
    }

    [Fact]
    public void An_untouched_chain_verifies()
    {
        Assert.Equal(new ChainVerification(true, 5, null, null), AuditChain.Verify(Chain(5)));
        Assert.True(AuditChain.Verify([]).Intact);
    }

    [Fact]
    public void Editing_an_event_is_detected_at_that_event()
    {
        var chain = Chain(5);
        chain[2] = chain[2] with { Record = chain[2].Record with { Outcome = "denied" } };

        var result = AuditChain.Verify(chain);

        Assert.False(result.Intact);
        Assert.Equal(3, result.FirstBrokenSequence);
        Assert.Equal(2, result.Checked);
    }

    [Fact]
    public void Editing_an_event_and_recomputing_only_its_own_hash_breaks_the_next_link()
    {
        var chain = Chain(5);
        var forged = chain[2].Record with { Detail = "nothing to see" };
        chain[2] = new ChainedAuditRecord(forged, chain[2].PreviousHash, AuditChain.ComputeHash(forged, chain[2].PreviousHash));

        var result = AuditChain.Verify(chain);

        Assert.False(result.Intact);
        Assert.Equal(4, result.FirstBrokenSequence);
    }

    [Fact]
    public void Removing_or_reordering_events_is_detected()
    {
        var removed = Chain(5);
        removed.RemoveAt(1);
        Assert.Equal(2, AuditChain.Verify(removed).FirstBrokenSequence);

        var swapped = Chain(5);
        (swapped[1], swapped[2]) = (swapped[2], swapped[1]);
        Assert.False(AuditChain.Verify(swapped).Intact);
    }

    [Fact]
    public void Hash_distinguishes_fields_that_concatenate_to_the_same_text()
    {
        var at = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var a = new AuditRecord(1, "u", null, "scan.run", "ab", null, "ok", "c", at);
        var b = new AuditRecord(1, "u", null, "scan.run", "a", null, "ok", "bc", at);

        Assert.NotEqual(AuditChain.ComputeHash(a, AuditChain.Genesis), AuditChain.ComputeHash(b, AuditChain.Genesis));
    }

    [Fact]
    public void Hash_does_not_depend_on_the_datetime_kind_storage_loses()
    {
        var utc = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var record = new AuditRecord(1, "u", null, "scan.run", null, null, "ok", "d", utc);
        var reloaded = record with { CreatedAt = DateTime.SpecifyKind(utc, DateTimeKind.Unspecified) };

        Assert.Equal(AuditChain.ComputeHash(record, AuditChain.Genesis), AuditChain.ComputeHash(reloaded, AuditChain.Genesis));
    }
}
