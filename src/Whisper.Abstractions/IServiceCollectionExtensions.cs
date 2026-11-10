using Whisper.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;
public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddWhisper(this IServiceCollection services, Action<IWhisperBuilder> configure)
    {
        configure?.Invoke(new WhisperBuilder(services));

        // Lets packages finish their registrations now that every builder method has run, whatever their order.
        foreach (var registered in WhisperConfiguredCallback.RegisteredIn(services))
            registered.Callback(services);

        return services;
    }

    private sealed record WhisperBuilder(IServiceCollection Services) : IWhisperBuilder;
}
