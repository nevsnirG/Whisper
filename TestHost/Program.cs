using Hermes.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.MsSql;

//var container = new MongoDbBuilder().Build();
var container = new MsSqlBuilder().Build();
await container.StartAsync();

var builder = Host.CreateApplicationBuilder(args);
var serviceProvider = builder.Services
    .AddMediatR(c => c.RegisterServicesFromAssemblyContaining<Program>())
    .AddHermes(b =>
    {
        b.AddMediatR();
        b.AddOutbox(ob =>
        {
            //ob.AddMongo(new()
            //{
            //    ConnectionString = container.GetConnectionString(),
            //    DatabaseName = "testdatabase",
            //});
            ob.AddSqlServer(new()
            {
                ConnectionString = container.GetConnectionString(),
                TableName = "outboxrecords",
                SchemaName = "outbox",
            });
        });
    })
    .BuildServiceProvider();

var host = builder.Build();
var hostRunTask = host.RunAsync();

var domainEventDispatchers = host.Services.GetServices<IDispatchDomainEvents>();
var domainEventDispatcher = domainEventDispatchers.Single();
await domainEventDispatcher.Dispatch(new SomeDomainEvent
{
    SomeProperty = "Hoi"
}, CancellationToken.None);

await hostRunTask;