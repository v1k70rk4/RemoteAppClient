using Microsoft.AspNetCore.Http;
using RemoteServer.Security;

namespace RemoteAppClient.Tests.Server;

/// <summary>
/// A token is a single-factor credential: the allowlist is exact routes, never "every GET under /admin",
/// because the admin API has GETs that hand out secrets.
/// </summary>
public class ApiTokenGateTests
{
    [Theory]
    [InlineData("/admin/server/logs")]
    [InlineData("/admin/server/diag")]
    [InlineData("/admin/server/status")]
    [InlineData("/admin/devices")]
    [InlineData("/admin/devices/online")]
    [InlineData("/admin/groups")]
    [InlineData("/admin/channels")]
    [InlineData("/admin/audit")]
    [InlineData("/admin/devices/abc123/events")]
    public void Read_scope_opens_the_listed_routes(string path)
    {
        Assert.True(ApiTokenGate.Allows(ApiTokenGate.ScopeRead, "GET", new PathString(path)));
        Assert.True(ApiTokenGate.Allows(ApiTokenGate.ScopeUpdate, "GET", new PathString(path)), "update includes read");
    }

    [Theory]
    [InlineData("GET", "/admin/devices/abc123/vnc-secret")]
    [InlineData("GET", "/admin/devices/abc/123/events")]
    [InlineData("GET", "/admin/devices//events")]
    [InlineData("GET", "/admin/server/backup")]
    [InlineData("GET", "/admin/users")]
    [InlineData("GET", "/admin/tokens-list")]
    [InlineData("GET", "/admin/me/tokens")]
    [InlineData("POST", "/admin/devices")]
    [InlineData("POST", "/admin/server/update")]
    [InlineData("DELETE", "/admin/devices/abc123")]
    public void Read_scope_never_reaches_secrets_or_writes(string method, string path)
    {
        Assert.False(ApiTokenGate.Allows(ApiTokenGate.ScopeRead, method, new PathString(path)));
    }

    [Theory]
    [InlineData("/admin/server/package", true)]
    [InlineData("/admin/server/update", true)]
    [InlineData("/admin/server/rollback", true)]
    [InlineData("/admin/server/settings", false)]
    [InlineData("/admin/devices/abc123/power", false)]
    [InlineData("/admin/users", false)]
    public void Update_scope_adds_exactly_the_three_self_update_posts(string path, bool allowed)
    {
        Assert.Equal(allowed, ApiTokenGate.Allows(ApiTokenGate.ScopeUpdate, "POST", new PathString(path)));
    }

    [Fact]
    public void Unknown_scopes_open_nothing()
    {
        Assert.False(ApiTokenGate.IsKnownScope("admin"));
        Assert.False(ApiTokenGate.IsKnownScope(null));
        Assert.False(ApiTokenGate.Allows("admin", "GET", new PathString("/admin/server/status")));
    }

    [Fact]
    public void Ten_rejected_tokens_from_one_address_block_it_and_only_it()
    {
        var ip = "203.0.113." + Random.Shared.Next(1, 250);      // the brake is process-wide: use a fresh address
        var other = "198.51.100." + Random.Shared.Next(1, 250);
        for (int i = 0; i < 9; i++) ApiTokenGate.RecordFailure(ip);
        Assert.False(ApiTokenGate.IsBlocked(ip));
        ApiTokenGate.RecordFailure(ip);
        Assert.True(ApiTokenGate.IsBlocked(ip));
        Assert.False(ApiTokenGate.IsBlocked(other));
    }
}
