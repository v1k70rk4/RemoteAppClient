using RemoteAgent.Admin;

namespace RemoteAppClient.Tests.Contracts;

/// <summary>A server is reached over TLS; plain HTTP is for a server on the same machine only.</summary>
public class ServerUrlPolicyTests
{
    [Theory]
    [InlineData("https://racd.example.com")]
    [InlineData("https://racd.example.com:8443/")]
    [InlineData("wss://racd.example.com/agent")]
    [InlineData("http://localhost:5000")]
    [InlineData("http://127.0.0.1:5000/")]
    [InlineData("ws://[::1]:5000/agent")]
    public void Allowed(string url) => Assert.True(ServerUrlPolicy.IsAllowed(url));

    [Theory]
    [InlineData("http://racd.example.com")]
    [InlineData("http://10.0.0.5:5000")]
    [InlineData("ws://racd.example.com/agent")]
    [InlineData("ftp://racd.example.com")]
    [InlineData("racd.example.com")]
    [InlineData("")]
    [InlineData(null)]
    public void Refused(string? url) => Assert.False(ServerUrlPolicy.IsAllowed(url));
}
