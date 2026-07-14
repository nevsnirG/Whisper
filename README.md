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
| [**Whisper**](https://www.nuget.org/packages/Whisper) | Core tracking logic (`Whispers`, `IDomainEvent`, scopes) |
| [**Whisper.Abstractions**](https://www.nuget.org/packages/Whisper.Abstractions) | Shared contracts (`IWhisperBuilder`, `IDispatchDomainEvents`) |
| [**Whisper.MediatR**](https://www.nuget.org/packages/Whisper.MediatR) | MediatR integration — **automatically** dispatches raised events after each request |
| [**Whisper.AspNetCore**](https://www.nuget.org/packages/Whisper.AspNetCore) | AspNetCore integration — **automatically** dispatches raised events after each request |
| [**Whisper.Outbox**](https://www.nuget.org/packages/Whisper.Outbox) | Outbox infrastructure + background worker and installer |
| [**Whisper.Outbox.MongoDb**](https://www.nuget.org/packages/Whisper.Outbox.MongoDb) | MongoDB outbox store + optional unit-of-work participation via `IMongoSessionProvider` |
| [**Whisper.Outbox.SqlServer**](https://www.nuget.org/packages/Whisper.Outbox.SqlServer) | SQL Server outbox store + optional unit-of-work participation via `IConnectionLeaseProvider` |
| [**Whisper.Outbox.AspNetCore**](https://www.nuget.org/packages/Whisper.Outbox.AspNetCore) | Management dashboard + JSON API for failed outbox records (`MapWhisperOutbox`) |
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

using var scope = Whispers.CreateScope();
// ... domain operations that raise events
var raised = Whispers.GetAndClearEvents(); // events for this scope (and children)
```

Without an explicit scope, `Whispers.About()` stores events in an **implicit collector** bound to the current async context (`AsyncLocal`); `GetAndClearEvents()` and `Peek()` read that same context.

`AsyncLocal` state flows **down** into awaited children, never **up**: events raised in a parallel branch (`Task.Run`, an unawaited task) without an enclosing explicit scope are invisible to the caller. Create the scope **before** branching so all branches share one collector.

> Most users don’t need to manage scopes explicitly — integrations (like MediatR) take care of flushing at the right time. This is why the integrations (the AspNetCore middleware, the MediatR behavior) create an explicit scope per request/pipeline.

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

**Registration order with an ambient unit of work**

If a middleware manages your unit of work, register it **before** (outer to) `UseDomainEventDispatcherMiddleware()`, and commit **after** `await next()` (commit-on-unwind). Whisper dispatches when the pipeline unwinds back through its middleware, so the dispatch lands inside the still-open unit of work:

```csharp
app.UseMiddleware<UnitOfWorkMiddleware>();      // outer: begins the unit of work, commits after next()
app.UseDomainEventDispatcherMiddleware();       // inner: dispatches before the commit
```

> **Warning — OnStarting commits:** unit-of-work middlewares that commit inside `HttpResponse.OnStarting` are unsupported — `OnStarting` fires at the first response write, inside `next()`, before Whisper’s dispatch, so outbox writes would land after the commit. Use commit-on-unwind ordering instead.

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

> The background worker always reads and dispatches **after** the unit of work completes.

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
                // CollectionName = "outboxrecords" // optional, default shown
            });
        });
    });
```

> `AddMongoDb` also has an overload taking a `Func<IServiceProvider, MongoDbOutboxConfiguration>` for configuration resolved from DI.

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

**NServiceBus storage session** (requires `Whisper.Outbox.MongoDb.NServiceBus`)

```csharp
b.AddOutbox(ob =>
{
    ob.AddMongoDb(new() { /* ... */ });
    ob.UseNServiceBusStorageSession(); // IMongoSessionProvider backed by the NServiceBus storage session
});
```

> Internally routes through `UseMongoSessionProvider`, so the same ownership rule applies: NServiceBus owns the session; Whisper only uses it. May be called before or after `AddMongoDb` — registration is order-independent.

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

> `UseMongoSessionProvider` also has an overload taking a `Func<IServiceProvider, TProvider>` factory:  
> `ob.UseMongoSessionProvider(sp => new UnitOfWorkMongoSessionProvider(sp.GetRequiredService<MyUnitOfWork>()));`  
> Registering `IMongoSessionProvider` directly in DI is honored too, but the builder method is the intended path.

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
                SchemaName       = "outbox",        // optional, default "dbo"
                TableName        = "OutboxRecords"  // optional, default "outboxrecords"
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

**`TransactionScope` — zero-code opt-in**

On this no-provider path, the `SqlConnection` Whisper opens enlists in an ambient `System.Transactions.TransactionScope` by default (`Enlist=true`). Wrap the use case in a scope and outbox writes plus your own SQL join one transaction that the **host** completes:

```csharp
using var tx = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
// ... host SQL work + domain operations that raise events
tx.Complete();
```

> `TransactionScopeAsyncFlowOption.Enabled` is required with async code. Sequential opens against the same connection string reuse the transaction-affiliated pooled connection, so a single SQL Server does not escalate to MSDTC — but concurrent connections inside the scope can escalate to a distributed transaction.

**NServiceBus storage session** (requires `Whisper.Outbox.SqlServer.NServiceBus`)

```csharp
b.AddOutbox(ob =>
{
    ob.AddSqlServer(new() { /* ... */ });
    ob.UseNServiceBusStorageSession(); // IConnectionLeaseProvider backed by the NServiceBus storage session
});
```

> Internally routes through `UseConnectionLeaseProvider`, so the same ownership rule applies: NServiceBus owns the connection and transaction; Whisper only uses them. May be called before or after `AddSqlServer` — registration is order-independent.

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

> `UseConnectionLeaseProvider` also has an overload taking a `Func<IServiceProvider, TProvider>` factory:  
> `ob.UseConnectionLeaseProvider(sp => new UnitOfWorkConnectionLeaseProvider(sp.GetRequiredService<MyUnitOfWork>()));`  
> Registering `IConnectionLeaseProvider` directly in DI is honored too, but the builder method is the intended path.

### Worker configuration & retry semantics

The background worker polls the store on a fixed interval. Configure it via `ConfigureWorker`:

```csharp
b.AddOutbox(ob =>
{
    ob.AddSqlServer(new() { /* ... */ });
    ob.ConfigureWorker(w =>
    {
        w.BatchSize              = 10;   // default
        w.PollingIntervalMs      = 1000; // default
        w.UsePollingTimeProvider = false; // default — polling waits in real time
    });
});
```

> **Polling clock:** polling waits in real time by default — a globally registered fake `TimeProvider` does **not** freeze polling unless you opt in via `w.UsePollingTimeProvider = true` (drives the polling delay from the registered `TimeProvider`), but it **does** drive all timestamps and retry due-times. `AddOutbox` registers `TimeProvider.System` with `TryAddSingleton`, so a host-registered `TimeProvider` is never overridden.

The worker reads records that are **due**: not dispatched, not failed, and `NextRetryAtUtc` null or at/before now — ordered by `NextRetryAtUtc` ascending (nulls first), then `Id` ascending. With no retry delay configured this is identical to plain `Id` order. Per record in a batch:

- **Success:** the record is marked `DispatchedAtUtc`.
- **Deserialization failure is permanent:** the record is marked `FailedAtUtc` immediately — no retry.
- **Dispatch failure:** an **unrecoverable** exception (per the recoverability policy) fails the record immediately; otherwise `Retries` is incremented and the next attempt is scheduled via `NextRetryAtUtc`; once `Retries + 1 >= MaxRetries`, the record is marked `FailedAtUtc`.
- **Every failed attempt persists the error on the record:** `LastError` (`Exception.ToString()`, truncated to 32,768 characters) and `LastErrorAtUtc` — so you can always see why a record is retrying or failed.

The worker loop catches and logs all errors and keeps polling.

> Failed records **stay in the store** — there is no automatic cleanup. Monitor them yourself or use the [management dashboard](#management-dashboard) to inspect, retry, or delete them.

### Recoverability

`ConfigureRecoverability` controls how failed dispatch attempts are handled — maximum attempts, the delay before the next attempt, and which exceptions are unrecoverable:

```csharp
b.AddOutbox(ob =>
{
    ob.AddSqlServer(new() { /* ... */ });
    ob.ConfigureRecoverability(r =>
    {
        r.MaxRetries = 3;                                                     // default; total dispatch attempts
        r.RetryDelay = attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)); // exponential backoff
        r.UnrecoverableExceptionTypes.Add(typeof(NotSupportedException));
        r.UnrecoverableExceptionPredicates.Add(ex => ex.Message.Contains("permanent"));
    });
});
```

- `MaxRetries` (default `3`) — maximum **total dispatch attempts**; a record fails permanently when `Retries + 1 >= MaxRetries`. *Moved here from `OutboxWorkerOptions` (breaking).*
- `RetryDelay` — maps the 1-based failed-attempt ordinal to the delay before the next attempt. `null` (the default) or a non-positive delay means the record is eligible again at the next poll — the previous behavior.
- `UnrecoverableExceptionTypes` — exception types (including derived types) that permanently fail a record on the first occurrence.
- `UnrecoverableExceptionPredicates` — predicates that mark an exception unrecoverable.

> **Polling granularity:** the effective retry moment is the first poll at or after `NextRetryAtUtc`, so delays shorter than `PollingIntervalMs` behave like immediate (next-poll) retries.

> A throwing predicate is logged and treated as non-matching (recoverable); a throwing `RetryDelay` is logged and falls back to a next-poll retry.

### Serializer configuration

Domain events are serialized with System.Text.Json. Use `ConfigureSerializer` to register `JsonConverter`s for value types (e.g., readonly record structs) that System.Text.Json cannot deserialize using the default parameterless constructor:

```csharp
b.AddOutbox(ob =>
{
    ob.AddSqlServer(new() { /* ... */ });
    ob.ConfigureSerializer(s => s.Converters.Add(new MyValueTypeConverter()));
});
```

### Startup & installation

`AddOutbox` registers an installer hosted service that prepares the store at startup:

- **SQL Server:** creates the schema (if missing) and the table with a clustered primary key on `Id`, UTC check constraints, the filtered ready index `IX_Outbox_Ready_ByDue`, and the failed-records index `IX_Outbox_Failed_ByFailedAt`.
- **MongoDB:** creates the partial index `ix_outbox_ready_by_due` and the index `ix_outbox_failed_by_failedat` on the outbox collection.

Existing tables/collections are **upgraded in place**: the installer idempotently adds the new columns (`LastError`, `LastErrorAtUtc`, `NextRetryAtUtc`) and indexes and drops the legacy undispatched index. On SQL Server this runs as two sequential batches (columns first, then constraints/indexes) so upgrades from older schemas compile cleanly.

Dispatches issued before installation completes are gated, not failed — they wait until the installer signals completion. The background worker waits the same way before its first poll.

### UUID generation

`AddOutbox` registers a default `IUuidProvider` that generates database-friendly UUIDs (via UUIDNext); `AddSqlServer` deterministically replaces it with a SQL Server-specific provider that generates sequential GUIDs, friendly to the clustered primary key.

To override it, implement `IUuidProvider` (`Guid Provide()`) and register your implementation **after** the `AddWhisper(...)` call so it wins over the backend’s replacement:

```csharp
services.AddWhisper(b => b.AddOutbox(ob => ob.AddSqlServer(...)));
services.Replace(ServiceDescriptor.Transient<IUuidProvider, MyUuidProvider>());
```

> Outbox records are read and dispatched **due-first** — ordered by `NextRetryAtUtc` (nulls first), then `Id`; identical to plain `Id` order when no retry delay is configured. On SQL Server `Id` is the clustered primary key — a non-sequential custom provider changes dispatch ordering behavior and fragments the clustered index.

### Management dashboard

The **Whisper.Outbox.AspNetCore** package adds a host-mounted management dashboard for failed outbox records:

```csharp
using Microsoft.AspNetCore.Builder;

app.MapWhisperOutbox();                        // mounts at /whisper/outbox
// or: app.MapWhisperOutbox("/admin/outbox");  // custom pattern
```

| Endpoint | Purpose |
| --- | --- |
| `GET /` | The dashboard page |
| `GET /api/failed?page&pageSize` | Page failed records, `FailedAtUtc` descending (`pageSize` clamped to 1..200) |
| `GET /api/records/{id}` | Full record incl. `Payload` and `LastError` (404 on miss) |
| `POST /api/records/{id}/retry` | Re-queue a failed record (204 / 404) |
| `POST /api/failed/retry-all` | Re-queue all failed records → `{ "retried": n }` |
| `DELETE /api/records/{id}` | Delete a failed record (204 / 404) |

Retrying resets `FailedAtUtc`, `Retries`, and `NextRetryAtUtc` but **keeps `LastError`** as an audit trail. Retry and delete only affect records that are actually failed, so the worker and the dashboard can never fight over a record.

**Secure by default.** Every endpoint is mapped with `RequireAuthorization()`; opt out explicitly via `app.MapWhisperOutbox(configure: o => o.AllowAnonymous = true)`. A host without authentication/authorization configured gets an `InvalidOperationException` on the first request — an intended fail-safe, because the dashboard exposes event payloads. `MapWhisperOutbox` returns an `IEndpointConventionBuilder` for policies of your own, and throws at map time when no outbox storage backend (and therefore no `IOutboxManagementStore`) is registered.

> The embedded page relies on inline `<script>`/`<style>`. If your host enforces a strict Content-Security-Policy, allow inline script and style for the dashboard route.

---

## Clean Architecture fit

- **Domain**  
  References only `Whisper`. Raises events via `Whispers.About(...)`.  
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

### `Whispers` (Whisper)

| Method | Description |
| --- | --- |
| `IDisposable CreateScope()` | Creates an ambient scope (nestable). |
| `void About(IDomainEvent domainEvent)` | Raises a domain event from anywhere in the domain. |
| `IDomainEvent[] Peek()` | Inspect currently raised events without clearing them. |
| `IDomainEvent[] GetAndClearEvents()` | Retrieve and clear the collected events for the current (deepest) scope. |

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
No. Raise events with `Whispers.About(...)` and let Whisper collect them.

**Is it safe across `async/await`?**  
Yes. Whisper uses `AsyncLocal<T>` to keep events bound to the current async execution flow. Concurrent `About()` calls inside a shared scope are thread-safe. Parallel branches without their own scope each get an isolated implicit collector whose events don’t flow back to the parent — by design, `AsyncLocal` flows down, not up.

**How do duplicates get handled?**  
Whisper does not attempt to deduplicate; if you need deduplication, implement it at your dispatcher/consumer side. Outbox publishing is at-least-once: a record that was dispatched successfully but not yet marked (e.g., a crash between the dispatch and `SetDispatchedAt`) is re-dispatched on a later poll — consumers must be idempotent.

**Who commits/rolls back the DB transaction for the outbox?**  
You do — if there is one. The rule: **Whisper disposes only what it creates, and it creates only when no provider is registered.** Without a provider there is no host transaction: Whisper opens and disposes its own `SqlConnection` per operation (SQL Server) or performs plain writes (MongoDB), and outbox records commit independently. With a provider registered (`UseConnectionLeaseProvider` / `UseMongoSessionProvider`), the host owns the connection/transaction/session entirely — Whisper only uses them and never begins, commits, rolls back, or disposes anything. The worker dispatches after your unit of work completes (at-least-once).

**Where do I see why an event failed?**  
On the record itself. Every failed dispatch attempt persists the exception on the outbox record — `LastError` (`Exception.ToString()`, truncated) and `LastErrorAtUtc` — while the record is retrying and after it fails permanently. Mount the management dashboard from `Whisper.Outbox.AspNetCore` (`app.MapWhisperOutbox()`) to browse failed records, inspect the error and payload, and retry or delete them.

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
