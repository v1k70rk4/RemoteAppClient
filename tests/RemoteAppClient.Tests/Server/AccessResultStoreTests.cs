using RemoteServer.Services;

namespace RemoteAppClient.Tests.Server;

/// <summary>An agent on a fast link answers before the request's bookkeeping is done; the answer must survive.</summary>
public class AccessResultStoreTests
{
    [Fact]
    public void Request_then_answer_is_the_ordinary_case()
    {
        var s = new AccessResultStore();
        var id = Guid.NewGuid();
        var pending = s.SetPending("n1", "alice", id, "HOST");
        Assert.Null(pending.Outcome);
        Assert.Null(s.Get("n1"));

        var answered = s.RecordOutcome("n1", "auto");
        Assert.NotNull(answered);
        Assert.Equal("alice", answered!.Actor);
        Assert.Equal(id, answered.DeviceId);
        Assert.False(answered.Placeholder);
        Assert.Equal("auto", s.Get("n1"));
    }

    [Fact]
    public void An_answer_that_beats_the_request_is_parked_and_then_merged()
    {
        var s = new AccessResultStore();
        var parked = s.RecordOutcome("n1", "granted");
        Assert.NotNull(parked);
        Assert.True(parked!.Placeholder);
        Assert.Equal("?", parked.Actor);

        var bound = s.SetPending("n1", "alice", Guid.NewGuid(), "HOST");
        Assert.False(bound.Placeholder);
        Assert.Equal("alice", bound.Actor);
        Assert.Equal("granted", bound.Outcome);           // the parked answer was kept, not overwritten
        Assert.Equal("granted", s.Get("n1"));
        Assert.Equal("alice", s.GetEntry("n1")!.Actor);
    }

    [Fact]
    public void A_bound_request_is_not_replaced_by_a_second_binding()
    {
        var s = new AccessResultStore();
        s.SetPending("n1", "alice", null, "A");
        s.RecordOutcome("n1", "denied");
        var again = s.SetPending("n1", "bob", null, "B");
        Assert.Equal("bob", again.Actor);               // the newest binding wins for a non-placeholder entry
        Assert.Null(again.Outcome);
    }

    [Fact]
    public void Unknown_and_empty_nonces_are_handled()
    {
        var s = new AccessResultStore();
        Assert.Null(s.Get("nope"));
        Assert.Null(s.GetEntry("nope"));
        Assert.Null(s.RecordOutcome("", "auto"));
        var e = s.SetPending("", "alice", null, "A");
        Assert.Equal("alice", e.Actor);
        Assert.Null(s.GetEntry(""));
    }

    [Fact]
    public void Expired_entries_vanish_on_read()
    {
        var s = new AccessResultStore();
        var e = s.SetPending("n1", "alice", null, "A");
        e.Expires = DateTimeOffset.UtcNow.AddSeconds(-1);
        Assert.Null(s.GetEntry("n1"));
        Assert.Null(s.Get("n1"));
    }
}
