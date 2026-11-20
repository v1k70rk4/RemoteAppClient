using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using RemoteClient;

namespace RemoteAppClient.Tests.Core;

/// <summary>
/// The file window's two new judgements: which failure means the session is gone (reconnect and try again) and
/// which belongs to the one file (count it, go on); and the speed and time left it shows.
/// </summary>
public class FileTransferTests
{
    [Fact]
    public void No_connection_is_a_lost_session() =>
        Assert.True(FileClient.IsConnectionLost(new HttpRequestException("refused", new SocketException((int)SocketError.ConnectionRefused))));

    [Fact]
    public void A_transfer_cut_midway_is_a_lost_session() =>
        Assert.True(FileClient.IsConnectionLost(new HttpIOException(HttpRequestError.ResponseEnded, "ended")));

    [Fact]
    public void A_reset_socket_under_a_stream_is_a_lost_session() =>
        Assert.True(FileClient.IsConnectionLost(new IOException("reset", new SocketException((int)SocketError.ConnectionReset))));

    [Fact]
    public void A_token_the_device_no_longer_takes_is_a_lost_session() =>
        // The device clears its tokens when the tunnel closes: the old window then gets 403 on everything.
        Assert.True(FileClient.IsConnectionLost(new HttpRequestException("forbidden", null, HttpStatusCode.Forbidden)));

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]   // a file the device cannot read (locked, denied)
    [InlineData(HttpStatusCode.BadRequest)]
    public void An_error_about_the_file_is_not_a_lost_session(HttpStatusCode status) =>
        Assert.False(FileClient.IsConnectionLost(new HttpRequestException("x", null, status)));

    [Fact]
    public void A_local_disk_error_is_not_a_lost_session() =>
        Assert.False(FileClient.IsConnectionLost(new IOException("The disk is full.")));

    [Fact]
    public void Speed_is_over_the_last_seconds_not_from_the_start()
    {
        var m = new TransferMeter(TimeSpan.FromSeconds(3));
        Assert.Null(m.Sample(TimeSpan.Zero, 0));                       // nothing to measure yet
        for (int s = 1; s <= 10; s++) m.Sample(TimeSpan.FromSeconds(s), s * 1_000_000L);   // 1 MB/s
        // Then 4 MB/s for three seconds: the window sees only that.
        double? rate = null;
        for (int s = 11; s <= 13; s++) rate = m.Sample(TimeSpan.FromSeconds(s), 10_000_000L + (s - 10) * 4_000_000L);
        Assert.Equal(4_000_000, rate!.Value, 1);
    }

    [Fact]
    public void A_stall_shows_as_zero_speed()
    {
        var m = new TransferMeter(TimeSpan.FromSeconds(3));
        for (int s = 0; s <= 5; s++) m.Sample(TimeSpan.FromSeconds(s), s * 1_000_000L);
        double? rate = null;
        for (int s = 6; s <= 10; s++) rate = m.Sample(TimeSpan.FromSeconds(s), 5_000_000L);   // nothing more arrives
        Assert.Equal(0, rate!.Value, 1);
        Assert.Null(TransferMeter.Remaining(5_000_000, 10_000_000, rate));
    }

    [Fact]
    public void Time_left_is_the_rest_at_this_speed()
    {
        Assert.Equal(TimeSpan.FromSeconds(50), TransferMeter.Remaining(0, 100_000_000, 2_000_000));
        Assert.Null(TransferMeter.Remaining(100, 100, 2_000_000));     // done
        Assert.Null(TransferMeter.Remaining(0, 100, null));            // no speed yet
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1023, "1023 B")]
    [InlineData(1536, "1.50 KB")]
    [InlineData(12.3 * 1024 * 1024, "12.3 MB")]
    [InlineData(512.0 * 1024 * 1024, "512 MB")]
    [InlineData(1.25 * 1024 * 1024 * 1024, "1.25 GB")]
    public void Sizes_read_like_people_write_them(double bytes, string expected) =>
        Assert.Equal(expected, TransferMeter.Size(bytes, CultureInfo.InvariantCulture));

    [Fact]
    public void Sizes_use_the_culture_decimal_separator() =>
        Assert.Equal("12,3 MB", TransferMeter.Size(12.3 * 1024 * 1024, CultureInfo.GetCultureInfo("hu-HU")));

    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3725, "1:02:05")]
    public void Durations_are_minutes_and_seconds(int seconds, string expected) =>
        Assert.Equal(expected, TransferMeter.Duration(TimeSpan.FromSeconds(seconds)));
}
