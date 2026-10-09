using System.Text.Json;
using RemoteClient;

namespace RemoteAppClient.Tests.Core;

/// <summary>The "remember this device" trust token is not written in the clear on Windows, and a config from an
/// older console (token in the clear) still loads.</summary>
public class ClientConfigTests
{
    [Fact]
    public void A_plain_token_from_an_older_config_still_loads()
    {
        var cfg = JsonSerializer.Deserialize<ClientConfig>("""{"TrustToken":"abc123","TrustUsername":"op"}""")!;
        Assert.Equal("abc123", cfg.TrustToken);
        Assert.Equal("op", cfg.TrustUsername);
    }

    [Fact]
    public void The_token_round_trips_through_json()
    {
        var cfg = new ClientConfig { TrustToken = "secret-trust", TrustUsername = "op" };
        var json = JsonSerializer.Serialize(cfg);
        var back = JsonSerializer.Deserialize<ClientConfig>(json)!;
        Assert.Equal("secret-trust", back.TrustToken);
    }

    [Fact]
    public void On_windows_the_stored_form_is_sealed()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI only exists there; elsewhere the file mode protects it
        var cfg = new ClientConfig { TrustToken = "secret-trust" };
        var json = JsonSerializer.Serialize(cfg);
        Assert.DoesNotContain("secret-trust", json);
        Assert.StartsWith("dpapi:", cfg.TrustTokenStored);
    }

    [Fact]
    public void Clearing_the_token_clears_the_stored_form()
    {
        var cfg = new ClientConfig { TrustToken = "secret-trust" };
        cfg.TrustToken = null;
        Assert.Null(cfg.TrustTokenStored);
        Assert.Null(cfg.TrustToken);
    }
}
