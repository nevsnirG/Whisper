using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Whisper.Outbox.Abstractions;
using Whisper.Outbox.AspNetCore;

namespace Microsoft.AspNetCore.Builder;

public static class IEndpointRouteBuilderExtensions
{
    private const int MinPage = 1;
    private const int MinPageSize = 1;
    private const int MaxPageSize = 200;

    private static readonly Lazy<string> DashboardHtml = new(ReadEmbeddedDashboardHtml);

    // The dashboard's JavaScript hard-codes camelCase property names, so the wire contract is pinned
    // here instead of relying on the host's global JsonOptions: a host with a different naming policy
    // or a source-generated resolver without metadata for this package's internal DTOs would
    // otherwise silently break or fail the API.
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Maps the outbox management dashboard (a self-contained embedded HTML page) and its JSON API
    /// under <paramref name="pattern"/>. Every endpoint requires authorization unless
    /// <see cref="WhisperOutboxDashboardOptions.AllowAnonymous"/> is set; every endpoint carries
    /// <see cref="WhisperOutboxEndpointMetadata"/> so hosts can recognize dashboard routes.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Thrown at map time when no <see cref="IOutboxManagementStore"/> is registered.
    /// </exception>
    public static IEndpointConventionBuilder MapWhisperOutbox(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/whisper/outbox",
        Action<WhisperOutboxDashboardOptions>? configure = null)
    {
        ThrowIfManagementStoreIsNotRegistered(endpoints);

        var options = new WhisperOutboxDashboardOptions();
        configure?.Invoke(options);

        var group = endpoints.MapGroup(pattern);
        group.WithMetadata(new WhisperOutboxEndpointMetadata());
        ApplyAuthorizationPosture(group, options);

        MapDashboardPage(group);
        MapManagementApi(group);

        return group;
    }

    private static void ThrowIfManagementStoreIsNotRegistered(IEndpointRouteBuilder endpoints)
    {
        var serviceProbe = endpoints.ServiceProvider.GetService<IServiceProviderIsService>();
        if (serviceProbe is null || serviceProbe.IsService(typeof(IOutboxManagementStore)))
            return;

        throw new InvalidOperationException(
            $"No {nameof(IOutboxManagementStore)} is registered, so MapWhisperOutbox has no storage backend to manage. " +
            "Register an outbox storage backend with AddWhisper(b => b.AddOutbox(ob => ob.AddSqlServer(...))) " +
            "or AddWhisper(b => b.AddOutbox(ob => ob.AddMongoDb(...))).");
    }

    private static void ApplyAuthorizationPosture(RouteGroupBuilder group, WhisperOutboxDashboardOptions options)
    {
        if (options.AllowAnonymous)
            group.AllowAnonymous();
        else
            group.RequireAuthorization();
    }

    private static void MapDashboardPage(RouteGroupBuilder group)
    {
        group.MapGet("/", () => Results.Text(DashboardHtml.Value, "text/html", Encoding.UTF8));
    }

    private static void MapManagementApi(RouteGroupBuilder group)
    {
        group.MapGet("/api/failed", GetFailed);
        group.MapGet("/api/records/{id:guid}", GetRecord);
        group.MapPost("/api/records/{id:guid}/retry", RetryRecord);
        group.MapPost("/api/failed/retry-all", RetryAll);
        group.MapDelete("/api/records/{id:guid}", DeleteRecord);
    }

    private static async Task<IResult> GetFailed(
        IOutboxManagementStore store,
        CancellationToken cancellationToken,
        int page = 1,
        int pageSize = 50)
    {
        page = Math.Max(MinPage, page);
        pageSize = Math.Clamp(pageSize, MinPageSize, MaxPageSize);

        var failedPage = await store.GetFailed(page, pageSize, cancellationToken);
        return Results.Json(FailedRecordsPageResponse.From(failedPage, page, pageSize), WireJson);
    }

    private static async Task<IResult> GetRecord(Guid id, IOutboxManagementStore store, CancellationToken cancellationToken)
    {
        var record = await store.Get(id, cancellationToken);
        return record is null ? Results.NotFound() : Results.Json(OutboxRecordResponse.From(record), WireJson);
    }

    private static async Task<IResult> RetryRecord(Guid id, IOutboxManagementStore store, CancellationToken cancellationToken)
    {
        return await store.Retry(id, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> RetryAll(IOutboxManagementStore store, CancellationToken cancellationToken)
    {
        var retried = await store.RetryAll(cancellationToken);
        return Results.Json(new RetryAllResponse(retried), WireJson);
    }

    private static async Task<IResult> DeleteRecord(Guid id, IOutboxManagementStore store, CancellationToken cancellationToken)
    {
        return await store.Delete(id, cancellationToken) ? Results.NoContent() : Results.NotFound();
    }

    private static string ReadEmbeddedDashboardHtml()
    {
        const string resourceName = "Whisper.Outbox.AspNetCore.Dashboard.html";
        var assembly = typeof(WhisperOutboxEndpointMetadata).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
