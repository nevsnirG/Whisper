using System.Net;
using Microsoft.AspNetCore.TestHost;

namespace Whisper.Outbox.AspNetCore.UnitTests;

/// <summary>
/// The endpoints that change something only accept requests with the command header. A browser cannot add a custom
/// header to a request another site makes it send, so a page on another site cannot retry or delete records on
/// behalf of someone the browser authenticates on its own (Windows authentication, SameSite=None cookies).
/// </summary>
public class CommandHeaderTests
{
    private const string RecordId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    [Theory]
    [InlineData("POST", "/whisper/outbox/api/records/" + RecordId + "/retry")]
    [InlineData("POST", "/whisper/outbox/api/failed/retry-all")]
    [InlineData("DELETE", "/whisper/outbox/api/records/" + RecordId)]
    public async Task Command_WithoutTheHeader_IsRejectedBeforeItReachesTheStore(string method, string path)
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(WhisperOutboxDashboardOptions.CommandHeader);
        store.ReceivedCalls().Should().BeEmpty();
    }

    [Theory]
    [InlineData("POST", "/whisper/outbox/api/records/" + RecordId + "/retry")]
    [InlineData("POST", "/whisper/outbox/api/failed/retry-all")]
    [InlineData("DELETE", "/whisper/outbox/api/records/" + RecordId)]
    public async Task Command_WithTheHeader_ReachesTheStore(string method, string path)
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetDashboardClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
        store.ReceivedCalls().Should().ContainSingle();
    }

    [Theory]
    [InlineData("POST", "/whisper/outbox/api/records/" + RecordId + "/retry")]
    [InlineData("POST", "/whisper/outbox/api/failed/retry-all")]
    [InlineData("DELETE", "/whisper/outbox/api/records/" + RecordId)]
    public async Task Command_WithoutTheHeader_WhenTheRequirementIsSwitchedOff_ReachesTheStore(string method, string path)
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(
            store,
            configureDashboard: options => options.RequireCommandHeader = false);

        var response = await app.GetTestClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
        store.ReceivedCalls().Should().ContainSingle();
    }

    // Reading changes nothing, so it stays a plain GET that works from an address bar or curl.
    [Theory]
    [InlineData("/whisper/outbox")]
    [InlineData("/whisper/outbox/api/failed")]
    [InlineData("/whisper/outbox/api/records/" + RecordId)]
    public async Task Read_WithoutTheHeader_IsNotRejected(string path)
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().GetAsync(path);

        response.StatusCode.Should().NotBe(HttpStatusCode.BadRequest);
    }

    // The header check must not answer before authorization has: an anonymous caller learns nothing from it.
    [Theory]
    [InlineData("POST", "/whisper/outbox/api/failed/retry-all")]
    [InlineData("DELETE", "/whisper/outbox/api/records/" + RecordId)]
    public async Task Command_WithoutTheHeader_Unauthenticated_ReturnsUnauthorized(string method, string path)
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartSecuredHost(store, RejectingAuthenticationHandler.Register);

        var response = await app.GetTestClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
