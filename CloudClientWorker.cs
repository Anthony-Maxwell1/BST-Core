using System.Net.WebSockets;
using System.Text;
public class CloudClientWorker : BackgroundService
{
    private readonly ILogger<GitClientWorker> _logger;
    private ClientWebSocket _ws;

    public CloudClientWorker(ILogger<GitClientWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                _ws = new ClientWebSocket();
                await _ws.ConnectAsync(new Uri("ws://localhost:5000"), stoppingToken);
                _logger.LogInformation("Git client connected");

                var buffer = new byte[8192];

                while (_ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    var result = await _ws.ReceiveAsync(buffer, stoppingToken);
                    var msg    = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    // await ProcessMessageAsync(msg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Git client disconnected, retrying in 5s...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }
}