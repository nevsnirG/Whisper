# Whisper

Whisper is a minimal-impact **domain event tracking** library for .NET.  
It lets rich domain models raise domain events with **no pollution** — no `Events` property on aggregates, no plumbing in entities, and no domain-level awareness of dispatching or persistence.

Under the hood, Whisper uses `AsyncLocal<T>` to safely track domain events across both synchronous and asynchronous execution flows.  
Events raised during a logical operation (e.g., a MediatR request, an NServiceBus message handler, or an ASP.NET Core request) remain attached to that flow until they are dispatched or persisted by outer layers.

Whisper integrates cleanly with **DDD** and **Clean Architecture** by keeping the **domain pure** and moving event handling to the **application** and **infrastructure** layers.  
It also provides an optional **outbox** with **MongoDB** and **SQL Server** support, plus drop-in packages for **MediatR** and integration with **NServiceBus** unit of work.

> 📖 Deep dive article: [Minimal-impact domain events](https://vgss.io/posts/domain-events.html)

---

## Why Whisper?

Traditional domain event patterns often force you to:

- Add an `Events` collection to every aggregate and bubble those events up.
- Hand-roll async/thread context handling, or rely on brittle statics.
- Mix domain logic with dispatching, messaging, or persistence concerns.

**Whisper avoids all of that.**

- **Clean domain** — no `Events` list and no infrastructure references in your entities.
- **Async-safe tracking** — uses `AsyncLocal` so events flow across `await`s.
- **Infrastructure integration** — MediatR, NServiceBus, MongoDB, and SQL Server support.
- **Zero ceremony** — a single static call to raise an event from anywhere in your domain.

---

## Packages

| Package | Purpose |
| --- | --- |
| [**Whisper**](https://www.nuget.org/packages/Whisper) | Core tracking logic (`Whisper`, `IDomainEvent`, scopes) |
| [**Whisper.Abstractions**](https://www.nuget.org/packages/Whisper.Abstractions) | Shared contracts (`IWhisperBuilder`, `IDispatchDomainEvents`) |
| [**Whisper.MediatR**](https://www.nuget.org/packages/Whisper.MediatR) | MediatR integration — **automatically** dispatches raised events after each request |
| [**Whisper.AspNetCore**](https://www.nuget.org/packages/Whisper.AspNetCore) | AspNetCore integration — **automatically** dispatches raised events after each request |
| [**Whisper.Outbox**](https://www.nuget.org/packages/Whisper.Outbox) | Outbox infrastructure + background worker and installer |
| [**Whisper.Outbox.MongoDb**](https://www.nuget.org/packages/Whisper.Outbox.MongoDb) | MongoDB outbox store + transaction participation via `IMongoSessionProvider` |
| [**Whisper.Outbox.SqlServer**](https://www.nuget.org/packages/Whisper.Outbox.SqlServer) | SQL Server outbox store + transaction participation via `IConnectionLeaseProvider` |
| [**Whisper.Outbox.MongoDb.NServiceBus**](https://www.nuget.org/packages/Whisper.Outbox.MongoDb.NServiceBus) | Adapter to reuse the NServiceBus **Mongo** storage session |
| [**Whisper.Outbox.SqlServer.NServiceBus**](https://www.nuget.org/packages/Whisper.Outbox.SqlServer.NServiceBus) | Adapter to reuse the NServiceBus **SQL** storage session |

> Licensed under **MIT**.

---

## How it works (high level)

Whisper keeps a **per-execution “domain event scope”** in an `AsyncLocal<T>`:

1. In your **domain**, you raise events:
   ```csharp
   Whispers.About(new OrderApproved(orderId));
   ```
2. Whisper attaches those events to the current async flow.
3. In your **application / infrastructure** layer, Whisper (or your configured integration) retrieves and dispatches/persists the collected events at the right time — e.g., after a MediatR pipeline completes or via the outbox worker.

Your domain never exposes an events collection and never learns about dispatching, messaging, or persistence.

---

## Core usage

### 1) Define a domain event

```csharp
using Whisper;

public sealed record OrderApproved(Guid OrderId) : IDomainEvent;
```

### 2) Raise it from anywhere in the domain

```csharp
using Whisper;

public class Order
{
    public Guid Id { get; }

    public void Approve()
    {
        // Domain logic...
        Whispers.About(new OrderApproved(Id));
    }
}
```

### 3) Optional: scopes for isolation

If you want isolation per request/unit of work, create a scope. Events raised inside are collected and can be read/cleared as needed:

```csharp
using Whisper;

using var scope = await Whispers.CreateScope();
// ... domain operations that raise events
var raised = Whispers.GetAndClearEvents(); // events for this scope (and children)
```

> Most users don’t need to manage scopes explicitly — integrations (like MediatR) take care of flushing at the right time.

---

## MediatR integration

When you call `b.AddMediatR()` inside the Whisper builder, Whisper registers:
- An `IDispatchDomainEvents` implementation that publishes via `IMediator`.
- A **MediatR pipeline behavior** that **automatically retrieves and dispatches** all raised domain events after each request.

```csharp
using Whisper.Abstractions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddMediatR(c => c.RegisterServicesFromAssemblyContaining<Program>())
    .AddWhisper(b =>
    {
        b.AddMediatR(); // wires up dispatcher + automatic behavior
    });
```

---

## AspNetCore integration

When you call `app.UseDomainEventDispatcherMiddleware()` on the IApplicationBuilder of your AspNetCore host, Whisper registers:
- A conventional `DomainEventDispatcherMiddleware` implementation that publishes via the registered `IDispatchDomainEvents` implementations.

```csharp
using Microsoft.AspNetCore.Builder;

app.UseDomainEventDispatcherMiddleware();
```

---

## Outbox & transactions

Whisper provides an **outbox** for reliable, asynchronous dispatch:

- Persists raised events to a durable store.
- A background worker reads pending records and dispatches them via your configured `IDispatchDomainEvents`.
- Supports **MongoDB** and **SQL Server**.
- Optionally participates in your unit of work via very small provider abstractions.

**Unit-of-work participation is opt-in**, enabled explicitly via builder methods — `ob.UseConnectionLeaseProvider<T>()` (SQL Server) and `ob.UseMongoSessionProvider<T>()` (MongoDB). Ownership follows origin: **Whisper disposes only what it creates, and it creates only when no provider is registered.**

- **No provider registered:** Whisper manages its own database access. For SQL Server it opens and disposes its own `SqlConnection` per operation, with no transaction; for MongoDB it uses plain writes with no `IClientSessionHandle` (and therefore no replica set requirement). Outbox records commit independently of any host transaction.
- **Provider registered:** the host owns the connection/transaction/session entirely — Whisper only uses what the provider yields and never opens, begins, commits, rolls back, or disposes it.

> The background worker always reads and dispatches **after** the unit of work completes — deliberately outside it.  
> That is at-least-once publishing: the point of an outbox.

### MongoDB outbox

```csharp
using Whisper.Abstractions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddMediatR(c => c.RegisterServicesFromAssemblyContaining<Program>())
    .AddWhisper(b =>
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
        });
    });
```

**`IMongoSessionProvider`** (optional) lets the dispatch-side `Add` participate in your MongoDB session/transaction:

```csharp
using MongoDB.Driver;

public interface IMongoSessionProvider
{
    public bool IsInTransaction => Session is not null;

    IClientSessionHandle? Session { get; }
}
```

> There are **no** `Commit` / `Abort` methods on this interface.  
> When `Session` is `null`, Whisper writes without a session; otherwise it passes the session to its writes and the **host** owns it — Whisper never starts, commits, aborts, or disposes it.

**Default — no session**

`AddMongoDb` alone registers **no** provider. Outbox records are written as plain inserts — no `IClientSessionHandle`, no replica set requirement — and commit independently of any host transaction.

```csharp
b.AddOutbox(ob => ob.AddMongoDb(new() { /* ... */ }));
```

**Host-managed session**

For the common “my unit of work starts the session” case, Whisper ships a built-in provider pair. Register it with `UseHostManagedMongoSession()`, then hand your session over via `IMongoSessionProviderInitializer` after starting it:

```csharp
b.AddOutbox(ob =>
{
    ob.AddMongoDb(new() { /* ... */ });
    ob.UseHostManagedMongoSession();
});
```

```csharp
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Whisper.Outbox.MongoDb;

// Inside your unit of work, after starting the session:
using var session = await mongoClient.StartSessionAsync(cancellationToken: ct);
session.StartTransaction();

serviceProvider.GetRequiredService<IMongoSessionProviderInitializer>().Initialize(session);

// ... domain operations — the outbox `Add` now joins `session`

await session.CommitTransactionAsync(ct); // the host owns commit/abort
```

> A scope that never calls `Initialize` gets plain (session-less) writes.

**NServiceBus storage session**

```csharp
b.AddOutbox(ob =>
{
    ob.AddMongoDb(new() { /* ... */ });
    ob.UseNServiceBusStorageSession(); // IMongoSessionProvider backed by the NServiceBus storage session
});
```

**Custom unit of work**

Return the session your unit of work already owns; the unit of work keeps ownership of commit/abort:

```csharp
using MongoDB.Driver;
using Whisper.Outbox.MongoDb;

public sealed class UnitOfWorkMongoSessionProvider(MyUnitOfWork unitOfWork) : IMongoSessionProvider
{
    public IClientSessionHandle? Session => unitOfWork.Session;
}
```

```csharp
b.AddOutbox(ob =>
{
    ob.AddMongoDb(new() { /* ... */ });
    ob.UseMongoSessionProvider<UnitOfWorkMongoSessionProvider>();
});
```

> `UseMongoSessionProvider` also has an overload taking a `Func<IServiceProvider, TProvider>` factory. Registering `IMongoSessionProvider` directly in DI is honored too, but the builder method is the intended path.

### SQL Server outbox

```csharp
using Whisper.Abstractions;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection()
    .AddMediatR(c => c.RegisterServicesFromAssemblyContaining<Program>())
    .AddWhisper(b =>
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
        });
    });
```

**`IConnectionLeaseProvider`** (optional) lets the dispatch-side `Add` use your SQL connection + transaction:

```csharp
using Microsoft.Data.SqlClient;

public interface IConnectionLeaseProvider
{
    ValueTask<ConnectionLease> Provide(CancellationToken cancellationToken);
}

public sealed record ConnectionLease(SqlConnection Connection, SqlTransaction? Transaction = null);
```

> **Ownership by origin:** Whisper disposes only what it creates, and it creates only when no provider is registered.  
> `ConnectionLease` is pure data — everything a provider yields belongs to the host; Whisper only uses the connection and transaction and never opens, commits, rolls back, or disposes them.

**Default — own connection per operation**

`AddSqlServer` alone registers **no** provider. Without one, Whisper opens a fresh `SqlConnection` per operation, uses no transaction, and disposes the connection itself. Outbox records commit independently of any host transaction.

```csharp
b.AddOutbox(ob => ob.AddSqlServer(new() { /* ... */ }));
```

**NServiceBus storage session**

```csharp
b.AddOutbox(ob =>
{
    ob.AddSqlServer(new() { /* ... */ });
    ob.UseNServiceBusStorageSession(); // IConnectionLeaseProvider backed by the NServiceBus storage session
});
```

**Custom unit of work**

Return the connection and transaction your unit of work already owns; the unit of work keeps ownership of commit/rollback and disposal:

```csharp
using Whisper.Outbox.SqlServer;

public sealed class UnitOfWorkConnectionLeaseProvider(MyUnitOfWork unitOfWork) : IConnectionLeaseProvider
{
    public ValueTask<ConnectionLease> Provide(CancellationToken cancellationToken)
        => ValueTask.FromResult(new ConnectionLease(unitOfWork.Connection, unitOfWork.Transaction));
}
```

```csharp
b.AddOutbox(ob =>
{
    ob.AddSqlServer(new() { /* ... */ });
    ob.UseConnectionLeaseProvider<UnitOfWorkConnectionLeaseProvider>();
});
```

> **Warning — every scope, no fallback:** a registered `IConnectionLeaseProvider` is consulted for **every** store operation, including the background worker’s own polling scopes, where no unit of work is active. There is deliberately no fallback. Your provider must return a usable, open connection in every scope — when no unit of work is active, open one yourself (e.g., from configuration); otherwise the worker can never read or dispatch outbox records. This applies to the SQL provider only: the Mongo equivalent handles it naturally (`Session` is `null` → plain writes).

> `UseConnectionLeaseProvider` also has an overload taking a `Func<IServiceProvider, TProvider>` factory. Registering `IConnectionLeaseProvider` directly in DI is honored too, but the builder method is the intended path.

### NServiceBus adapters

Re-use the NServiceBus storage session as the transaction context. `UseNServiceBusStorageSession()` may be called before or after `AddMongoDb` / `AddSqlServer` — registration is order-independent. Internally it routes through `UseMongoSessionProvider` / `UseConnectionLeaseProvider`, so the same ownership rule applies: NServiceBus owns the session/connection; Whisper only uses it.

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
  References only `Whisper`. Raises events via `Whisper.About(...)`.  
  No `Events` collection, no leakage of domain events, no knowledge of dispatching or outbox.

- **Application**  
  Orchestrates operations; MediatR behavior (from Whisper.MediatR) automatically flushes events.

- **Infrastructure**  
  Configures outbox storage (Mongo/SQL), transaction providers, and messaging integrations (e.g., NServiceBus).

- **Presentation/API**  
  AspNetCore middleware to scope domain events per request.

---

## AddWhisper API (DI)

Whisper uses a tiny builder to register components.

```csharp
services.AddWhisper(b =>
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
public static IServiceCollection AddWhisper(
    this IServiceCollection services,
    Action<Whisper.Abstractions.IWhisperBuilder> configure);
```

`IWhisperBuilder` exposes the underlying `IServiceCollection` so you can extend/customize the setup.

---

## Core API reference

### `Whisper` (Whisper)

| Method | Description |
| --- | --- |
| `Task<IDisposable> CreateScope()` | Creates an ambient scope (nestable). |
| `void About(IDomainEvent domainEvent)` | Raises a domain event from anywhere in the domain. |
| `IReadOnlyCollection<IDomainEvent> Peek()` | Inspect currently raised events without clearing them. |
| `IReadOnlyCollection<IDomainEvent> GetAndClearEvents()` | Retrieve and clear the collected events for the current (deepest) scope. |

### `IDispatchDomainEvents` (Whisper.Abstractions)

```csharp
public interface IDispatchDomainEvents
{
    Task Dispatch(IDomainEvent domainEvent, CancellationToken cancellationToken);
    Task Dispatch(IDomainEvent[] domainEvents, CancellationToken cancellationToken);
}
```

> Whisper.MediatR provides an implementation that publishes via `IMediator`, and includes an automatic behavior that flushes raised events after each pipeline execution.

---

## FAQ (quick)

**Do I need to add an `Events` list to my aggregates?**  
No. Raise events with `Whisper.About(...)` and let Whisper collect them.

**Is it safe across `async/await`?**  
Yes. Whisper uses `AsyncLocal<T>` to keep events bound to the current async execution flow.

**How do duplicates get handled?**  
Whisper does not attempt to deduplicate; if you need deduplication, implement it at your dispatcher/consumer side.

**Who commits/rolls back the DB transaction for the outbox?**  
You do — if there is one. The rule: **Whisper disposes only what it creates, and it creates only when no provider is registered.** Without a provider there is no host transaction: Whisper opens and disposes its own `SqlConnection` per operation (SQL Server) or performs plain writes (MongoDB), and outbox records commit independently. With a provider registered (`UseConnectionLeaseProvider` / `UseMongoSessionProvider`), the host owns the connection/transaction/session entirely — Whisper only uses them and never begins, commits, rolls back, or disposes anything. The worker dispatches after your unit of work completes (at-least-once).

**Does MediatR require a custom behavior from me?**  
No. When you use `b.AddMediatR()`, Whisper adds its own behavior that dispatches raised events automatically.

---

## Summary

- **Minimal domain impact**: raise events anywhere, no aggregate `Events` list.  
- **Async-safe**: powered by `AsyncLocal<T>`.  
- **MediatR auto-dispatch**: built-in behavior flushes events after each request.  
- **Outbox ready**: MongoDB & SQL Server with transaction participation.  
- **NServiceBus adapters**: reuse existing storage sessions.  
- **Clean Architecture aligned**: domain stays pure; infrastructure handles dispatch/persistence.

> Whisper — domain events without domain pollution.
