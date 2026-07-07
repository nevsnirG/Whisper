using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.AspNetCore.UnitTests;

public class RecordEndpointTests
{
    private static readonly Guid RecordId = Guid.Parse("5c0a9c9e-8d5f-4b0a-9c3d-2b8f5a1e7d64");

    [Fact]
    public async Task GetRecord_ExistingRecord_ReturnsOkWithPayloadAndLastError()
    {
        var store = DashboardTestHost.CreateStore();
        store.Get(RecordId, Arg.Any<CancellationToken>()).Returns(new OutboxRecord
        {
            Id = RecordId,
            EnqueuedAtUtc = new DateTimeOffset(2026, 7, 1, 8, 30, 0, TimeSpan.Zero),
            FailedAtUtc = new DateTimeOffset(2026, 7, 2, 9, 45, 0, TimeSpan.Zero),
            Retries = 5,
            AssemblyQualifiedType = "My.Event, My.Assembly",
            Payload = /*lang=json*/ """{"amount":42}""",
            LastError = "System.InvalidOperationException: boom",
            LastErrorAtUtc = new DateTimeOffset(2026, 7, 2, 9, 45, 0, TimeSpan.Zero),
        });
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().GetAsync($"/whisper/outbox/api/records/{RecordId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("id").GetGuid().Should().Be(RecordId);
        root.GetProperty("payload").GetString().Should().Be("""{"amount":42}""", "the detail endpoint exposes the payload");
        root.GetProperty("lastError").GetString().Should().Be("System.InvalidOperationException: boom");
    }

    [Fact]
    public async Task GetRecord_UnknownRecord_ReturnsNotFound()
    {
        var store = DashboardTestHost.CreateStore();
        store.Get(RecordId, Arg.Any<CancellationToken>()).Returns((OutboxRecord?)null);
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().GetAsync($"/whisper/outbox/api/records/{RecordId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetRecord_MalformedId_ReturnsNotFoundWithoutCallingStore()
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().GetAsync("/whisper/outbox/api/records/not-a-guid");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the guid route constraint rejects malformed ids");
        await store.DidNotReceiveWithAnyArgs().Get(default, default);
    }

    [Fact]
    public async Task RetryRecord_RetryableRecord_ReturnsNoContent()
    {
        var store = DashboardTestHost.CreateStore();
        store.Retry(RecordId, Arg.Any<CancellationToken>()).Returns(true);
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().PostAsync($"/whisper/outbox/api/records/{RecordId}/retry", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await store.Received(1).Retry(RecordId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetryRecord_MissingOrNotFailedRecord_ReturnsNotFound()
    {
        var store = DashboardTestHost.CreateStore();
        store.Retry(RecordId, Arg.Any<CancellationToken>()).Returns(false);
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().PostAsync($"/whisper/outbox/api/records/{RecordId}/retry", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task DeleteRecord_FailedRecord_ReturnsNoContent()
    {
        var store = DashboardTestHost.CreateStore();
        store.Delete(RecordId, Arg.Any<CancellationToken>()).Returns(true);
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().DeleteAsync($"/whisper/outbox/api/records/{RecordId}");

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        await store.Received(1).Delete(RecordId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteRecord_MissingOrNotFailedRecord_ReturnsNotFound()
    {
        var store = DashboardTestHost.CreateStore();
        store.Delete(RecordId, Arg.Any<CancellationToken>()).Returns(false);
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().DeleteAsync($"/whisper/outbox/api/records/{RecordId}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
