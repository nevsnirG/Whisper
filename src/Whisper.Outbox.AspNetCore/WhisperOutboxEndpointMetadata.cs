namespace Whisper.Outbox.AspNetCore;

/// <summary>
/// Marker metadata applied to every endpoint mapped by MapWhisperOutbox. Hosts can detect dashboard
/// routes via <c>context.GetEndpoint()?.Metadata.GetMetadata&lt;WhisperOutboxEndpointMetadata&gt;()</c>
/// to skip their unit-of-work middleware for these requests. This is an optimization only, never
/// required for correctness: the management store owns its storage access and is immune to ambient
/// transactions and pipeline ordering.
/// </summary>
public sealed class WhisperOutboxEndpointMetadata;
