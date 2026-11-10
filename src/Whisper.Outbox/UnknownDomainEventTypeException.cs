namespace Whisper.Outbox;

/// <summary>
/// Thrown when the type an outbox record names cannot be loaded by the running application: the type was renamed or
/// removed, or the record was written by another version of the application that shares the outbox. The worker books
/// it as a failed attempt and retries, because another version may know the type. Add it to
/// <see cref="OutboxRecoverabilityOptions.UnrecoverableExceptionTypes"/> to fail such records on the first attempt.
/// </summary>
public sealed class UnknownDomainEventTypeException(string typeName, Exception innerException) : Exception(
    $"The domain event type '{typeName}' cannot be loaded by this application. " +
    "It was renamed or removed, or the record was written by another version of the application.",
    innerException)
{
    /// <summary>The type name as stored on the outbox record.</summary>
    public string TypeName { get; } = typeName;
}
