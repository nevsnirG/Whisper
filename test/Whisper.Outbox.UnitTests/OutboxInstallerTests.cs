using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.UnitTests;
public class OutboxInstallerTests
{
    private readonly IInstallOutbox _outboxInstaller = Substitute.For<IInstallOutbox>();
    private readonly OutboxInstallerAwaiter _awaiter = new();
    private readonly ILogger<OutboxInstaller> _logger = Substitute.For<ILogger<OutboxInstaller>>();
    private readonly OutboxWorkerOptions _workerOptions = new();

    [Fact]
    public async Task StartAsync_CallsInstallerAndSignalsCompletion()
    {
        var sut = CreateSut(b =>
        {
            b.Services.AddSingleton(Substitute.For<IDispatchDomainEvents>());
            b.AddOutbox(_ => { });
        });

        await sut.StartAsync(CancellationToken.None);

        await _outboxInstaller.Received(1).InstallCollection(CancellationToken.None);
        _awaiter.IsReady.Should().BeTrue();
        LoggedLevels().Should().NotContain(level => level >= LogLevel.Warning);
    }

    // Without a dispatcher behind the outbox, the worker would mark every record as dispatched without delivering it.
    [Fact]
    public async Task StartAsync_WithoutADispatcherBehindTheOutbox_FailsBeforeInstalling()
    {
        var sut = CreateSut(b => b.AddOutbox(_ => { }));

        var act = () => sut.StartAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*no dispatcher*AddMediatR*");
        await _outboxInstaller.DidNotReceiveWithAnyArgs().InstallCollection(default);
        _awaiter.IsReady.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_WithoutADispatcherBehindTheOutbox_WorkerDisabled_Installs()
    {
        _workerOptions.Enabled = false;
        var sut = CreateSut(b => b.AddOutbox(_ => { }));

        await sut.StartAsync(CancellationToken.None);

        await _outboxInstaller.Received(1).InstallCollection(CancellationToken.None);
    }

    // A dispatcher registered after AddWhisper cannot be moved behind the outbox any more. That may be on purpose,
    // so it is reported rather than rejected.
    [Fact]
    public async Task StartAsync_DispatcherRegisteredAfterAddWhisper_LogsAWarning()
    {
        var sut = CreateSut(
            b =>
            {
                b.Services.AddSingleton(Substitute.For<IDispatchDomainEvents>());
                b.AddOutbox(_ => { });
            },
            afterAddWhisper: services => services.AddScoped<IDispatchDomainEvents, LateDispatcher>());

        await sut.StartAsync(CancellationToken.None);

        LoggedLevels().Should().ContainSingle(level => level == LogLevel.Warning);
        await _outboxInstaller.Received(1).InstallCollection(CancellationToken.None);
    }

    private OutboxInstaller CreateSut(Action<IWhisperBuilder> configure, Action<IServiceCollection>? afterAddWhisper = null)
    {
        var services = new ServiceCollection();
        services.AddWhisper(configure);
        afterAddWhisper?.Invoke(services);

        return new OutboxInstaller(_outboxInstaller, _awaiter, new OutboxDispatcherRegistrations(services), Options.Create(_workerOptions), _logger);
    }

    private LogLevel[] LoggedLevels()
    {
        return [.. _logger.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
            .Select(call => (LogLevel)call.GetArguments()[0]!)];
    }

    private sealed class LateDispatcher : IDispatchDomainEvents
    {
        public Task Dispatch(IDomainEvent domainEvent, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task Dispatch(IDomainEvent[] domainEvents, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
