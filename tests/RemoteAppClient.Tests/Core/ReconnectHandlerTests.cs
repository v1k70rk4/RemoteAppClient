using System.Net;
using RemoteClient;

namespace RemoteAppClient.Tests.Core;

/// <summary>A request that dies on the wire (the tunnel accepted it, the link behind was gone) drops the forward;
/// a GET is sent once more through the fresh one, anything else is reported as it failed.</summary>
public class ReconnectHandlerTests
{
    private sealed class Flaky(int failures) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (Calls <= failures) throw new HttpRequestException("An error occurred while sending the request.", new IOException());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static (HttpClient http, Flaky inner, Func<int> dropped) Build(int failures)
    {
        int dropped = 0;
        var inner = new Flaky(failures);
        var http = new HttpClient(new AdminApi.ReconnectHandler(inner, () => dropped++)) { BaseAddress = new Uri("http://127.0.0.1") };
        return (http, inner, () => dropped);
    }

    [Fact]
    public async Task A_get_that_dies_on_the_wire_is_sent_again_through_a_fresh_forward()
    {
        var (http, inner, dropped) = Build(failures: 1);
        using var resp = await http.GetAsync("/admin/devices");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(2, inner.Calls);
        Assert.Equal(1, dropped());
    }

    [Fact]
    public async Task A_get_that_dies_twice_fails()
    {
        var (http, inner, dropped) = Build(failures: 2);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("/admin/devices"));
        Assert.Equal(2, inner.Calls);
        Assert.Equal(1, dropped());   // the retry's own failure is reported as it is
    }

    [Fact]
    public async Task A_post_is_not_repeated_but_the_forward_is_dropped()
    {
        var (http, inner, dropped) = Build(failures: 1);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.PostAsync("/admin/devices/x/commands", new StringContent("{}")));
        Assert.Equal(1, inner.Calls);
        Assert.Equal(1, dropped());
    }

    [Fact]
    public async Task A_healthy_request_touches_nothing()
    {
        var (http, inner, dropped) = Build(failures: 0);
        using var resp = await http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, inner.Calls);
        Assert.Equal(0, dropped());
    }
}
