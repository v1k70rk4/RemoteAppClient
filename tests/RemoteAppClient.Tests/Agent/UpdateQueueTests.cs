using RemoteAgent.Commands;
using RemoteAgent.Update;

namespace RemoteAppClient.Tests.Agent;

/// <summary>A rollout of several packages lands within seconds; the agent runs one update at a time and the rest
/// wait their turn, the agent's own package last since its replacement restarts the process.</summary>
public class UpdateQueueTests
{
    private static AgentCommand Update(string? target, string version = "1.0") =>
        new() { Type = CommandTypes.Update, Data = new CommandData { UpdateTarget = target, UpdateVersion = version } };

    [Fact]
    public void The_first_command_runs_and_the_runner_goes_idle_when_nothing_waits()
    {
        var q = new UpdateQueue();
        Assert.True(q.TryStart(Update("agent")));
        Assert.True(q.IsRunning);
        Assert.Null(q.Next());
        Assert.False(q.IsRunning);
        Assert.True(q.TryStart(Update("client")));
    }

    [Fact]
    public void Commands_arriving_during_a_run_wait_and_come_out_in_the_servers_order_agent_last()
    {
        var q = new UpdateQueue();
        Assert.True(q.TryStart(Update("client")));
        Assert.False(q.TryStart(Update("agent")));
        Assert.False(q.TryStart(Update("updater")));
        Assert.False(q.TryStart(Update("vnc")));
        Assert.Equal(3, q.Pending);

        Assert.Equal("vnc", q.Next()!.Data!.UpdateTarget);
        Assert.Equal("updater", q.Next()!.Data!.UpdateTarget);
        Assert.Equal("agent", q.Next()!.Data!.UpdateTarget);
        Assert.Null(q.Next());
        Assert.False(q.IsRunning);
    }

    [Fact]
    public void A_newer_command_for_the_same_component_replaces_the_waiting_one()
    {
        var q = new UpdateQueue();
        q.TryStart(Update("client"));
        q.TryStart(Update("agent", "3.0.0.5"));
        q.TryStart(Update("agent", "3.0.0.6"));
        Assert.Equal(1, q.Pending);
        Assert.Equal("3.0.0.6", q.Next()!.Data!.UpdateVersion);
    }

    [Fact]
    public void After_the_grace_period_what_arrived_meanwhile_goes_first_and_the_agent_package_last()
    {
        var q = new UpdateQueue();
        Assert.True(q.TryStart(Update("agent")));
        Assert.False(q.TryStart(Update("client")));
        Assert.False(q.TryStart(Update("vnc")));

        Assert.Equal("vnc", q.Reorder(Update("agent"))!.Data!.UpdateTarget);   // the agent's own package waits
        Assert.Equal("client", q.Next()!.Data!.UpdateTarget);
        Assert.Equal("agent", q.Next()!.Data!.UpdateTarget);
        Assert.Null(q.Next());
        Assert.False(q.IsRunning);
    }

    [Fact]
    public void Reorder_runs_the_command_itself_when_nothing_earlier_waits_and_a_newer_one_for_the_component_replaces_it()
    {
        var q = new UpdateQueue();
        var client = Update("client");
        Assert.True(q.TryStart(client));
        Assert.Same(client, q.Reorder(client));
        Assert.True(q.IsRunning);

        Assert.Null(q.Next());

        var older = Update("agent", "3.0.0.5");
        Assert.True(q.TryStart(older));
        Assert.False(q.TryStart(Update("agent", "3.0.0.6")));   // a newer agent package during the grace minute
        Assert.Equal("3.0.0.6", q.Reorder(older).Data!.UpdateVersion);
        Assert.Null(q.Next());                                   // the older one is not applied after it
    }

    [Theory]
    [InlineData(null, "agent")]
    [InlineData("", "agent")]
    [InlineData("Agent", "agent")]
    [InlineData("helper", "updater")]
    [InlineData("updater", "updater")]
    public void The_target_is_read_the_way_the_server_names_components(string? target, string component) =>
        Assert.Equal(component, UpdateQueue.Component(target));

    [Fact]
    public void A_component_the_order_does_not_know_still_comes_out()
    {
        var q = new UpdateQueue();
        q.TryStart(Update("agent"));
        q.TryStart(Update("something-new"));
        Assert.Equal("something-new", q.Next()!.Data!.UpdateTarget);
        Assert.Null(q.Next());
    }
}
