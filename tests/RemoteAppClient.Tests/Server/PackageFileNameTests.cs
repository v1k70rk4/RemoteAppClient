using RemoteServer.Services;

namespace RemoteAppClient.Tests.Server;

/// <summary>An uploaded package's file name carries its hash: different bytes never share a file, the same bytes do.</summary>
public class PackageFileNameTests
{
    private const string A = "678F1BF0AFD84C00286FE30F2C55C78281823ED2893046A5C4067DBC06C469C7";
    private const string B = "4D9EE7E3A622D5297DF4BDF8F21A152120903115A630C57B19CCB9E666034451";

    [Fact]
    public void Same_version_different_bytes_land_in_different_files()
    {
        var first = PackageFileName.For("updater", "beta", "2.3.0.0", A);
        var second = PackageFileName.For("updater", "beta", "2.3.0.0", B);
        Assert.NotEqual(first, second);
        Assert.Equal("updater-beta-2.3.0.0-678f1bf0afd8.exe", first);
    }

    [Fact]
    public void Same_bytes_land_in_the_same_file_whatever_the_hex_case()
    {
        Assert.Equal(PackageFileName.For("agent", "rtm", "2.3.1.0", A), PackageFileName.For("agent", "rtm", "2.3.1.0", A.ToLowerInvariant()));
    }

    [Fact]
    public void Vnc_is_an_msi_and_the_version_cannot_escape_the_folder()
    {
        var name = PackageFileName.For("vnc", "rtm", "../2.8.89.0", B);
        Assert.EndsWith(".msi", name);
        Assert.DoesNotContain("/", name);
        Assert.DoesNotContain("\\", name);
        Assert.Equal(name, Path.GetFileName(name));
    }

    [Fact]
    public void A_short_hash_is_refused()
    {
        Assert.Throws<ArgumentException>(() => PackageFileName.For("agent", "rtm", "1", "abc"));
    }
}
