using Whisper.Abstractions;

namespace Microsoft.Extensions.DependencyInjection;
public static class IServiceCollectionExtensions
{
    public static IServiceCollection AddWhisper(this IServiceCollection services, Action<IWhisperBuilder> configure)
    {
        configure?.Invoke(new WhisperBuilder(services));
        return services;
    }

    private sealed record WhisperBuilder(IServiceCollection Services) : IWhisperBuilder;
}