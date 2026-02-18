using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

public class InternalClientWorker : BackgroundService
{
    private readonly ILogger<InternalClientWorker> _logger;

    public InternalClientWorker(ILogger<InternalClientWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var ws = new ClientWebSocket();
                await ws.ConnectAsync(new Uri("ws://localhost:5000"), stoppingToken);
                _logger.LogInformation("Internal client connected");

                var buffer = new byte[4096];

                while (ws.State == WebSocketState.Open && !stoppingToken.IsCancellationRequested)
                {
                    var result = await ws.ReceiveAsync(buffer, stoppingToken);
                    var msg = Encoding.UTF8.GetString(buffer, 0, result.Count);

                    ProcessMessage(msg);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Internal client disconnected, retrying in 5s...");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private void ProcessMessage(string msg)
    {
        try
        {
            using var doc = JsonDocument.Parse(msg);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp))
                return;

            var type = typeProp.GetString();
            if (type != "cli")
                return; // ignore all non-CLI packets

            // handle CLI packet
            HandleCliPacket(root);
        }
        catch (JsonException)
        {
            // ignore invalid JSON
        }
    }

    private void HandleCliPacket(JsonElement packet)
    {
        var command = packet.GetProperty("command").GetString();
        _logger.LogInformation("CLI packet received: {command}", command);

        // optionally parse args
        if (packet.TryGetProperty("args", out var args))
        {
            // handle arguments
        }

        // act based on command
        switch (command)
        {
            case "unpack":
                // call unpack routine
                break;
            case "pack":
                // call pack routine
                break;
            case "status":
                // return status
                break;
        }
    }
}
