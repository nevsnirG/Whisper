using Microsoft.Extensions.DependencyInjection;

namespace Whisper.Abstractions;
public interface IWhisperBuilder
{
    IServiceCollection Services { get; }
}