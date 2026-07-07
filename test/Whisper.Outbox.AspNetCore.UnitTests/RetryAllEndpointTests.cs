using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;

namespace Whisper.Outbox.AspNetCore.UnitTests;

public class RetryAllEndpointTests
{
    [Fact]
    public async Task RetryAll_ReturnsOkWithRetriedCountFromStore()
    {
        var store = DashboardTestHost.CreateStore();
        store.RetryAll(Arg.Any<CancellationToken>()).Returns(5_000_000_000L);
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().PostAsync("/whisper/outbox/api/failed/retry-all", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("retried").GetInt64().Should().Be(5_000_000_000L, "retried is a long on the wire");
    }
}
