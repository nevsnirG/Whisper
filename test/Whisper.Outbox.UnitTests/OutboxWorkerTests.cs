using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Whisper.Abstractions;
using Whisper.Outbox.Abstractions;

namespace Whisper.Outbox.UnitTests;

public class OutboxWorkerTests
{
    private static readonly DateTimeOffset StartTime = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task WhenOutboxEmpty_DoesNotDispatchAnything()
    {
        using var harness = new WorkerHarness();
        harness.ReturnBatchOnceThenCancel();

        await harness.RunToCompletion();

        await harness.OutboxStore.Received(1).ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().SetDispatchedAt(default!, default, default);
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().ScheduleRetry(default!, default!, default, default);
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().SetFailedAt(default!, default!, default);
    }

    [Fact]
    public async Task ReadNextBatch_ReceivesBatchSizeAndCurrentUtcTimeAsDueAt()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.BatchSize = 25;
        harness.ReturnBatchOnceThenCancel();

        await harness.RunToCompletion();

        await harness.OutboxStore.Received(1).ReadNextBatch(25, StartTime, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenOutboxNotEmpty_DispatchesRecords_AndMarksAsDispatched()
    {
        using var harness = new WorkerHarness();
        var outboxRecord = CreateRecord();
        var domainEvent = new TestEvent();
        harness.Serializer.Deserialize("SomePayload", "SomeType").Returns(domainEvent);
        harness.ReturnBatchOnceThenCancel(outboxRecord);

        await harness.RunToCompletion();

        await harness.Dispatcher.Received(1).Dispatch(domainEvent, Arg.Any<CancellationToken>());
        await harness.OutboxStore.Received(1).SetDispatchedAt(outboxRecord, StartTime, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenDeserializationFails_MarksRecordAsFailedWithError_AndContinuesWithNextRecord()
    {
        using var harness = new WorkerHarness();
        var badRecord = CreateRecord(payload: "BadPayload", type: "BadType");
        var goodRecord = CreateRecord(payload: "GoodPayload", type: "GoodType");
        var deserializationException = new InvalidOperationException("Bad type");
        harness.Serializer.When(s => s.Deserialize("BadPayload", "BadType"))
            .Do(_ => throw deserializationException);
        harness.Serializer.Deserialize("GoodPayload", "GoodType").Returns(new TestEvent());
        harness.ReturnBatchOnceThenCancel(badRecord, goodRecord);
        var failures = harness.CaptureSetFailedAtCalls();

        await harness.RunToCompletion();

        var (failedRecord, failure) = failures.Should().ContainSingle().Subject;
        failedRecord.Should().BeSameAs(badRecord);
        failure.Error.Should().Be(deserializationException.ToString());
        failure.OccurredAtUtc.Should().Be(StartTime);
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().ScheduleRetry(default!, default!, default, default);
        await harness.OutboxStore.Received(1).SetDispatchedAt(goodRecord, StartTime, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenDispatchFails_AndRetriesNotExhausted_SchedulesRetryWithFailureDetails()
    {
        using var harness = new WorkerHarness();
        var outboxRecord = CreateRecord(retries: 0);
        var dispatchException = new Exception("Downstream service unavailable");
        harness.SetUpFailingDispatch(outboxRecord, dispatchException);
        var retries = harness.CaptureScheduleRetryCalls();

        await harness.RunToCompletion();

        var (retriedRecord, failure, nextRetryAtUtc) = retries.Should().ContainSingle().Subject;
        retriedRecord.Should().BeSameAs(outboxRecord);
        failure.Error.Should().Be(dispatchException.ToString());
        failure.OccurredAtUtc.Should().Be(StartTime);
        nextRetryAtUtc.Should().BeNull("no RetryDelay is configured, so the record is eligible at the next poll");
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().SetFailedAt(default!, default!, default);
    }

    [Fact]
    public async Task WhenDispatchFails_WithRetryDelay_SchedulesRetryUsingOneBasedAttemptOrdinal()
    {
        using var harness = new WorkerHarness();
        var observedAttempts = new List<int>();
        harness.RecoverabilityOptions.MaxRetries = 5;
        harness.RecoverabilityOptions.RetryDelay = attempt =>
        {
            observedAttempts.Add(attempt);
            return TimeSpan.FromMinutes(attempt);
        };
        // One retry already recorded: this dispatch is the second failed attempt.
        var outboxRecord = CreateRecord(retries: 1);
        harness.SetUpFailingDispatch(outboxRecord, new Exception("Still down"));
        var retries = harness.CaptureScheduleRetryCalls();

        await harness.RunToCompletion();

        observedAttempts.Should().Equal(2);
        retries.Should().ContainSingle()
            .Which.NextRetryAtUtc.Should().Be(StartTime + TimeSpan.FromMinutes(2));
    }

    [Fact]
    public async Task WhenDispatchFails_AndRetriesExhausted_MarksAsFailedWithFailureDetails()
    {
        using var harness = new WorkerHarness();
        harness.RecoverabilityOptions.MaxRetries = 3;
        var outboxRecord = CreateRecord(retries: 2); // Third total attempt: 2 + 1 >= 3
        var dispatchException = new Exception("Downstream still down");
        harness.SetUpFailingDispatch(outboxRecord, dispatchException);
        var failures = harness.CaptureSetFailedAtCalls();

        await harness.RunToCompletion();

        var (failedRecord, failure) = failures.Should().ContainSingle().Subject;
        failedRecord.Should().BeSameAs(outboxRecord);
        failure.Error.Should().Be(dispatchException.ToString());
        failure.OccurredAtUtc.Should().Be(StartTime);
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().ScheduleRetry(default!, default!, default, default);
    }

    [Fact]
    public async Task WhenDispatchFails_WithUnrecoverableExceptionType_MarksAsFailedOnFirstAttempt()
    {
        using var harness = new WorkerHarness();
        harness.RecoverabilityOptions.MaxRetries = 3;
        harness.RecoverabilityOptions.UnrecoverableExceptionTypes.Add(typeof(InvalidOperationException));
        var outboxRecord = CreateRecord(retries: 0);
        var dispatchException = new InvalidOperationException("Poison message");
        harness.SetUpFailingDispatch(outboxRecord, dispatchException);
        var failures = harness.CaptureSetFailedAtCalls();

        await harness.RunToCompletion();

        var (failedRecord, failure) = failures.Should().ContainSingle().Subject;
        failedRecord.Should().BeSameAs(outboxRecord);
        failure.Error.Should().Be(dispatchException.ToString());
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().ScheduleRetry(default!, default!, default, default);
    }

    [Fact]
    public async Task WhenDispatchFails_ErrorLongerThanMaxErrorLength_IsTruncated()
    {
        using var harness = new WorkerHarness();
        var outboxRecord = CreateRecord(retries: 0);
        var dispatchException = new Exception(new string('x', OutboxWorker.MaxErrorLength));
        harness.SetUpFailingDispatch(outboxRecord, dispatchException);
        var retries = harness.CaptureScheduleRetryCalls();

        await harness.RunToCompletion();

        dispatchException.ToString().Length.Should().BeGreaterThan(OutboxWorker.MaxErrorLength);
        retries.Should().ContainSingle()
            .Which.Failure.Error.Should().Be(dispatchException.ToString()[..OutboxWorker.MaxErrorLength]);
    }

    [Fact]
    public async Task WhenReadNextBatchThrows_LogsErrorAndContinues()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.PollingIntervalMs = 1;
        var callCount = 0;
        harness.OutboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                    throw new Exception("DB connection failed");
                harness.Cts.Cancel();
                return Array.Empty<OutboxRecord>();
            });

        await harness.RunToCompletion();

        await harness.OutboxStore.Received(2).ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenUsePollingTimeProviderEnabled_AdvancingFakeTimeTriggersNextPoll_WithoutRealWaiting()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.UsePollingTimeProvider = true;
        harness.WorkerOptions.PollingIntervalMs = 300_000;
        var callCount = 0;
        harness.OutboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 2)
                    harness.Cts.Cancel();
                return Array.Empty<OutboxRecord>();
            });

