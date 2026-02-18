using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<MainWebSocketWorker>();
        services.AddHostedService<InternalClientWorker>();
    })
    .Build();

await host.RunAsync();
