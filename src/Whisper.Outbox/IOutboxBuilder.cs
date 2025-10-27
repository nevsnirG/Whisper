using Microsoft.Extensions.DependencyInjection;

namespace Whisper.Outbox;
public interface IOutboxBuilder
{
    IServiceCollection Services { get; }
}