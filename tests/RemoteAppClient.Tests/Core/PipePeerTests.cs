using System.IO.Pipes;
using RemoteClient;

namespace RemoteAppClient.Tests.Core;

/// <summary>A pipe whose server is an ordinary user process - such as this test - is not the agent service.</summary>
public class PipePeerTests
{
    [Fact]
    public async Task A_pipe_served_by_a_user_process_is_refused()
    {
        if (!OperatingSystem.IsWindows()) return; // named pipes with session ids are a Windows thing

        var name = "RemoteAppClient.Tests." + Guid.NewGuid().ToString("N");
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var accept = server.WaitForConnectionAsync();
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        await accept;

        Assert.False(PipePeer.IsAgentService(client, out var why));
        Assert.Contains("session", why); // the test runs in the user's session, never in session 0
    }

    [Fact]
    public void Off_windows_there_is_nothing_to_check()
    {
        if (OperatingSystem.IsWindows()) return;
        using var a = new AnonymousPipeServerStream();
        Assert.True(PipePeer.IsAgentService(a, out _));
    }
}
