using MediatR;

namespace Whisper.Outbox.IntegrationTests.Fakes;

/// <summary>
/// Fails every dispatch with a deterministic exception until <see cref="StopFailing"/> is called.
/// </summary>
public sealed class ToggleFailingDomainEventHandler : INotificationHandler<TestDomainEvent>
{
    public const string FailureMessage = "Simulated handler failure";

    private volatile bool _fail = true;
    private readonly TaskCompletionSource _firstSuccess = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void StopFailing() => _fail = false;

    public Task WaitForFirstSuccess(TimeSpan timeout) => _firstSuccess.Task.WaitAsync(timeout);

    public Task Handle(TestDomainEvent notification, CancellationToken cancellationToken)
    {
        if (_fail)
            throw new InvalidOperationException(FailureMessage);

        _firstSuccess.TrySetResult();
        return Task.CompletedTask;
    }
}
