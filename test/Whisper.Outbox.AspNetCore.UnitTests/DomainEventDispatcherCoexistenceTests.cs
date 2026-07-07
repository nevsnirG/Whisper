using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Whisper.Abstractions;

namespace Whisper.Outbox.AspNetCore.UnitTests;

public class DomainEventDispatcherCoexistenceTests
{
    [Fact]
    public async Task ApiRequest_ThroughDomainEventDispatcherMiddleware_ReturnsOkAndDispatchesNoEvents()
    {
        var store = DashboardTestHost.CreateStore();
        var dispatcher = Substitute.For<IDispatchDomainEvents>();
        await using var app = await DashboardTestHost.StartAnonymousHost(
            store,
            configureServices: services => services.AddSingleton(dispatcher),
            configurePipeline: pipeline => pipeline.UseDomainEventDispatcherMiddleware());

        var response = await app.GetTestClient().GetAsync("/whisper/outbox/api/failed");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        dispatcher.ReceivedCalls().Should().BeEmpty("dashboard endpoints must never raise domain events");
    }
}
