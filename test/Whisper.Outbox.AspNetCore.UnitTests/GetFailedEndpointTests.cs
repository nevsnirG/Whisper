using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.AspNetCore.UnitTests;

public class GetFailedEndpointTests
{
    [Fact]
    public async Task GetFailed_WithFailedRecords_ReturnsCamelCasePageShapeWithoutPayload()
    {
        var summaryId = Guid.Parse("0f8fad5b-d9cb-469f-a165-70867728950e");
        var enqueuedAt = new DateTimeOffset(2026, 7, 1, 8, 30, 0, TimeSpan.Zero);
        var failedAt = new DateTimeOffset(2026, 7, 2, 9, 45, 0, TimeSpan.Zero);
        var store = DashboardTestHost.CreateStore();
        store.GetFailed(1, 50, Arg.Any<CancellationToken>()).Returns(new OutboxFailedPage(
            [new OutboxRecordSummary(summaryId, enqueuedAt, failedAt, 3, "My.Event, My.Assembly", "System.InvalidOperationException: boom", failedAt)],
            5_000_000_000L));
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().GetAsync("/whisper/outbox/api/failed");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        root.GetProperty("totalCount").GetInt64().Should().Be(5_000_000_000L, "totalCount is a long on the wire");
        root.GetProperty("page").GetInt32().Should().Be(1);
        root.GetProperty("pageSize").GetInt32().Should().Be(50);
        var record = root.GetProperty("records")[0];
        record.GetProperty("id").GetGuid().Should().Be(summaryId);
        record.GetProperty("enqueuedAtUtc").GetDateTimeOffset().Should().Be(enqueuedAt);
        record.GetProperty("failedAtUtc").GetDateTimeOffset().Should().Be(failedAt);
        record.GetProperty("retries").GetInt32().Should().Be(3);
        record.GetProperty("assemblyQualifiedType").GetString().Should().Be("My.Event, My.Assembly");
        record.GetProperty("lastError").GetString().Should().Be("System.InvalidOperationException: boom");
        record.GetProperty("lastErrorAtUtc").GetDateTimeOffset().Should().Be(failedAt);
        record.TryGetProperty("payload", out _).Should().BeFalse("failed-record summaries must never expose the payload");
    }

    [Theory]
    [InlineData("", 1, 50)]                      // no query -> handler defaults
    [InlineData("?page=0&pageSize=999", 1, 200)] // clamped up to MinPage, down to MaxPageSize
    [InlineData("?page=-5&pageSize=0", 1, 1)]    // clamped up to both minimums
    [InlineData("?page=3&pageSize=200", 3, 200)] // in-range values pass through unchanged
    public async Task GetFailed_PagingValues_AreClampedBeforeCallingStoreAndEchoedInResponse(
        string query, int expectedPage, int expectedPageSize)
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().GetAsync($"/whisper/outbox/api/failed{query}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await store.Received(1).GetFailed(expectedPage, expectedPageSize, Arg.Any<CancellationToken>());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        document.RootElement.GetProperty("page").GetInt32().Should().Be(expectedPage);
        document.RootElement.GetProperty("pageSize").GetInt32().Should().Be(expectedPageSize);
    }

    [Fact]
    public async Task GetFailed_NonNumericPagingValues_ReturnsBadRequestWithoutCallingStore()
    {
        var store = DashboardTestHost.CreateStore();
        await using var app = await DashboardTestHost.StartAnonymousHost(store);

        var response = await app.GetTestClient().GetAsync("/whisper/outbox/api/failed?page=abc&pageSize=xyz");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        store.ReceivedCalls().Should().BeEmpty();
    }
}
