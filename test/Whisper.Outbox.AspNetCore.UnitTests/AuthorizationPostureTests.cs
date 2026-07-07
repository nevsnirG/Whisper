using System.Net;
using Microsoft.AspNetCore.TestHost;

namespace Whisper.Outbox.AspNetCore.UnitTests;

public class AuthorizationPostureTests
{
    private const string RecordId = "0f8fad5b-d9cb-469f-a165-70867728950e";

    [Theory]
    [InlineData("GET", "/whisper/outbox")]
    [InlineData("GET", "/whisper/outbox/api/failed")]
    [InlineData("GET", "/whisper/outbox/api/records/" + RecordId)]
    [InlineData("POST", "/whisper/outbox/api/records/" + RecordId + "/retry")]
    [InlineData("POST", "/whisper/outbox/api/failed/retry-all")]
    [InlineData("DELETE", "/whisper/outbox/api/records/" + RecordId)]
    public async Task EveryEndpoint_DefaultPostureWithRejectingAuthentication_ReturnsUnauthorized(string method, string path)
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartSecuredHost(store, RejectingAuthenticationHandler.Register);

        var response = await app.GetTestClient().SendAsync(new HttpRequestMessage(new HttpMethod(method), path));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        store.ReceivedCalls().Should().BeEmpty("unauthenticated requests must never reach the management store");
    }

    [Fact]
    public async Task DefaultPosture_WithoutAuthorizationServices_FailsRequestWithInvalidOperationException()
    {
        // Intended fail-safe: RequireAuthorization metadata without authorization middleware makes
        // ASP.NET Core throw at request time instead of silently serving payloads unauthenticated.
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartSecuredHost(store);
        var client = app.GetTestClient();

        var act = () => client.GetAsync("/whisper/outbox/api/failed");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*authorization*");
    }

    [Fact]
    public async Task AllowAnonymous_WithoutAuthenticationServices_AllowsAccess()
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().GetAsync("/whisper/outbox/api/failed");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task AllowAnonymous_WithRejectingAuthentication_StillAllowsAccess()
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(
            store,
            configureServices: RejectingAuthenticationHandler.Register);

        var response = await app.GetTestClient().GetAsync("/whisper/outbox/api/failed");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "AllowAnonymous must override the authorization requirement");
    }
}
