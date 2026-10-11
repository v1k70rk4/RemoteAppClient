using RemoteAgent.Security;

namespace RemoteAppClient.Tests.Agent;

/// <summary>The agent reads its tunnel certificate's validity itself and decides when to ask for a new one.</summary>
public class SshCertificateTests
{
    // ssh-keygen -s ca -I testdev -n agent -V 20260101120000:20260104120000 -z 12345 k.pub (made in CET, hence 11:00Z)
    private const string Sample =
        "ssh-ed25519-cert-v01@openssh.com AAAAIHNzaC1lZDI1NTE5LWNlcnQtdjAxQG9wZW5zc2guY29tAAAAIL5uwvucEyyyy1ZdRSbopjAGLxuyCcS/oiyGY0f+gNUjAAAAIJYsMOYJ21Op5PjoLhjhzcsFnFB4qlhs9RWF2gfB6jR+AAAAAAAAMDkAAAABAAAAB3Rlc3RkZXYAAAAJAAAABWFnZW50AAAAAGlWU7AAAAAAaVpIMAAAAAAAAACCAAAAFXBlcm1pdC1YMTEtZm9yd2FyZGluZwAAAAAAAAAXcGVybWl0LWFnZW50LWZvcndhcmRpbmcAAAAAAAAAFnBlcm1pdC1wb3J0LWZvcndhcmRpbmcAAAAAAAAACnBlcm1pdC1wdHkAAAAAAAAADnBlcm1pdC11c2VyLXJjAAAAAAAAAAAAAAAzAAAAC3NzaC1lZDI1NTE5AAAAIMpqqKnPLO0Cgt+vp2mcpfd1pCQV81EveGp4uONsD879AAAAUwAAAAtzc2gtZWQyNTUxOQAAAED7Vo6mJrsD2kMk29eiuJllP3EG4F6mB4tTtWCHm6dkdrorDTEcGN5BH4KgYgJbTMl6bDgKRC4vdQxF2GbzgO4O k";

    private static readonly DateTimeOffset After = new(2026, 1, 1, 11, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Before = new(2026, 1, 4, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Reads_serial_key_id_and_validity_from_the_wire_format()
    {
        var v = SshCertificate.Parse(Sample);
        Assert.NotNull(v);
        Assert.Equal("ssh-ed25519-cert-v01@openssh.com", v.Type);
        Assert.Equal(12345UL, v.Serial);
        Assert.Equal("testdev", v.KeyId);
        Assert.Equal(After, v.ValidAfter);
        Assert.Equal(Before, v.ValidBefore);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIMpqqKnPLO0Cgt+vp2mcpfd1pCQV81EveGp4uONsD879 k")] // a plain key, not a certificate
    [InlineData("ssh-ed25519-cert-v01@openssh.com not-base64 k")]
    [InlineData("ssh-ed25519-cert-v01@openssh.com AAAAIHNzaC1lZDI1NTE5LWNlcnQtdjAxQG9wZW5zc2guY29t k")] // truncated after the type
    public void Anything_else_is_not_a_certificate(string? line) => Assert.Null(SshCertificate.Parse(line));

    [Fact]
    public void A_missing_or_unreadable_certificate_is_renewed() => Assert.Equal("renew", SshCertificate.Due(null, After));

    [Fact]
    public void A_short_certificate_is_renewed_only_near_its_end()
    {
        var v = SshCertificate.Parse(Sample)!;
        Assert.Null(SshCertificate.Due(v, After.AddHours(1)));
        Assert.Null(SshCertificate.Due(v, Before.AddHours(-37)));
        Assert.Equal("renew", SshCertificate.Due(v, Before.AddHours(-35)));
        Assert.Equal("renew", SshCertificate.Due(v, Before.AddDays(30))); // long expired: still just a renewal
    }

    [Fact]
    public void A_long_lived_certificate_from_enrollment_means_a_key_rotation()
    {
        var v = new SshCertificate.Validity("ssh-ed25519-cert-v01@openssh.com", 1, "dev", After, After.AddDays(825));
        Assert.Equal("rotate", SshCertificate.Due(v, After.AddDays(10)));
        Assert.Equal("renew", SshCertificate.Due(v, After.AddDays(825).AddHours(-1))); // but near its end, a renewal first
    }
}
