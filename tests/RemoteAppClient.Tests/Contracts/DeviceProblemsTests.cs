using RemoteAgent.Admin;

namespace RemoteAppClient.Tests.Contracts;

public class DeviceProblemsTests
{
    [Theory]
    [InlineData(null, "", "")]
    [InlineData("", "", "")]
    [InlineData("   ", "", "")]
    [InlineData("clock-skew:+88", "clock-skew", "+88")]
    [InlineData("clock-skew:-34", "clock-skew", "-34")]
    [InlineData("some-code", "some-code", "")]
    [InlineData("a:b:c", "a", "b:c")]
    public void Parse_splits_code_and_value(string? problem, string code, string value)
    {
        Assert.Equal((code, value), DeviceProblems.Parse(problem));
    }
}
