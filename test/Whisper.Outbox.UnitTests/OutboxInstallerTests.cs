using Microsoft.Extensions.Logging;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.UnitTests;
public class OutboxInstallerTests
{
    [Fact]
    public async Task StartAsync_CallsInstallerAndSignalsCompletion()
    {
        var outboxInstaller = Substitute.For<IInstallOutbox>();
        var awaiter = new OutboxInstallerAwaiter();
        var logger = Substitute.For<ILogger<OutboxInstaller>>();
        var sut = new OutboxInstaller(outboxInstaller, awaiter, logger);

        await sut.StartAsync(CancellationToken.None);

        await outboxInstaller.Received(1).InstallCollection(CancellationToken.None);
        awaiter.IsReady.Should().BeTrue();
    }
}
