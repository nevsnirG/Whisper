using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.AspNetCore.UnitTests;

/// <summary>
/// Builds minimal in-memory hosts (WebApplication + TestServer) around a substitute
/// <see cref="IOutboxManagementStore"/> for exercising the endpoints mapped by MapWhisperOutbox.
/// </summary>
internal static class DashboardTestHost
{
    /// <summary>Starts a host with <c>AllowAnonymous = true</c> so functional tests need no authentication services.</summary>
    internal static Task<WebApplication> StartAnonymousHost(
        IOutboxManagementStore store,
        string pattern = "/whisper/outbox",
        Action<IServiceCollection>? configureServices = null,
        Action<WebApplication>? configurePipeline = null)
    {
        return StartHost(store, pattern, options => options.AllowAnonymous = true, configureServices, configurePipeline);
    }

    /// <summary>Starts a host with the default secure-by-default posture (every endpoint requires authorization).</summary>
    internal static Task<WebApplication> StartSecuredHost(
        IOutboxManagementStore store,
        Action<IServiceCollection>? configureServices = null)
    {
        return StartHost(store, "/whisper/outbox", configure: null, configureServices, configurePipeline: null);
    }

    /// <summary>
    /// A TestServer-backed builder pinned to the Production environment for determinism: no developer
    /// exception page swallowing pipeline exceptions into 500s, and no ThrowOnBadRequest turning
    /// query-binding failures into exceptions instead of 400s.
    /// </summary>
    internal static WebApplicationBuilder CreateBuilder()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        return builder;
    }

    /// <summary>
    /// A substitute store whose GetFailed returns an empty page by default; NSubstitute cannot
    /// auto-fabricate the sealed <see cref="OutboxFailedPage"/> and would otherwise return null.
    /// </summary>
    internal static IOutboxManagementStore CreateStore()
    {
        var store = Substitute.For<IOutboxManagementStore>();
        store.GetFailed(0, 0, CancellationToken.None).ReturnsForAnyArgs(new OutboxFailedPage([], 0));
        return store;
    }

    private static async Task<WebApplication> StartHost(
        IOutboxManagementStore store,
        string pattern,
        Action<WhisperOutboxDashboardOptions>? configure,
        Action<IServiceCollection>? configureServices,
        Action<WebApplication>? configurePipeline)
    {
        var builder = CreateBuilder();
        builder.Services.AddSingleton(store);
        configureServices?.Invoke(builder.Services);

        var app = builder.Build();
        configurePipeline?.Invoke(app);
        app.MapWhisperOutbox(pattern, configure);

        await app.StartAsync();
        return app;
    }
}

/// <summary>
/// An authentication scheme that authenticates nobody, so the default authorization policy
/// (authenticated user required) always challenges with 401.
/// </summary>
internal sealed class RejectingAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    internal const string SchemeName = "TestReject";

    internal static void Register(IServiceCollection services)
    {
        services
            .AddAuthentication(SchemeName)
            .AddScheme<AuthenticationSchemeOptions, RejectingAuthenticationHandler>(SchemeName, configureOptions: null);
        services.AddAuthorization();
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        return Task.FromResult(AuthenticateResult.NoResult());
    }
}
