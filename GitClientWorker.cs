using System.Net.WebSockets;

public class GitClientWorker : BackgroundService
{
    private readonly ILogger<InternalClientWorker> _logger;
    private ClientWebSocket _ws;

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        throw new NotImplementedException();
    }
}