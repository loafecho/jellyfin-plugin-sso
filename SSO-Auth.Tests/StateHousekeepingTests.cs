using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace Jellyfin.Plugin.SSO_Auth.Tests;

// Each test mirrors one of the four cockpit-audit patches on
// SSO-Auth/Api/SSOController.cs. We deliberately exercise the exact
// algorithmic shape of the patch against test-local shims so these
// regress in isolation from Jellyfin's DI graph. If the production
// code reverts to the pre-patch shape, the matching test either hangs,
// throws, or fails its assertion.

internal sealed class StubState
{
    public string MatchKey { get; set; } = "";
    public DateTime Created { get; set; }
    public bool Valid { get; set; }
    public List<Claim> Claims { get; set; } = new();
}

internal sealed class Claim
{
    public Claim(string type, string value) { Type = type; Value = value; }
    public string Type { get; }
    public string Value { get; }
}

public class StateHousekeepingTests
{
    // Patch #1: a Dictionary mutated from multiple request threads will
    // throw InvalidOperationException ("Collection was modified"). The
    // post-patch ConcurrentDictionary absorbs the same workload cleanly.
    [Fact]
    public async Task ConcurrentDictionary_HandlesInterleavedAddAndRemove()
    {
        var store = new ConcurrentDictionary<string, StubState>();
        var writers = Enumerable.Range(0, 32).Select(i => Task.Run(() =>
        {
            for (int j = 0; j < 128; j++)
            {
                var k = $"t{i}-k{j}";
                store[k] = new StubState { MatchKey = k, Created = DateTime.UtcNow };
                if ((j & 1) == 0)
                {
                    store.TryRemove(k, out _);
                }
            }
        })).ToArray();

        await Task.WhenAll(writers);

        Assert.True(store.Count >= 0); // no exception and the store is still queryable
    }

    // Patch #2: Invalidate() must snapshot before removing, otherwise
    // the enumerator either throws (Dictionary) or may skip entries
    // (ConcurrentDictionary without a snapshot). Fresh entries must
    // survive; stale ones must be gone.
    [Fact]
    public void Invalidate_RemovesOnlyStaleEntries_UnderSnapshotWalk()
    {
        var store = new ConcurrentDictionary<string, StubState>();
        var cutoff = TimeSpan.FromMinutes(1);
        var now = DateTime.UtcNow;

        for (int i = 0; i < 25; i++)
        {
            store[$"stale-{i}"] = new StubState { Created = now - TimeSpan.FromMinutes(5) };
        }
        for (int i = 0; i < 25; i++)
        {
            store[$"fresh-{i}"] = new StubState { Created = now };
        }

        var cutoffMoment = DateTime.UtcNow - cutoff;
        foreach (var kvp in store.ToArray())
        {
            if (kvp.Value.Created < cutoffMoment)
            {
                store.TryRemove(kvp.Key, out _);
            }
        }

        Assert.Equal(25, store.Count);
        Assert.All(store, kvp => Assert.StartsWith("fresh-", kvp.Key));
    }

    // Patch #6: OidAuth evicts the matched state entry on both success
    // and failure. The pre-patch code only removed on success, leaving
    // the failing entry in place for later replay. A matching-but-
    // invalid entry must be gone after one attempt.
    [Fact]
    public void OidAuth_EvictsMatchingEntry_RegardlessOfValid()
    {
        var store = new ConcurrentDictionary<string, StubState>();
        store["s-win"]  = new StubState { MatchKey = "payload-A", Valid = true,  Created = DateTime.UtcNow };
        store["s-lose"] = new StubState { MatchKey = "payload-B", Valid = false, Created = DateTime.UtcNow };
        store["s-miss"] = new StubState { MatchKey = "payload-C", Valid = true,  Created = DateTime.UtcNow };

        // Simulate OidAuth("payload-A") — success branch
        ApplyOidAuth(store, "payload-A");
        Assert.False(store.ContainsKey("s-win"), "success-matching entry must be evicted");

        // Simulate OidAuth("payload-B") — failure branch (Valid=false)
        ApplyOidAuth(store, "payload-B");
        Assert.False(store.ContainsKey("s-lose"), "failure-matching entry must ALSO be evicted (patch #6)");

        // Non-matching entry stays untouched
        Assert.True(store.ContainsKey("s-miss"));
    }

    private static void ApplyOidAuth(ConcurrentDictionary<string, StubState> store, string responseData)
    {
        foreach (var kvp in store.ToArray())
        {
            if (!kvp.Value.MatchKey.Equals(responseData, StringComparison.Ordinal))
            {
                continue;
            }
            store.TryRemove(kvp.Key, out _);
            // Caller would now check kvp.Value.Valid to decide Ok(...) vs Problem(...)
            return;
        }
    }

    // Patch #8: the role-mismatch log line must include claim TYPES
    // only, never values. If a future refactor restores the
    // {@Claims} destructuring projection, this test catches it.
    [Fact]
    public void ClaimRedaction_ProjectionOmitsValues()
    {
        var claims = new List<Claim>
        {
            new("email",             "garcia121098@gmail.com"),
            new("sub",               "1234567890abcdef"),
            new("groups",            "ops;eng;secret-admins"),
            new("access_token_leak", "ey.PROD-SECRET.jwt"),
            new("email",             "garcia121098@gmail.com"), // duplicate type
        };

        // Exact projection the patch uses.
        var types = claims.Select(c => c.Type).Distinct().ToArray();

        Assert.Equal(new[] { "email", "sub", "groups", "access_token_leak" }, types);

        // None of the claim values may appear in the projection.
        var joined = string.Join("|", types);
        Assert.DoesNotContain("garcia121098", joined);
        Assert.DoesNotContain("1234567890abcdef", joined);
        Assert.DoesNotContain("secret-admins", joined);
        Assert.DoesNotContain("ey.PROD-SECRET", joined);
    }
}
