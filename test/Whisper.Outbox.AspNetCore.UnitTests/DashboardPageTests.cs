using System.Net;
using Microsoft.AspNetCore.Builder;
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

    // The page has buttons that retry and delete records, so another site must not be able to frame it.
    [Fact]
    public async Task DashboardPage_ForbidsFramingByOtherSites()
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().GetAsync("/whisper/outbox");

        response.Headers.GetValues("X-Frame-Options").Should().Equal("SAMEORIGIN");
        response.Headers.GetValues("Content-Security-Policy").Should().Equal("frame-ancestors 'self'");
    }

    [Fact]
    public async Task DashboardPage_HostAlreadySetTheFramingHeaders_KeepsTheHostsValues()
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store, configurePipeline: pipeline =>
            pipeline.Use(async (context, next) =>
            {
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'self'";
                await next(context);
            }));

        var response = await app.GetTestClient().GetAsync("/whisper/outbox");

        response.Headers.GetValues("X-Frame-Options").Should().Equal("DENY");
        response.Headers.GetValues("Content-Security-Policy").Should().Equal("default-src 'self'");
    }

    // The page and the API agree on the header by name only, so this keeps the two from drifting apart.
    [Fact]
    public async Task DashboardPage_SendsTheCommandHeaderTheApiRequires()
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var html = await app.GetTestClient().GetStringAsync("/whisper/outbox");

        html.Should().Contain($"'{WhisperOutboxDashboardOptions.CommandHeader}'");
    }
}
