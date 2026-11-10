using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;

namespace Whisper.Abstractions;

/// <summary>Infrastructure for packages that plug into <see cref="IWhisperBuilder"/>.</summary>
public static class WhisperBuilderExtensions
{
    /// <summary>
    /// Registers a callback that runs every time an <c>AddWhisper</c> call has finished its configure action.
    /// A package uses it to finish registrations that depend on what the other builder methods registered, so the
    /// order those methods are called in does not matter. The callback can run more than once (once per
    /// <c>AddWhisper</c> call), so it must be safe to repeat. Registering the same delegate again is a no-op.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static IWhisperBuilder OnConfigured(this IWhisperBuilder builder, Action<IServiceCollection> callback)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(callback);

        if (!WhisperConfiguredCallback.RegisteredIn(builder.Services).Any(registered => registered.Callback == callback))
            builder.Services.AddSingleton(new WhisperConfiguredCallback(callback));

        return builder;
    }
}

/// <summary>
/// Carries an <see cref="WhisperBuilderExtensions.OnConfigured"/> callback in the service collection, which is the only
/// state the builder methods of different packages share.
/// </summary>
internal sealed class WhisperConfiguredCallback(Action<IServiceCollection> callback)
{
    public Action<IServiceCollection> Callback { get; } = callback;

    public static WhisperConfiguredCallback[] RegisteredIn(IServiceCollection services)
    {
        return [.. services
            .Where(descriptor => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(WhisperConfiguredCallback))
            .Select(descriptor => (WhisperConfiguredCallback)descriptor.ImplementationInstance!)];
    }
}
