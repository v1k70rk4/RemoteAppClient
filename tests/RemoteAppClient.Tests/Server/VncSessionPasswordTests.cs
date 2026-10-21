using RemoteServer.Services;

namespace RemoteAppClient.Tests.Server;

/// <summary>A session's VNC password is reused for an hour from the time it was chosen, then replaced.</summary>
public class VncSessionPasswordTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Eight_characters_from_the_unambiguous_alphabet_and_not_the_same_twice()
    {
        var a = VncSessionPassword.Generate();
        var b = VncSessionPassword.Generate();
        Assert.Equal(8, a.Length);
        Assert.DoesNotContain(a, c => "0O1lI".Contains(c));
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Reused_within_the_hour_replaced_after_it()
    {
        Assert.False(VncSessionPassword.Reusable(null, T0));
        Assert.True(VncSessionPassword.Reusable(T0, T0));
        Assert.True(VncSessionPassword.Reusable(T0, T0.AddMinutes(59)));
        Assert.False(VncSessionPassword.Reusable(T0, T0.AddMinutes(60)));
        Assert.False(VncSessionPassword.Reusable(T0.AddMinutes(5), T0));   // a stamp from the future is not trusted
    }
}
