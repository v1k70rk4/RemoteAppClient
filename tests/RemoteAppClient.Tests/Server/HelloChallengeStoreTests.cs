using RemoteServer.Services;

namespace RemoteAppClient.Tests.Server;

/// <summary>Windows Hello challenges: anyone can ask for one, so asking must not break someone else's sign-in.</summary>
public class HelloChallengeStoreTests
{
    [Fact]
    public void Someone_else_asking_in_your_name_does_not_replace_your_challenge()
    {
        var s = new HelloChallengeStore();
        var mine = s.Issue("alice")!;
        s.Issue("alice");                                    // a stranger asks in alice's name
        Assert.Same(mine, s.Consume("alice", n => n == mine));
    }

    [Fact]
    public void A_signature_that_matches_nothing_uses_nothing_up()
    {
        var s = new HelloChallengeStore();
        var mine = s.Issue("alice")!;
        Assert.Null(s.Consume("alice", _ => false));          // a junk attempt
        Assert.True(s.HasLive("alice"));
        Assert.Same(mine, s.Consume("alice", n => n == mine));
        Assert.Null(s.Consume("alice", n => n == mine));      // one use only
        Assert.False(s.HasLive("alice"));
    }

    [Fact]
    public void Only_the_few_newest_are_kept_per_user()
    {
        var s = new HelloChallengeStore();
        var first = s.Issue("bob")!;
        for (int i = 0; i < 4; i++) s.Issue("BOB");           // names are case-insensitive
        Assert.Null(s.Consume("bob", n => n == first));       // pushed out by the newer ones
    }

    [Fact]
    public void The_store_has_a_ceiling()
    {
        var s = new HelloChallengeStore();
        int issued = 0;
        for (int i = 0; i < 10_050; i++)
            if (s.Issue("user" + i) is not null) issued++;
        Assert.Equal(10_000, issued);
        Assert.NotNull(s.Issue("user5"));                      // an existing name still gets one
    }
}
