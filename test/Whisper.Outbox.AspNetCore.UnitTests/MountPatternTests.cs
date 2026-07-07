using System.Net;
using Microsoft.AspNetCore.TestHost;

namespace Whisper.Outbox.AspNetCore.UnitTests;

public class MountPatternTests
{
    private static readonly Guid RecordId = Guid.Parse("5c0a9c9e-8d5f-4b0a-9c3d-2b8f5a1e7d64");

    [Fact]
    public async Task CustomMountPattern_ServesDashboardAndApiUnderCustomPrefix()
    {
        var store = DashboardTestHost.CreateStore();
        store.Retry(RecordId, Arg.Any<CancellationToken>()).Returns(true);
        await using var app = await DashboardTestHost.StartAnonymousHost(store, pattern: "/admin/outbox");
        var client = app.GetTestClient();

        var page = await client.GetAsync("/admin/outbox");
        var failed = await client.GetAsync("/admin/outbox/api/failed");
        var retry = await client.PostAsync($"/admin/outbox/api/records/{RecordId}/retry", content: null);

        page.StatusCode.Should().Be(HttpStatusCode.OK);
        failed.StatusCode.Should().Be(HttpStatusCode.OK);
        retry.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task CustomMountPattern_DoesNotServeDefaultPrefix()
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store, pattern: "/admin/outbox");

        var response = await app.GetTestClient().GetAsync("/whisper/outbox/api/failed");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
