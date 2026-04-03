using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices(services =>
    {
        services.AddHostedService<MainWebSocketWorker>();
        services.AddHostedService<InternalClientWorker>();
        services.AddHostedService<GitClientWorker>();
        services.AddHostedService<CloudClientWorker>();
    })
    .Build();

await host.RunAsync();
