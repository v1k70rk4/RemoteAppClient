using RemoteAgent.Telemetry;

namespace RemoteAppClient.Tests.Agent;

/// <summary>Reading tpmtool's "-Key: Value" output into the TPM telemetry fields.</summary>
public class TpmInfoTests
{
    // Real `tpmtool getdeviceinformation` output (Windows 11, AMD fTPM), trimmed to the lines that matter plus a
    // nested block, which must not confuse the parser.
    private const string Ready = """

        -TPM Present: True
        -TPM Version: 2.0
        -TPM Manufacturer ID: AMD
        -TPM Manufacturer Full Name: AMD
        -Is Initialized: True
        -Ready For Storage: True
        -Ready For Attestation: True
        -Is Capable For Attestation: True
        -Clear Needed To Recover: False
        -TPM Has Vulnerable Firmware: False
        -TPM Errata Date: Friday, March 02, 2018
        -Lockout Information:
        	-Is Locked Out: False
        	-Lockout Counter: 0
        """;

    [Fact]
    public void A_ready_tpm_reports_every_field()
    {
        var s = TpmInfo.Parse(Ready.Replace("\n", "\r\n"));   // tpmtool writes CRLF
        Assert.Equal(new TpmInfo.State(true, "2.0", "AMD", true, true, false), s);
    }

    [Fact]
    public void No_tpm_is_a_definite_no_not_unknown()
    {
        var s = TpmInfo.Parse("\r\n-TPM Present: False\r\n");
        Assert.False(s.Present);
        Assert.False(s.Ready);
        Assert.False(s.Attestation);
    }

    [Fact]
    public void Unparseable_output_is_unknown()
    {
        Assert.Equal(TpmInfo.State.Unknown, TpmInfo.Parse(""));
        Assert.Equal(TpmInfo.State.Unknown, TpmInfo.Parse("tpmtool: something went wrong"));
    }

    [Fact]
    public void A_tpm_not_ready_for_attestation_and_with_vulnerable_firmware()
    {
        var s = TpmInfo.Parse("-TPM Present: True\n-TPM Version: 2.0\n-TPM Manufacturer ID: IFX\n-Ready For Storage: True\n-Ready For Attestation: False\n-TPM Has Vulnerable Firmware: True\n");
        Assert.Equal(new TpmInfo.State(true, "2.0", "IFX", true, false, true), s);
    }
}
