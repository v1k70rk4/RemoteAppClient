using RemoteAgent.Commands;

namespace RemoteAgent.Update;

/// <summary>
/// Update commands that arrive while one is running. A rollout of three packages (console, agent, updater)
/// lands within seconds, and the agent runs one update at a time: it used to drop what came in meanwhile, and
/// a device took the first package only, the rest ten minutes later when the server offered them again. Now
/// the newest command per component waits here and runs after the current one, in the order the server
/// offers them - the agent's own package last, since its replacement restarts this process and whatever was
/// still waiting would go with it. Single-flight stays: <see cref="TryStart"/> says whether the caller runs the
/// command or has parked it, and <see cref="Next"/> hands the runner the next one or marks it idle, both under
/// one lock, so a command parked at the moment the runner finishes is never left behind.
/// </summary>
public sealed class UpdateQueue
{
    private static readonly string[] Order = ["vnc", "client", "updater", "agent"];

    private readonly Dictionary<string, AgentCommand> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();
    private bool _running;

    /// <summary>The component an update command's target names, in the server's spelling.</summary>
    public static string Component(string? target) =>
        string.IsNullOrWhiteSpace(target) ? "agent"
        : string.Equals(target, "helper", StringComparison.OrdinalIgnoreCase) ? "updater"
        : target.Trim().ToLowerInvariant();

    /// <summary>True: nothing runs, the caller runs this command now. False: one runs, this one waits its turn
    /// (replacing an earlier command for the same component).</summary>
    public bool TryStart(AgentCommand cmd)
    {
        lock (_lock)
        {
            if (_running) { _pending[Component(cmd.Data?.UpdateTarget)] = cmd; return false; }
            _running = true;
            return true;
        }
    }

    /// <summary>The next command to run, or null when nothing waits - the runner is then idle and the next
    /// <see cref="TryStart"/> runs its command.</summary>
    public AgentCommand? Next()
    {
        lock (_lock)
        {
            foreach (var comp in Order)
                if (_pending.Remove(comp, out var cmd)) return cmd;
            foreach (var comp in _pending.Keys)
                if (_pending.Remove(comp, out var cmd)) return cmd;
            _running = false;
            return null;
        }
    }

    public bool IsRunning { get { lock (_lock) return _running; } }
    public int Pending { get { lock (_lock) return _pending.Count; } }
}
