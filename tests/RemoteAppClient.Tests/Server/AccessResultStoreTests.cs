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
    public void A_second_binding_keeps_the_answer_and_who_filed_it()
    {
        // The uplink side binds a fast answer from the command row and files it; the request side's own binding
        // arrives a moment later and must neither drop the outcome nor file it a second time.
        var s = new AccessResultStore();
        s.RecordOutcome("n1", "auto");                                 // the device answered first
        var byUplink = s.SetPending("n1", "alice", null, "A");         // bound from the command row
        byUplink.Audited = true;
        var byRequest = s.SetPending("n1", "alice", Guid.NewGuid(), "A");
        Assert.Equal("auto", byRequest.Outcome);
        Assert.True(byRequest.Audited);
        Assert.Equal("auto", s.Get("n1"));
    }

    [Fact]
    public void A_binding_without_an_answer_yet_is_simply_replaced()
    {
        var s = new AccessResultStore();
        s.SetPending("n1", "alice", null, "A");
        var again = s.SetPending("n1", "bob", null, "B");
        Assert.Equal("bob", again.Actor);
        Assert.Null(again.Outcome);
        s.RecordOutcome("n1", "denied");
        var third = s.SetPending("n1", "carol", null, "C");
        Assert.Equal("denied", third.Outcome);           // once answered, the answer survives any rebinding
        Assert.False(third.Audited);
    }

    [Fact]
    public void A_device_cannot_answer_another_devices_command()
    {
        var s = new AccessResultStore();
        var target = Guid.NewGuid();
        var other = Guid.NewGuid();
        s.SetPending("n1", "alice", target, "HOST");
        Assert.Null(s.RecordOutcome("n1", "granted", other));       // not its command
        Assert.Null(s.Get("n1"));
        Assert.Equal("auto", s.RecordOutcome("n1", "auto", target)!.Outcome);
    }

    [Fact]
    public void An_answer_parked_by_the_wrong_device_is_dropped_when_the_request_binds()
    {
        var s = new AccessResultStore();
        var target = Guid.NewGuid();
        s.RecordOutcome("n1", "granted", Guid.NewGuid());            // someone else got in first with this nonce
        var bound = s.SetPending("n1", "alice", target, "HOST");
        Assert.Null(bound.Outcome);                                   // not the target's answer: gone
        Assert.Equal("denied", s.RecordOutcome("n1", "denied", target)!.Outcome);

        var s2 = new AccessResultStore();
        s2.RecordOutcome("n2", "granted", target);                    // the right device was simply fast
        Assert.Equal("granted", s2.SetPending("n2", "alice", target, "HOST").Outcome);
    }

    [Fact]
    public void Parked_answers_have_a_ceiling()
    {
        var s = new AccessResultStore();
        var dev = Guid.NewGuid();
        int parked = 0;
        for (int i = 0; i < 10_100; i++)
            if (s.RecordOutcome("n" + i, "auto", dev) is not null) parked++;
        Assert.Equal(10_000, parked);
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
