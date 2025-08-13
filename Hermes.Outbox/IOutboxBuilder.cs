using Microsoft.Extensions.DependencyInjection;

namespace Hermes.Outbox;
public interface IOutboxBuilder
{
    IServiceCollection Services { get; }
}