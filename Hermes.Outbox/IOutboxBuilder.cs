namespace Microsoft.Extensions.DependencyInjection;
public interface IOutboxBuilder
{
    IServiceCollection Services { get; }
}