        var runTask = harness.RunToCompletion();
        while (!runTask.IsCompleted)
        {
            harness.TimeProvider.Advance(TimeSpan.FromMilliseconds(300_000));
            await Task.Delay(10);
        }
        await runTask;

        await harness.OutboxStore.Received(2).ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenUsePollingTimeProviderDisabled_FakeTimeProviderDoesNotGatePolling()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.PollingIntervalMs = 1;
        var callCount = 0;
        harness.OutboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 2)
                    harness.Cts.Cancel();
                return Array.Empty<OutboxRecord>();
            });

        await harness.RunToCompletion();

        await harness.OutboxStore.Received(2).ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    private static OutboxRecord CreateRecord(int retries = 0, string payload = "SomePayload", string type = "SomeType") => new()
    {
        Id = Guid.NewGuid(),
        AssemblyQualifiedType = type,
        EnqueuedAtUtc = StartTime,
        Retries = retries,
        Payload = payload,
    };

    private sealed class WorkerHarness : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private ServiceProvider? _serviceProvider;

        public IOutboxStore OutboxStore { get; } = Substitute.For<IOutboxStore>();
        public IDomainEventSerializer Serializer { get; } = Substitute.For<IDomainEventSerializer>();
        public IDispatchDomainEvents Dispatcher { get; } = Substitute.For<IDispatchDomainEvents>();
        public FakeTimeProvider TimeProvider { get; } = new(StartTime);
        public ILogger<OutboxWorker> Logger { get; } = Substitute.For<ILogger<OutboxWorker>>();
        public OutboxWorkerOptions WorkerOptions { get; } = new();
        public OutboxRecoverabilityOptions RecoverabilityOptions { get; } = new();

        public CancellationTokenSource Cts => _cts;

        public void ReturnBatchOnceThenCancel(params OutboxRecord[] records)
        {
            OutboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(records)
                .AndDoes(_ => _cts.Cancel());
        }

        public void SetUpFailingDispatch(OutboxRecord outboxRecord, Exception dispatchException)
        {
            Serializer.Deserialize(outboxRecord.Payload, outboxRecord.AssemblyQualifiedType).Returns(new TestEvent());
            Dispatcher.When(d => d.Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>()))
                .Do(_ => throw dispatchException);
            ReturnBatchOnceThenCancel(outboxRecord);
        }

        public List<(OutboxRecord Record, OutboxFailure Failure, DateTimeOffset? NextRetryAtUtc)> CaptureScheduleRetryCalls()
        {
            var calls = new List<(OutboxRecord, OutboxFailure, DateTimeOffset?)>();
            OutboxStore.When(s => s.ScheduleRetry(Arg.Any<OutboxRecord>(), Arg.Any<OutboxFailure>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()))
                .Do(ci => calls.Add((ci.ArgAt<OutboxRecord>(0), ci.ArgAt<OutboxFailure>(1), ci.ArgAt<DateTimeOffset?>(2))));
            return calls;
        }

        public List<(OutboxRecord Record, OutboxFailure Failure)> CaptureSetFailedAtCalls()
        {
            var calls = new List<(OutboxRecord, OutboxFailure)>();
            OutboxStore.When(s => s.SetFailedAt(Arg.Any<OutboxRecord>(), Arg.Any<OutboxFailure>(), Arg.Any<CancellationToken>()))
                .Do(ci => calls.Add((ci.ArgAt<OutboxRecord>(0), ci.ArgAt<OutboxFailure>(1))));
            return calls;
        }

        public async Task RunToCompletion()
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => OutboxStore);
            services.AddKeyedScoped(ServiceKeys.InnerDispatcher, (_, _) => Dispatcher);
            _serviceProvider = services.BuildServiceProvider();

            var awaiter = new OutboxInstallerAwaiter();
            awaiter.SignalCompletion();

            using var worker = new OutboxWorker(
                Serializer,
                TimeProvider,
                awaiter,
                Options.Create(WorkerOptions),
                Options.Create(RecoverabilityOptions),
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                Logger);

            await worker.StartAsync(_cts.Token);
            await worker.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            await worker.StopAsync(CancellationToken.None);
        }

        public void Dispose()
        {
            _serviceProvider?.Dispose();
            _cts.Dispose();
        }
    }

    private record TestEvent() : IDomainEvent;
}
