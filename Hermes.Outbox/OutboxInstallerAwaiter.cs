namespace Hermes.Outbox;
internal sealed class OutboxInstallerAwaiter
{
    private readonly TaskCompletionSource<bool> _tcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsReady => _tcs.Task.IsCompleted;

    public Task WaitForCompletion(CancellationToken cancellationToken)
        => _tcs.Task.WaitAsync(cancellationToken);

    public void SignalCompletion()
        => _tcs.TrySetResult(true);
}