using RemoteClient;

namespace RemoteAppClient.Tests.Core;

/// <summary>The consoles start the OpenSSH tools by an absolute path, never by a bare name the PATH resolves.</summary>
public class SshLocatorTests
{
    [Fact]
    public void Ssh_is_an_absolute_path_to_an_existing_file()
    {
        string path;
        try { path = SshLocator.Ssh(); }
        catch (InvalidOperationException) { return; } // a runner without OpenSSH: nothing to locate
        Assert.True(Path.IsPathRooted(path), path);
        Assert.True(File.Exists(path), path);
        Assert.EndsWith(OperatingSystem.IsWindows() ? "ssh.exe" : "ssh", path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Ssh_keygen_is_an_absolute_path_to_an_existing_file()
    {
        string path;
        try { path = SshLocator.SshKeygen(); }
        catch (InvalidOperationException) { return; }
        Assert.True(Path.IsPathRooted(path), path);
        Assert.True(File.Exists(path), path);
    }
}
