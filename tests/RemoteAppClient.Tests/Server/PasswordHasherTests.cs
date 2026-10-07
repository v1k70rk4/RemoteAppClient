using RemoteServer.Security;

namespace RemoteAppClient.Tests.Server;

public class PasswordHasherTests
{
    [Fact]
    public void Hashes_are_argon2id_with_the_fixed_cost_and_a_fresh_salt()
    {
        var h1 = PasswordHasher.Hash("correct horse battery staple");
        var h2 = PasswordHasher.Hash("correct horse battery staple");
        Assert.StartsWith("$argon2id$m=65536,t=3,p=4$", h1);
        Assert.NotEqual(h1, h2);
        Assert.True(PasswordHasher.Verify("correct horse battery staple", h1));
        Assert.True(PasswordHasher.Verify("correct horse battery staple", h2));
    }

    [Fact]
    public void A_wrong_password_or_a_damaged_record_never_verifies()
    {
        var h = PasswordHasher.Hash("secret-1");
        Assert.False(PasswordHasher.Verify("secret-2", h));
        Assert.False(PasswordHasher.Verify("secret-1", h.Replace("argon2id", "argon2i")));
        Assert.False(PasswordHasher.Verify("secret-1", "not a hash at all"));
        Assert.False(PasswordHasher.Verify("secret-1", ""));
    }
}
