using Microsoft.Extensions.Logging;

namespace Whisper.Outbox;

/// <summary>
/// Applies <see cref="OutboxRecoverabilityOptions"/>: decides whether an exception is unrecoverable
/// and when the next retry attempt becomes due.
/// </summary>
internal sealed class RecoverabilityPolicy(OutboxRecoverabilityOptions options, ILogger logger)
{
    public bool IsUnrecoverable(Exception exception)
    {
        return MatchesUnrecoverableType(exception) || MatchesUnrecoverablePredicate(exception);
    }

    /// <summary>
    /// The moment the given 1-based failed attempt becomes due, or null for eligible-at-next-poll
    /// (no delay configured, the configured delay is non-positive, or the delay function threw).
    /// </summary>
    public DateTimeOffset? NextRetryAt(int attemptOrdinal, DateTimeOffset nowUtc)
    {
        if (options.RetryDelay is null)
            return null;

        TimeSpan delay;
        try
        {
            delay = options.RetryDelay(attemptOrdinal);
        }
        catch (Exception delayException)
        {
            logger.LogError(delayException,
                "The RetryDelay function threw for attempt {AttemptOrdinal}. Falling back to a retry at the next poll.",
                attemptOrdinal);
            return null;
        }

        return delay > TimeSpan.Zero ? nowUtc + delay : null;
    }

    private bool MatchesUnrecoverableType(Exception exception)
        => options.UnrecoverableExceptionTypes.Any(type => type.IsInstanceOfType(exception));

    private bool MatchesUnrecoverablePredicate(Exception exception)
        => options.UnrecoverableExceptionPredicates.Any(predicate => MatchesSafely(predicate, exception));

    private bool MatchesSafely(Func<Exception, bool> predicate, Exception exception)
    {
        try
        {
            return predicate(exception);
        }
        catch (Exception predicateException)
        {
            logger.LogError(predicateException,
                "An unrecoverable-exception predicate threw while evaluating an exception of type {ExceptionType}. Treating it as recoverable.",
                exception.GetType());
            return false;
        }
    }
}
