using System.Net;
using Microsoft.AspNetCore.TestHost;

namespace Whisper.Outbox.AspNetCore.UnitTests;

public class DashboardPageTests
{
    [Theory]
    [InlineData("/whisper/outbox")]
    [InlineData("/whisper/outbox/")]
    public async Task DashboardPage_ReturnsEmbeddedHtmlDocument(string path)
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        response.Content.Headers.ContentType.CharSet.Should().Be("utf-8");
        var html = await response.Content.ReadAsStringAsync();
        html.Should().Contain("<title>Whisper Outbox</title>", "the embedded Dashboard.html must be served at the group root");
    }
}
