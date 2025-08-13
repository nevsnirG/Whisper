using Microsoft.Extensions.DependencyInjection;

namespace Hermes.Abstractions;
public interface IHermesBuilder
{
    IServiceCollection Services { get; }
}