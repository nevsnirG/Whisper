using Microsoft.Extensions.DependencyInjection;
using Whisper.Abstractions;

namespace Whisper.Outbox;

/// <summary>
/// Keeps the two sets of <see cref="IDispatchDomainEvents"/> registrations apart: the request path dispatches to the
/// outbox only, and the worker dispatches to everything else (the inner dispatchers, which are re-registered under
/// <see cref="ServiceKeys.InnerDispatcher"/>).
/// </summary>
internal sealed class OutboxDispatcherRegistrations(IServiceCollection services)
{
    private static readonly Func<IServiceProvider, IDispatchDomainEvents> RequestPathDispatcher = static sp =>
    {
        var awaiter = sp.GetRequiredService<OutboxInstallerAwaiter>();
        var outboxDispatcher = sp.GetRequiredService<OutboxDispatcher>();

        return awaiter.IsReady
            ? outboxDispatcher
            : new BlockingOutboxDispatcher(awaiter, outboxDispatcher);
    };

    /// <summary>Registers the outbox as a request-path dispatcher, once however often it is called.</summary>
    public static void AddOutboxDispatcher(IServiceCollection services)
    {
        if (!services.Any(IsOutboxDispatcher))
            services.AddScoped(RequestPathDispatcher);
    }

    /// <summary>
    /// Re-registers every request-path dispatcher except the outbox itself as an inner dispatcher. Safe to repeat:
    /// it only touches what is not moved yet. Keyed registrations (the host's own, or dispatchers moved earlier) are
    /// not part of the request path, so they are left as they are.
    /// </summary>
    public static void MoveDispatchersBehindTheOutbox(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(IsRequestPathDispatcherBesideTheOutbox).ToArray())
        {
            services.Remove(descriptor);
            AddAsInnerDispatcher(services, descriptor);
        }
    }

    /// <summary>The dispatchers the worker delivers stored events to.</summary>
    public string[] InnerDispatchers()
    {
        return [.. services
            .Where(d => d.IsKeyedService && d.ServiceType == typeof(IDispatchDomainEvents) && Equals(d.ServiceKey, ServiceKeys.InnerDispatcher))
            .Select(d => NameOf(d.KeyedImplementationType, d.KeyedImplementationInstance))];
    }

    /// <summary>
    /// Dispatchers that ended up in the request path next to the outbox, because they were registered after the last
    /// <c>AddWhisper</c> call finished. The worker does not deliver to these.
    /// </summary>
    public string[] RequestPathDispatchersBesideTheOutbox()
    {
        return [.. services
            .Where(IsRequestPathDispatcherBesideTheOutbox)
            .Select(d => NameOf(d.ImplementationType, d.ImplementationInstance))];
    }

    private static bool IsRequestPathDispatcherBesideTheOutbox(ServiceDescriptor descriptor)
        => !descriptor.IsKeyedService && descriptor.ServiceType == typeof(IDispatchDomainEvents) && !IsOutboxDispatcher(descriptor);

    private static bool IsOutboxDispatcher(ServiceDescriptor descriptor)
        => !descriptor.IsKeyedService
            && descriptor.ServiceType == typeof(IDispatchDomainEvents)
            && ReferenceEquals(descriptor.ImplementationFactory, RequestPathDispatcher);

    private static void AddAsInnerDispatcher(IServiceCollection services, ServiceDescriptor descriptor)
    {
        if (descriptor.ImplementationType is not null)
        {
            services.Add(new(descriptor.ServiceType, ServiceKeys.InnerDispatcher, descriptor.ImplementationType, descriptor.Lifetime));
        }
        else if (descriptor.ImplementationFactory is { } factory)
        {
            services.Add(new(descriptor.ServiceType, ServiceKeys.InnerDispatcher, (sp, _) => factory(sp), descriptor.Lifetime));
        }
        else
        {
            services.Add(new(descriptor.ServiceType, ServiceKeys.InnerDispatcher, descriptor.ImplementationInstance!));
        }
    }

    private static string NameOf(Type? implementationType, object? implementationInstance)
        => implementationType?.Name ?? implementationInstance?.GetType().Name ?? "a factory registration";
}
