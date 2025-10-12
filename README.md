# Hermes

Hermes is a minimal-impact **domain event tracking** library for .NET.  
It lets rich domain models raise domain events with **no pollution** — no `Events` property on aggregates, no plumbing in entities, and no domain-level awareness of dispatching or persistence.

Under the hood, Hermes uses `AsyncLocal<T>` to safely track domain events across both synchronous and asynchronous execution flows.  
Events raised during a logical operation (e.g., a MediatR request, an NServiceBus message handler, or an ASP.NET Core request) remain attached to that flow until they are dispatched or persisted by outer layers.

Hermes integrates cleanly with **DDD** and **Clean Architecture** by keeping the **domain pure** and moving event handling to the **application** and **infrastructure** layers.  
It also provides an optional **outbox** with **MongoDB** and **SQL Server** support, plus drop-in packages for **MediatR** and **NServiceBus**.

> 📖 Deep dive article: [Minimal-impact domain events](https://medium.com/@kenvgrinsven/minimal-impact-domain-events-313deb1af20e)

---

## Why Hermes?

Traditional domain event patterns often force you to:

- Add an `Events` collection to every aggregate and bubble those events up.
- Hand-roll async/thread context handling, or rely on brittle statics.
- Mix domain logic with dispatching, messaging, or persistence concerns.

**Hermes avoids all of that.**

- **Clean domain** — no `Events` list and no infrastructure references in your entities.
- **Async-safe tracking** — uses `AsyncLocal` so events flow across `await`s.
- **Infrastructure integration** — MediatR, NServiceBus, MongoDB, and SQL Server support.
- **Zero ceremony** — a single static call to raise an event from anywhere in your domain.

---

## Packages

| Package | Purpose |
| --- | --- |
| **Hermes.Core** | Core tracking logic (`DomainEventTracker`, `IDomainEvent`, scopes) |
| **Hermes.Abstractions** | Shared contracts (`IHermesBuilder`, `IDispatchDomainEvents`) |
| **Hermes.MediatR** | MediatR integration — **automatically** dispatches raised events after each request |
| **Hermes.Outbox** | Outbox infrastructure + background worker and installer |
| **Hermes.Outbox.MongoDb** | MongoDB outbox store + transaction participation via `IMongoSessionProvider` |
| **Hermes.Outbox.SqlServer** | SQL Server outbox store + transaction participation via `IConnectionLeaseProvider` |
| **Hermes.Outbox.MongoDb.NServiceBus** | Adapter to reuse the NServiceBus **Mongo** storage session |
| **Hermes.Outbox.SqlServer.NServiceBus** | Adapter to reuse the NServiceBus **SQL** storage session |

> Licensed under **GPL-3.0** (license file is already included in the repository).

---

## How it works (high level)

Hermes keeps a **per-execution “domain event scope”** in an `AsyncLocal<T>`:

1. In your **domain**, you raise events:
   ```csharp
   DomainEventTracker.RaiseDomainEvent(new OrderApproved(orderId));
   ```
2. Hermes attaches those events to the current async flow.
3. In your **application / infrastructure** layer, Hermes (or your configured integration) retrieves and dispatches/persists the collected events at the right time — e.g., after a MediatR pipeline completes or via the outbox worker.

Your domain never exposes an events collection and never learns about dispatching, messaging, or persistence.

---

## Core usage

### 1) Define a domain event

```csharp
using Hermes.Core;

public sealed record OrderApproved(Guid OrderId) : IDomainEvent;
```

### 2) Raise it from anywhere in the domain

```csharp
using Hermes.Core;

public class Order
{
    public Guid Id { get; }

    public void Approve()
    {
        // Domain logic...
        DomainEventTracker.RaiseDomainEvent(new OrderApproved(Id));
    }
}
```

### 3) Optional: scopes for isolation

If you want isolation per request/unit of work, create a scope. Events raised inside are collected and can be read/cleared as needed:

```csharp
using Hermes.Core;

using var scope = await DomainEventTracker.CreateScope();
// ... domain operations that raise events
var raised = DomainEventTracker.GetAndClearEvents(); // events for this scope (and children)
```

> Most users don’t need to manage scopes explicitly — integrations (like MediatR) take care of flushing at the right time.

---

## MediatR integration (automatic)

When you call `b.AddMediatR()` inside the Hermes builder, Hermes registers:
- An `IDispatchDomainEvents` implementation that publishes via `IMediator`.
- A **MediatR pipeline behavior** that **automatically retrieves and dispatches** all raised domain events after each request.

You do **not** need to write or register your own behavior.

```csharp
using Hermes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddMediatR(c => c.RegisterServicesFromAssemblyContaining<Program>())
    .AddHermes(b =>
    {
        b.AddMediatR(); // wires up dispatcher + automatic behavior
    });
```

---

## Outbox & transactions (optional)

Hermes provides an **outbox** for reliable, asynchronous dispatch:

- Persists raised events to a durable store.
- A background worker reads pending records and dispatches them via your configured `IDispatchDomainEvents`.
- Supports **MongoDB** and **SQL Server**.
- Participates in your transactions via very small provider abstractions.

> **Important:** Commit/rollback and disposal are handled **internally by the outbox worker**.  
> Do **not** commit, rollback, or dispose the underlying transaction/lease yourself.

### MongoDB outbox

```csharp
using Hermes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddMediatR(c => c.RegisterServicesFromAssemblyContaining<Program>())
    .AddHermes(b =>
    {
        b.AddMediatR();

        b.AddOutbox(ob =>
        {
            ob.AddMongoDb(new()
            {
                ConnectionString = "mongodb://localhost:27017",
                DatabaseName     = "appdb",
                // CollectionName = "outboxrecords" // optional
            });

            // If using NServiceBus Mongo persistence:
            // ob.UseNServiceBusStorageSession(); // provides IMongoSessionProvider from NServiceBus
        });
    });
```

**`IMongoSessionProvider`** allows Hermes to participate in your MongoDB session/transaction:

```csharp
using MongoDB.Driver;

public interface IMongoSessionProvider
{
    IClientSessionHandle? Session { get; }
}
```

> There are **no** `Commit` / `Abort` methods on this interface.  
> Transaction commit/rollback is coordinated internally by Hermes.

### SQL Server outbox

```csharp
using Hermes.Abstractions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddMediatR(c => c.RegisterServicesFromAssemblyContaining<Program>())
    .AddHermes(b =>
    {
        b.AddMediatR();

        b.AddOutbox(ob =>
        {
            ob.AddSqlServer(new()
            {
                ConnectionString = "Server=.;Database=AppDb;Trusted_Connection=True;Encrypt=False;",
                SchemaName       = "outbox",
                TableName        = "OutboxRecords"
            });

            // If using NServiceBus SQL persistence:
            // ob.UseNServiceBusStorageSession(); // maps storage session to IConnectionLeaseProvider
        });
    });
```

**`IConnectionLeaseProvider`** lets Hermes use your SQL connection + transaction:

```csharp
using Hermes.Outbox.SqlServer;
using Microsoft.Data.SqlClient;

public interface IConnectionLeaseProvider
{
    ValueTask<IConnectionLease> LeaseAsync(CancellationToken ct);
}

public interface IConnectionLease : IAsyncDisposable
{
    SqlConnection Connection { get; }
    SqlTransaction? Transaction { get; }
}
```

> The **lease lifecycle is owned by Hermes**.  
> Do **not** manually dispose/commit/rollback the lease — the outbox worker handles it.

### NServiceBus adapters

Re-use the NServiceBus storage session as the transaction context:

**MongoDB**
```csharp
b.AddOutbox(ob =>
{
    ob.AddMongoDb(...);
    ob.UseNServiceBusStorageSession(); // IMongoSessionProvider from NServiceBus
});
```

**SQL Server**
```csharp
b.AddOutbox(ob =>
{
    ob.AddSqlServer(...);
    ob.UseNServiceBusStorageSession(); // IConnectionLeaseProvider from NServiceBus
});
```

---

## Clean Architecture fit

- **Domain**  
  References only `Hermes.Core`. Raises events via `DomainEventTracker.RaiseDomainEvent(...)`.  
  No `Events` collection, no knowledge of dispatching or outbox.

- **Application**  
  Orchestrates operations; MediatR behavior (from Hermes.MediatR) automatically flushes events.

- **Infrastructure**  
  Configures outbox storage (Mongo/SQL), transaction providers, and messaging integrations (e.g., NServiceBus).

- **Presentation/API**  
  Optionally manages request scopes if you need explicit isolation (often not required).

---

## AddHermes API (DI)

Hermes uses a tiny builder to register components.

```csharp
services.AddHermes(b =>
{
    b.AddMediatR();
    b.AddOutbox(ob =>
    {
        ob.AddMongoDb(...);
        // or:
        // ob.AddSqlServer(...);
        // optional:
        // ob.UseNServiceBusStorageSession();
    });
});
```

**Signature**
```csharp
public static IServiceCollection AddHermes(
    this IServiceCollection services,
    Action<Hermes.Abstractions.IHermesBuilder> configure);
```

`IHermesBuilder` exposes the underlying `IServiceCollection` so you can extend/customize the setup.

---

## Core API reference

### `DomainEventTracker` (Hermes.Core)

| Method | Description |
| --- | --- |
| `Task<IDomainEventScope> CreateScope()` | Creates an ambient scope (nestable). |
| `void RaiseDomainEvent(IDomainEvent domainEvent)` | Raises a domain event from anywhere in the domain. |
| `IReadOnlyCollection<IDomainEvent> Peek()` | Inspect currently raised events without clearing them. |
| `IReadOnlyCollection<IDomainEvent> GetAndClearEvents()` | Retrieve and clear the collected events for the current (deepest) scope. |

### `IDispatchDomainEvents` (Hermes.Abstractions)

```csharp
public interface IDispatchDomainEvents
{
    Task Dispatch(IDomainEvent domainEvent, CancellationToken cancellationToken);
    Task Dispatch(IDomainEvent[] domainEvents, CancellationToken cancellationToken);
}
```

> Hermes.MediatR provides an implementation that publishes via `IMediator`, and includes an automatic behavior that flushes raised events after each pipeline execution.

---

## FAQ (quick)

**Do I need to add an `Events` list to my aggregates?**  
No. Raise events with `DomainEventTracker.RaiseDomainEvent(...)` and let Hermes collect them.

**Is it safe across `async/await`?**  
Yes. Hermes uses `AsyncLocal<T>` to keep events bound to the current async execution flow.

**How do duplicates get handled?**  
Hermes does not attempt to deduplicate; if you need deduplication, implement it at your dispatcher/consumer side.

**Who commits/rolls back the DB transaction for the outbox?**  
Hermes coordinates commit/rollback internally in the outbox worker. Don’t dispose/commit/rollback your lease/session manually.

**Does MediatR require a custom behavior from me?**  
No. When you use `b.AddMediatR()`, Hermes adds its own behavior that dispatches raised events automatically.

---

## Summary

- **Minimal domain impact**: raise events anywhere, no aggregate `Events` list.  
- **Async-safe**: powered by `AsyncLocal<T>`.  
- **MediatR auto-dispatch**: built-in behavior flushes events after each request.  
- **Outbox ready**: MongoDB & SQL Server with transaction participation.  
- **NServiceBus adapters**: reuse existing storage sessions.  
- **Clean Architecture aligned**: domain stays pure; infrastructure handles dispatch/persistence.

> Hermes — domain events without domain pollution.
