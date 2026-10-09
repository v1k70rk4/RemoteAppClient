using RemoteClient;

namespace RemoteAppClient.Tests.Core;

/// <summary>Names from a device's file listing must not be able to steer a copy out of the chosen folder.</summary>
public class SafeNamesTests
{
    [Theory]
    [InlineData("report.pdf")]
    [InlineData("Új mappa")]
    [InlineData(".config")]
    [InlineData("a..b")]
    [InlineData("CONFIG.SYS")]
    [InlineData("console.log")]
    public void Ordinary_names_pass(string name) => Assert.True(SafeNames.IsPlainName(name));

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"..\..\Startup\x.bat")]
    [InlineData("../x")]
    [InlineData(@"C:\Users\op\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup\x.bat")]
    [InlineData("file.txt:hidden")]
    [InlineData("x*")]
    [InlineData("a\tb")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1")]
    [InlineData("lpt9.log")]
    public void Anything_that_is_not_one_plain_name_is_refused(string name) => Assert.False(SafeNames.IsPlainName(name));
}
