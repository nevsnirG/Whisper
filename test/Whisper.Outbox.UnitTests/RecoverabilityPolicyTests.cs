using Microsoft.Extensions.Logging;

namespace Whisper.Outbox.UnitTests;

public class RecoverabilityPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 10, 0, 0, TimeSpan.Zero);

    private readonly OutboxRecoverabilityOptions _options = new();
    private readonly ILogger _logger = Substitute.For<ILogger>();

    private RecoverabilityPolicy CreateSut() => new(_options, _logger);

    [Fact]
    public void IsUnrecoverable_NoTypesOrPredicatesConfigured_ReturnsFalse()
    {
        var result = CreateSut().IsUnrecoverable(new Exception("boom"));

        result.Should().BeFalse();
    }

    [Fact]
    public void IsUnrecoverable_ExactRegisteredType_ReturnsTrue()
    {
        _options.UnrecoverableExceptionTypes.Add(typeof(InvalidOperationException));

        var result = CreateSut().IsUnrecoverable(new InvalidOperationException());

        result.Should().BeTrue();
    }

    [Fact]
    public void IsUnrecoverable_TypeDerivedFromRegisteredType_ReturnsTrue()
    {
        _options.UnrecoverableExceptionTypes.Add(typeof(ArgumentException));

        // ArgumentNullException derives from ArgumentException
        var result = CreateSut().IsUnrecoverable(new ArgumentNullException());

        result.Should().BeTrue();
    }

    [Fact]
    public void IsUnrecoverable_UnregisteredType_ReturnsFalse()
    {
        _options.UnrecoverableExceptionTypes.Add(typeof(ArgumentException));

        var result = CreateSut().IsUnrecoverable(new InvalidOperationException());

        result.Should().BeFalse();
    }

    [Fact]
    public void IsUnrecoverable_BaseTypeOfRegisteredType_ReturnsFalse()
    {
        // Registration matches the type and anything derived from it - never the other way around.
        _options.UnrecoverableExceptionTypes.Add(typeof(ArgumentNullException));

        var result = CreateSut().IsUnrecoverable(new ArgumentException());

        result.Should().BeFalse();
    }

    [Fact]
    public void IsUnrecoverable_MatchingPredicate_ReturnsTrue()
    {
        _options.UnrecoverableExceptionPredicates.Add(ex => ex.Message.Contains("poison"));

        var result = CreateSut().IsUnrecoverable(new Exception("poison message"));

        result.Should().BeTrue();
    }

    [Fact]
    public void IsUnrecoverable_NonMatchingPredicate_ReturnsFalse()
    {
        _options.UnrecoverableExceptionPredicates.Add(ex => ex.Message.Contains("poison"));

        var result = CreateSut().IsUnrecoverable(new Exception("transient hiccup"));

        result.Should().BeFalse();
    }

    [Fact]
    public void IsUnrecoverable_ThrowingPredicate_IsTreatedAsRecoverable()
    {
        _options.UnrecoverableExceptionPredicates.Add(_ => throw new InvalidOperationException("predicate blew up"));

        var result = CreateSut().IsUnrecoverable(new Exception("original failure"));

        result.Should().BeFalse();
    }

    [Fact]
    public void IsUnrecoverable_ThrowingPredicate_LogsThePredicateExceptionAsError()
    {
        var predicateException = new InvalidOperationException("predicate blew up");
        _options.UnrecoverableExceptionPredicates.Add(_ => throw predicateException);

        CreateSut().IsUnrecoverable(new Exception("original failure"));

        var logCall = _logger.ReceivedCalls().Should().ContainSingle().Subject;
        logCall.GetMethodInfo().Name.Should().Be(nameof(ILogger.Log));
        logCall.GetArguments()[0].Should().Be(LogLevel.Error);
        logCall.GetArguments()[3].Should().BeSameAs(predicateException);
    }

    [Fact]
    public void IsUnrecoverable_ThrowingPredicateFollowedByMatchingPredicate_ReturnsTrue()
    {
        _options.UnrecoverableExceptionPredicates.Add(_ => throw new InvalidOperationException("predicate blew up"));
        _options.UnrecoverableExceptionPredicates.Add(_ => true);

        var result = CreateSut().IsUnrecoverable(new Exception("original failure"));

        result.Should().BeTrue("a throwing predicate must not stop later predicates from being evaluated");
    }

    [Fact]
    public void NextRetryAt_NullRetryDelay_ReturnsNull()
    {
        _options.RetryDelay = null;

        var result = CreateSut().NextRetryAt(1, Now);

        result.Should().BeNull();
    }

    [Fact]
    public void NextRetryAt_PositiveDelay_ReturnsNowPlusDelay()
    {
        _options.RetryDelay = _ => TimeSpan.FromMinutes(5);

        var result = CreateSut().NextRetryAt(1, Now);

        result.Should().Be(Now + TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void NextRetryAt_ZeroDelay_ReturnsNull()
    {
        _options.RetryDelay = _ => TimeSpan.Zero;

        var result = CreateSut().NextRetryAt(1, Now);

        result.Should().BeNull();
    }

    [Fact]
    public void NextRetryAt_NegativeDelay_ReturnsNull()
    {
        _options.RetryDelay = _ => TimeSpan.FromSeconds(-1);

        var result = CreateSut().NextRetryAt(1, Now);

        result.Should().BeNull();
    }

    [Fact]
    public void NextRetryAt_PassesTheAttemptOrdinalToRetryDelay()
    {
        var observedAttempts = new List<int>();
        _options.RetryDelay = attempt =>
        {
            observedAttempts.Add(attempt);
            return TimeSpan.FromMinutes(attempt);
        };

        var result = CreateSut().NextRetryAt(3, Now);

        observedAttempts.Should().Equal(3);
        result.Should().Be(Now + TimeSpan.FromMinutes(3));
    }
}
