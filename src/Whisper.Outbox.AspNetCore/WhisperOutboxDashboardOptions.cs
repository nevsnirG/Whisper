namespace Whisper.Outbox.AspNetCore;

/// <summary>Options for the outbox management dashboard mapped by MapWhisperOutbox.</summary>
public sealed class WhisperOutboxDashboardOptions
{
    /// <summary>
    /// When false (the default) every dashboard endpoint requires authorization
    /// (<c>RequireAuthorization()</c>): a host without authentication/authorization configured gets an
    /// <see cref="InvalidOperationException"/> on the first request — an intended fail-safe, because the
    /// dashboard exposes event payloads and must never be reachable unauthenticated by accident.
    /// Set to true to map every endpoint with <c>AllowAnonymous()</c> instead.
    /// </summary>
    public bool AllowAnonymous { get; set; }
}
