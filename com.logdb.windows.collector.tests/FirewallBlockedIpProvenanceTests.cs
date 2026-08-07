using com.logdb.windows.collector.Services.Firewall;
using com.logdb.windows.collector.shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;

namespace com.logdb.windows.collector.tests;

/// <summary>
/// Covers what the Blocked IPs view promises the operator: which IPs are
/// blocked, since when, why, and by whom — plus the two ways that answer can
/// silently go wrong (a failed feed fetch being read as "unblock everything",
/// and a first-observed timestamp being presented as a real block time).
/// </summary>
public sealed class FirewallBlockedIpProvenanceTests : IDisposable
{
    private readonly string _indexPath =
        Path.Combine(Path.GetTempPath(), $"logdb-blocked-index-{Guid.NewGuid():N}.json");

    private FirewallBlockedIpIndex NewIndex() =>
        new(_indexPath, NullLogger<FirewallBlockedIpIndex>.Instance);

    private static Dictionary<string, HashSet<string>> Sets(
        params (string Source, string[] Ips)[] sources)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (source, ips) in sources)
            result[source] = ips.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return result;
    }

    private static HashSet<string> NoneRetained() => new(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        if (File.Exists(_indexPath)) File.Delete(_indexPath);
    }

    // ---- added_at parsing -------------------------------------------------

    [Fact]
    public void ParseAddedAt_ReadsSeconds()
    {
        var parsed = GuardBlocklistClient.ParseAddedAt(1_700_000_000);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), parsed);
    }

    [Fact]
    public void ParseAddedAt_ReadsMilliseconds()
    {
        // Same instant, expressed in ms — must not be read as the year 55000.
        var parsed = GuardBlocklistClient.ParseAddedAt(1_700_000_000_000);
        Assert.Equal(new DateTime(2023, 11, 14, 22, 13, 20, DateTimeKind.Utc), parsed);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void ParseAddedAt_TreatsUnsetAsUnknownRatherThan1970(long raw)
    {
        Assert.Null(GuardBlocklistClient.ParseAddedAt(raw));
    }

    // ---- provenance -------------------------------------------------------

    [Fact]
    public void GuardIp_KeepsBackendBlockTimeReasonAndAuthor()
    {
        var index = NewIndex();
        var blockedAt = new DateTime(2026, 3, 2, 9, 30, 0, DateTimeKind.Utc);
        var syncTime = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

        index.Reconcile(
            Sets(("LogDB Guard", ["52.238.192.78"])),
            NoneRetained(),
            syncTime,
            new Dictionary<string, BlockedIpProvenance>(StringComparer.OrdinalIgnoreCase)
            {
                ["52.238.192.78"] = new("SSH brute force", "vladimir@logdb.com", blockedAt)
            });

        var entry = Assert.Single(index.Query(null, 100).Entries);
        Assert.Equal("52.238.192.78", entry.Ip);
        Assert.Equal("SSH brute force", entry.Reason);
        Assert.Equal("vladimir@logdb.com", entry.AddedBy);
        // The real block time, not the sync cycle time five months later.
        Assert.Equal(blockedAt, entry.BlockedAtUtc);
        Assert.False(entry.BlockedAtApproximate);
    }

    [Fact]
    public void PublicFeedIp_IsMarkedApproximateAndCarriesNoReason()
    {
        var index = NewIndex();
        var syncTime = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

        index.Reconcile(Sets(("FireHOL Level 1", ["203.0.113.5"])), NoneRetained(), syncTime);

        var entry = Assert.Single(index.Query(null, 100).Entries);
        Assert.Equal(syncTime, entry.BlockedAtUtc);
        Assert.True(entry.BlockedAtApproximate);
        Assert.Equal(string.Empty, entry.Reason);
        Assert.Equal(string.Empty, entry.AddedBy);
    }

    [Fact]
    public void ExistingEntryWithoutProvenance_HealsOnceGuardSuppliesIt()
    {
        var index = NewIndex();
        var firstSeen = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        var realBlock = new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc);

        // Cycle 1: an index written before provenance existed.
        index.Reconcile(Sets(("LogDB Guard", ["198.51.100.9"])), NoneRetained(), firstSeen);
        Assert.True(Assert.Single(index.Query(null, 100).Entries).BlockedAtApproximate);

        // Cycle 2: the same IP, now with backend detail.
        index.Reconcile(
            Sets(("LogDB Guard", ["198.51.100.9"])),
            NoneRetained(),
            firstSeen.AddHours(1),
            new Dictionary<string, BlockedIpProvenance>(StringComparer.OrdinalIgnoreCase)
            {
                ["198.51.100.9"] = new("Credential stuffing", "ops", realBlock)
            });

        var entry = Assert.Single(index.Query(null, 100).Entries);
        Assert.Equal("Credential stuffing", entry.Reason);
        Assert.Equal(realBlock, entry.BlockedAtUtc);
        Assert.False(entry.BlockedAtApproximate);
    }

    [Fact]
    public void MissingProvenance_DoesNotEraseWhatWasAlreadyRecorded()
    {
        var index = NewIndex();
        var realBlock = new DateTime(2026, 7, 15, 8, 0, 0, DateTimeKind.Utc);
        var now = new DateTime(2026, 8, 7, 12, 0, 0, DateTimeKind.Utc);

        index.Reconcile(
            Sets(("LogDB Guard", ["198.51.100.9"])),
            NoneRetained(),
            now,
            new Dictionary<string, BlockedIpProvenance>(StringComparer.OrdinalIgnoreCase)
            {
                ["198.51.100.9"] = new("Credential stuffing", "ops", realBlock)
            });

        // A later cycle where Guard answered but detail was unavailable.
        index.Reconcile(Sets(("LogDB Guard", ["198.51.100.9"])), NoneRetained(), now.AddHours(1));

        var entry = Assert.Single(index.Query(null, 100).Entries);
        Assert.Equal("Credential stuffing", entry.Reason);
        Assert.Equal(realBlock, entry.BlockedAtUtc);
    }

    [Fact]
    public void ReasonAndAuthorAreSearchable()
    {
        var index = NewIndex();
        index.Reconcile(
            Sets(("LogDB Guard", ["198.51.100.9", "198.51.100.10"])),
            NoneRetained(),
            DateTime.UtcNow,
            new Dictionary<string, BlockedIpProvenance>(StringComparer.OrdinalIgnoreCase)
            {
                ["198.51.100.9"] = new("SSH brute force", "vladimir", null),
                ["198.51.100.10"] = new("Scraping", "ops", null)
            });

        Assert.Equal("198.51.100.9", Assert.Single(index.Query("brute", 100).Entries).Ip);
        Assert.Equal("198.51.100.10", Assert.Single(index.Query("ops", 100).Entries).Ip);
    }

    // ---- rule naming ------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void BlankRulePrefix_FallsBackInsteadOfNamingRulesDashSomething(string? configured)
    {
        // A blank prefix used to build rules called " - FireHOL Level 1" while
        // the listing looked for "LogDB Firewall*", and made the orphan prune
        // glob '-like "*"' across every rule on the host.
        Assert.Equal("LogDB Firewall", FirewallSyncEngine.ResolveRuleNamePrefix(configured));
    }

    [Fact]
    public void ConfiguredRulePrefix_IsTrimmedSoRuleNamesStayStable()
    {
        Assert.Equal("Acme FW", FirewallSyncEngine.ResolveRuleNamePrefix("  Acme FW  "));
    }

    [Fact]
    public void SourceExtraction_RoundTripsARuleNameBuiltFromTheSamePrefix()
    {
        var prefix = FirewallSyncEngine.ResolveRuleNamePrefix(null);
        Assert.Equal("FireHOL Level 1",
            FirewallSyncEngine.ExtractSourceFromDisplayName($"{prefix} - FireHOL Level 1", null!));
        // Chunked rules must resolve to the same feed, not "FireHOL Level 1 (2/3)".
        Assert.Equal("FireHOL Level 1",
            FirewallSyncEngine.ExtractSourceFromDisplayName($"{prefix} - FireHOL Level 1 (2/3)", ""));
    }

    // ---- the unblock-on-failure regression --------------------------------

    [Fact]
    public void DegradedSource_KeepsItsEntries()
    {
        var index = NewIndex();
        var now = DateTime.UtcNow;
        index.Reconcile(Sets(("FireHOL Level 1", ["203.0.113.5"])), NoneRetained(), now);

        // Next cycle the feed could not be fetched: it contributes no active set
        // but is retained. Its entries must survive — the OS is still blocking.
        var retained = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "FireHOL Level 1" };
        var (_, removed) = index.Reconcile(
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase), retained, now);

        Assert.Equal(0, removed);
        Assert.Equal("203.0.113.5", Assert.Single(index.Query(null, 100).Entries).Ip);
    }

    [Fact]
    public void DisabledSource_IsStillPruned()
    {
        var index = NewIndex();
        var now = DateTime.UtcNow;
        index.Reconcile(Sets(("FireHOL Level 1", ["203.0.113.5"])), NoneRetained(), now);

        // Feed switched off: absent from both the active sets and the retained
        // set, so its entries go — the distinction that makes the fix safe.
        var (_, removed) = index.Reconcile(
            new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase), NoneRetained(), now);

        Assert.Equal(1, removed);
        Assert.Empty(index.Query(null, 100).Entries);
    }
}
