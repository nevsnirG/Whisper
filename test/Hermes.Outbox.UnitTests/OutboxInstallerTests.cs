namespace Hermes.Outbox.UnitTests;
public class OutboxInstallerTests
{
    [Fact]
    public async Task StartAsync_CallsInstallerAndSignalsCompletion()
    {
        var outboxInstaller = Substitute.For<IInstallOutbox>();
        var awaiter = new OutboxInstallerAwaiter();
        var sut = new OutboxInstaller(outboxInstaller, awaiter);

        await sut.StartAsync(CancellationToken.None);

        await outboxInstaller.Received(1).InstallCollection(CancellationToken.None);
        awaiter.IsReady.Should().BeTrue();
    }
}
