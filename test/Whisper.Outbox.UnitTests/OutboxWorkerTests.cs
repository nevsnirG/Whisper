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
    public async Task WhenStoppingDuringBatch_MarksDispatchedRecordWithNonCancelledToken()
    {
        using var harness = new WorkerHarness();
        var outboxRecord = CreateRecord();
        harness.Serializer.Deserialize("SomePayload", "SomeType").Returns(new TestEvent());
        // Cancels the stopping token as soon as the batch is read, so the dispatch completes during shutdown.
        harness.ReturnBatchOnceThenCancel(outboxRecord);

        await harness.RunToCompletion();

        harness.Cts.IsCancellationRequested.Should().BeTrue();
        await harness.OutboxStore.Received(1).SetDispatchedAt(outboxRecord, StartTime,
            Arg.Is<CancellationToken>(t => !t.IsCancellationRequested));
    }

    [Fact]
    public async Task WhenMarkingDispatchedFails_BooksNoFailedAttempt_AndEndsTheBatch()
    {
        using var harness = new WorkerHarness();
        var firstRecord = CreateRecord(payload: "FirstPayload");
        var secondRecord = CreateRecord(payload: "SecondPayload");
        harness.RecoverabilityOptions.MaxRetries = 1; // a booked failure would fail the record permanently
        harness.Serializer.Deserialize(Arg.Any<string>(), Arg.Any<string>()).Returns(new TestEvent());
        harness.OutboxStore.SetDispatchedAt(firstRecord, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new TimeoutException("store unavailable")));
        harness.ReturnBatchOnceThenCancel(firstRecord, secondRecord);

        await harness.RunToCompletion();

        await harness.Dispatcher.Received(1).Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().ScheduleRetry(default!, default!, default, default);
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().SetFailedAt(default!, default!, default);
        await harness.OutboxStore.DidNotReceive().SetDispatchedAt(secondRecord, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
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
    public async Task WhenAFullBatchDispatchesCleanly_ReadsTheNextBatchWithoutWaiting()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.BatchSize = 2;
        // The fake clock never moves, so a wait between the polls would never end.
        harness.WorkerOptions.PollingIntervalMs = 300_000;
        harness.Serializer.Deserialize(Arg.Any<string>(), Arg.Any<string>()).Returns(new TestEvent());
        var reads = 0;
        harness.OutboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++reads == 1)
                    return [CreateRecord(), CreateRecord()];
                harness.Cts.Cancel();
                return Array.Empty<OutboxRecord>();
            });

        await harness.RunToCompletion();

        reads.Should().Be(2);
        await harness.Dispatcher.Received(2).Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenABatchIsNotFull_WaitsBeforeTheNextPoll()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.BatchSize = 2;
        harness.WorkerOptions.PollingIntervalMs = 300_000;
        harness.Serializer.Deserialize(Arg.Any<string>(), Arg.Any<string>()).Returns(new TestEvent());
        var record = CreateRecord();
        var dispatched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OutboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([record]);
        harness.OutboxStore.When(s => s.SetDispatchedAt(record, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>()))
            .Do(_ => dispatched.TrySetResult());

        await harness.StartAsync();
        await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        await harness.StopAsync();

        await harness.OutboxStore.Received(1).ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    // Retries with no RetryDelay are due "at the next poll". Reading on without a wait would burn through every
    // attempt of a failing record within milliseconds.
    [Fact]
    public async Task WhenAFullBatchHasAFailure_WaitsBeforeTheNextPoll()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.BatchSize = 1;
        harness.WorkerOptions.PollingIntervalMs = 300_000;
        var record = CreateRecord();
        harness.Serializer.Deserialize(Arg.Any<string>(), Arg.Any<string>()).Returns(new TestEvent());
        harness.Dispatcher.When(d => d.Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new Exception("Downstream service unavailable"));
        var retryScheduled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.OutboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([record]);
        harness.OutboxStore.When(s => s.ScheduleRetry(record, Arg.Any<OutboxFailure>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()))
            .Do(_ => retryScheduled.TrySetResult());

        await harness.StartAsync();
        await retryScheduled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(200);
        await harness.StopAsync();

        await harness.OutboxStore.Received(1).ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenStoppingCancelsADispatch_BooksNoFailedAttempt_AndLogsNoFailure()
    {
        using var harness = new WorkerHarness();
        harness.RecoverabilityOptions.MaxRetries = 1; // a booked failure would fail the record permanently
        harness.Serializer.Deserialize(Arg.Any<string>(), Arg.Any<string>()).Returns(new TestEvent());
        harness.Dispatcher.Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromCanceled(call.Arg<CancellationToken>()));
        // Cancels the stopping token as soon as the batch is read, so the dispatch is cancelled by the shutdown.
        harness.ReturnBatchOnceThenCancel(CreateRecord());

        await harness.RunToCompletion();

        await harness.OutboxStore.DidNotReceiveWithAnyArgs().ScheduleRetry(default!, default!, default, default);
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().SetFailedAt(default!, default!, default);
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().SetDispatchedAt(default!, default, default);
        harness.LoggedLevels().Should().NotContain(level => level >= LogLevel.Warning);
    }

    // A cancellation that does not come from the shutdown (a timeout inside a handler) is an ordinary failed attempt.
    [Fact]
    public async Task WhenADispatchIsCancelledWithoutAShutdown_SchedulesRetry()
    {
        using var harness = new WorkerHarness();
        var outboxRecord = CreateRecord();
        harness.Serializer.Deserialize(outboxRecord.Payload, outboxRecord.AssemblyQualifiedType).Returns(new TestEvent());
        harness.Dispatcher.When(d => d.Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>()))
            .Do(_ => throw new TaskCanceledException("The handler timed out."));
        var retries = harness.CaptureScheduleRetryCalls();
        harness.OutboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns([outboxRecord]);
        // The stop is only requested once the failure has been handled.
        harness.OutboxStore.When(s => s.ScheduleRetry(outboxRecord, Arg.Any<OutboxFailure>(), Arg.Any<DateTimeOffset?>(), Arg.Any<CancellationToken>()))
            .Do(_ => harness.Cts.Cancel());

        await harness.RunToCompletion();

        retries.Should().ContainSingle();
    }

    // Another version of the application sharing the outbox may know the type (a rolling deployment, a rollback).
    [Fact]
    public async Task WhenTheEventTypeIsUnknown_SchedulesRetryInsteadOfFailingTheRecord()
    {
        using var harness = new WorkerHarness();
        var outboxRecord = CreateRecord(type: "Some.Renamed.Event, Some.Assembly");
        var unknownType = new UnknownDomainEventTypeException(outboxRecord.AssemblyQualifiedType, new TypeLoadException());
        harness.Serializer.When(s => s.Deserialize(outboxRecord.Payload, outboxRecord.AssemblyQualifiedType))
            .Do(_ => throw unknownType);
        harness.ReturnBatchOnceThenCancel(outboxRecord);
        var retries = harness.CaptureScheduleRetryCalls();

        await harness.RunToCompletion();

        retries.Should().ContainSingle()
            .Which.Failure.Error.Should().Be(unknownType.ToString());
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().SetFailedAt(default!, default!, default);
        await harness.Dispatcher.DidNotReceiveWithAnyArgs().Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    // A worker runs no use case, so an event a handler raises has no scope to be collected in. It is rejected where
    // it is raised, and since every retry would fail the same way the record fails on the first attempt.
    [Fact]
    public async Task WhenAHandlerRaisesADomainEvent_FailsTheRecordOnTheFirstAttempt()
    {
        using var harness = new WorkerHarness();
        harness.RecoverabilityOptions.MaxRetries = 3;
        var outboxRecord = CreateRecord();
        harness.Serializer.Deserialize(outboxRecord.Payload, outboxRecord.AssemblyQualifiedType).Returns(new TestEvent());
        harness.Dispatcher.Dispatch(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                Whispers.About(new TestEvent());
                return Task.CompletedTask;
            });
        harness.ReturnBatchOnceThenCancel(outboxRecord);
        var failures = harness.CaptureSetFailedAtCalls();

        await harness.RunToCompletion();

        failures.Should().ContainSingle()
            .Which.Failure.Error.Should().Contain(nameof(DomainEventRaisedDuringDispatchException));
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().ScheduleRetry(default!, default!, default, default);
    }

    [Fact]
    public async Task WhenAnotherInstanceHoldsTheWorkerLock_DoesNotReadTheOutbox()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.UsePollingTimeProvider = false;
        harness.WorkerOptions.PollingIntervalMs = 1;
        var attempts = 0;
        harness.WorkerLock = Substitute.For<IOutboxWorkerLock>();
        harness.WorkerLock.TryAcquire(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (++attempts == 3)
                    harness.Cts.Cancel();
                return Task.FromResult<IAsyncDisposable?>(null);
            });

        await harness.RunToCompletion();

        attempts.Should().Be(3, "the worker tries again at every poll");
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().ReadNextBatch(default, default, default);
    }

    [Fact]
    public async Task WhenTheWorkerLockIsFree_HoldsItForTheBatch_AndReleasesItAfterwards()
    {
        using var harness = new WorkerHarness();
        var outboxRecord = CreateRecord();
        harness.Serializer.Deserialize(outboxRecord.Payload, outboxRecord.AssemblyQualifiedType).Returns(new TestEvent());
        var held = Substitute.For<IAsyncDisposable>();
        harness.WorkerLock = Substitute.For<IOutboxWorkerLock>();
        harness.WorkerLock.TryAcquire(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IAsyncDisposable?>(held));
        harness.ReturnBatchOnceThenCancel(outboxRecord);

        await harness.RunToCompletion();

        Received.InOrder(() =>
        {
            harness.WorkerLock.TryAcquire(Arg.Any<CancellationToken>());
            harness.OutboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
            harness.OutboxStore.SetDispatchedAt(outboxRecord, Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
            held.DisposeAsync();
        });
    }

    [Fact]
    public async Task WhenExclusivePollingIsOff_PollsWithoutTheWorkerLock()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.ExclusivePolling = false;
        harness.WorkerLock = Substitute.For<IOutboxWorkerLock>();
        harness.ReturnBatchOnceThenCancel();

        await harness.RunToCompletion();

        await harness.WorkerLock.DidNotReceiveWithAnyArgs().TryAcquire(default);
        await harness.OutboxStore.Received(1).ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenDisabled_NeverReadsTheOutbox_AndDoesNotWaitForTheInstaller()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.Enabled = false;
        harness.StorageIsInstalled = false; // an enabled worker would wait here until it is stopped

        await harness.RunToCompletion();

        harness.Cts.IsCancellationRequested.Should().BeFalse("the worker ended on its own");
        await harness.OutboxStore.DidNotReceiveWithAnyArgs().ReadNextBatch(default, default, default);
    }

    [Fact]
    public async Task WhenReadNextBatchThrows_LogsErrorAndContinues()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.PollingIntervalMs = 1;
        harness.WorkerOptions.UsePollingTimeProvider = false;
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
        harness.WorkerOptions.UsePollingTimeProvider = false;
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

    [Fact]
    public async Task WhenRetentionNotSet_NeverDeletesDispatchedRecords()
    {
        using var harness = new WorkerHarness();
        harness.PollThenCancel(polls: 3, advancePerPoll: TimeSpan.FromMinutes(2));

        await harness.RunToCompletion();

        await harness.OutboxStore.DidNotReceiveWithAnyArgs().DeleteDispatchedBefore(default, default, default);
    }

    [Fact]
    public async Task WhenRetentionSet_DeletesRecordsDispatchedBeforeNowMinusRetention()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.RetainDispatchedFor = TimeSpan.FromDays(7);
        harness.PollThenCancel(polls: 2);

        await harness.RunToCompletion();

        await harness.OutboxStore.Received(1).DeleteDispatchedBefore(
            StartTime - TimeSpan.FromDays(7), OutboxWorker.RetentionBatchSize, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenRetentionDeletesFullBatches_ContinuesUntilABatchIsNotFull()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.RetainDispatchedFor = TimeSpan.FromDays(7);
        harness.OutboxStore.DeleteDispatchedBefore(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(OutboxWorker.RetentionBatchSize, OutboxWorker.RetentionBatchSize, 3);
        harness.PollThenCancel(polls: 2);

        await harness.RunToCompletion();

        await harness.OutboxStore.Received(3).DeleteDispatchedBefore(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenRetentionKeepsDeletingFullBatches_StopsAtTheLimitPerRun()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.RetainDispatchedFor = TimeSpan.FromDays(7);
        harness.OutboxStore.DeleteDispatchedBefore(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(OutboxWorker.RetentionBatchSize);
        harness.PollThenCancel(polls: 2);

        await harness.RunToCompletion();

        await harness.OutboxStore.Received(OutboxWorker.MaxRetentionBatchesPerRun)
            .DeleteDispatchedBefore(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenPollsComeFasterThanTheRetentionInterval_CleansUpOncePerInterval()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.RetainDispatchedFor = TimeSpan.FromDays(7);
        harness.PollThenCancel(polls: 4); // the fake clock does not move between polls

        await harness.RunToCompletion();

        await harness.OutboxStore.Received(1).DeleteDispatchedBefore(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenStoreDoesNotSupportRetention_StopsTrying()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.RetainDispatchedFor = TimeSpan.FromDays(7);
        harness.OutboxStore.DeleteDispatchedBefore(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new NotSupportedException());
        harness.PollThenCancel(polls: 4, advancePerPoll: TimeSpan.FromMinutes(2));

        await harness.RunToCompletion();

        await harness.OutboxStore.Received(1).DeleteDispatchedBefore(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WhenRetentionFails_TriesAgainAtTheNextCleanup_AndKeepsPolling()
    {
        using var harness = new WorkerHarness();
        harness.WorkerOptions.RetainDispatchedFor = TimeSpan.FromDays(7);
        harness.OutboxStore.DeleteDispatchedBefore(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<int>(new TimeoutException("store unavailable")));
        harness.PollThenCancel(polls: 3, advancePerPoll: TimeSpan.FromMinutes(2));

        await harness.RunToCompletion();

        await harness.OutboxStore.Received(3).ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>());
        // Cleanup runs after the polls that did not stop the worker.
        await harness.OutboxStore.Received(2).DeleteDispatchedBefore(Arg.Any<DateTimeOffset>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
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
        private OutboxWorker? _worker;

        public IOutboxWorkerLock? WorkerLock { get; set; }
        public bool StorageIsInstalled { get; set; } = true;

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

        // Polls an empty outbox `polls` times in real time, moving the fake clock by `advancePerPoll` on each read,
        // and stops the worker during the last read.
        public void PollThenCancel(int polls, TimeSpan advancePerPoll = default)
        {
            WorkerOptions.UsePollingTimeProvider = false;
            WorkerOptions.PollingIntervalMs = 1;
            var reads = 0;
            OutboxStore.ReadNextBatch(Arg.Any<int>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
                .Returns(_ =>
                {
                    TimeProvider.Advance(advancePerPoll);
                    if (++reads == polls)
                        _cts.Cancel();
                    return Array.Empty<OutboxRecord>();
                });
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

        public async Task StartAsync()
        {
            var services = new ServiceCollection();
            services.AddScoped(_ => OutboxStore);
            services.AddKeyedScoped(ServiceKeys.InnerDispatcher, (_, _) => Dispatcher);
            _serviceProvider = services.BuildServiceProvider();

            var awaiter = new OutboxInstallerAwaiter();
            if (StorageIsInstalled)
                awaiter.SignalCompletion();

            _worker = new OutboxWorker(
                Serializer,
                TimeProvider,
                awaiter,
                Options.Create(WorkerOptions),
                Options.Create(RecoverabilityOptions),
                _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
                Logger,
                WorkerLock);

            await _worker.StartAsync(_cts.Token);
        }

        public async Task RunToCompletion()
        {
            await StartAsync();
            await _worker!.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            await _worker.StopAsync(CancellationToken.None);
        }

        public async Task StopAsync()
        {
            _cts.Cancel();
            await _worker!.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
            await _worker.StopAsync(CancellationToken.None);
        }

        // The levels of everything the worker logged, in order.
        public LogLevel[] LoggedLevels()
        {
            return [.. Logger.ReceivedCalls()
                .Where(call => call.GetMethodInfo().Name == nameof(ILogger.Log))
                .Select(call => (LogLevel)call.GetArguments()[0]!)];
        }

        public void Dispose()
        {
            _worker?.Dispose();
            _serviceProvider?.Dispose();
            _cts.Dispose();
        }
    }

    private record TestEvent() : IDomainEvent;
}
