namespace Whisper.Outbox.AspNetCore;

/// <summary>Options for the outbox management dashboard mapped by MapWhisperOutbox.</summary>
public sealed class WhisperOutboxDashboardOptions
{
    /// <summary>
    /// The header a request to a command endpoint must carry while <see cref="RequireCommandHeader"/> is on.
    /// Any value will do.
    /// </summary>
    public const string CommandHeader = "X-Whisper-Outbox";

    /// <summary>
    /// When false (the default) every dashboard endpoint requires authorization
    /// (<c>RequireAuthorization()</c>): a host without authentication/authorization configured gets an
    /// <see cref="InvalidOperationException"/> on the first request — an intended fail-safe, because the
    /// dashboard exposes event payloads and must never be reachable unauthenticated by accident.
    /// Set to true to map every endpoint with <c>AllowAnonymous()</c> instead.
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>
    /// When true (the default), the endpoints that change something (retry, retry all, delete) answer 400 to a
    /// request without the <see cref="CommandHeader"/> header. A browser cannot add a custom header to a request
    /// another site makes it send, so this stops cross-site request forgery in setups where the browser
    /// authenticates on its own: Windows authentication, or cookies sent cross-site (<c>SameSite=None</c>).
    /// The dashboard page sends the header itself; other clients of the API have to add it.
    /// Set to false to accept commands without it.
    /// </summary>
    public bool RequireCommandHeader { get; set; } = true;
}
