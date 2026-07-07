using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.AspNetCore.UnitTests;

public class MapWhisperOutboxTests
{
    [Fact]
    public async Task MapWhisperOutbox_WithoutManagementStoreRegistered_ThrowsAtMapTimeWithAddOutboxGuidance()
    {
        var builder = DashboardTestHost.CreateBuilder();
        await using var app = builder.Build();

        var act = () => app.MapWhisperOutbox();

        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*{nameof(IOutboxManagementStore)}*")
            .WithMessage("*AddOutbox*");
    }

    [Fact]
    public async Task MapWhisperOutbox_MarksEveryEndpointWithWhisperOutboxEndpointMetadata()
    {
        await using var app = await DashboardTestHost.StartAnonymousHost(DashboardTestHost.CreateStore());

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .ToArray();

        endpoints.Should().HaveCount(6, "one dashboard page plus five API endpoints are mapped");
        endpoints.Should().OnlyContain(
            endpoint => endpoint.Metadata.GetMetadata<WhisperOutboxEndpointMetadata>() != null,
            "hosts detect dashboard routes through this marker metadata");
    }
}
