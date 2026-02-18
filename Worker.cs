using Fleck;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BST_Core;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly List<IWebSocketConnection> _clients = new();

    public Worker(ILogger<Worker> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var server = new WebSocketServer("ws://0.0.0.0:5000");

        server.Start(socket =>
        {
            socket.OnOpen = () =>
            {
                _clients.Add(socket);
                _logger.LogInformation("Client connected: {0}", socket.ConnectionInfo.ClientIpAddress);
            };

            socket.OnClose = () =>
            {
                _clients.Remove(socket);
                _logger.LogInformation("Client disconnected: {0}", socket.ConnectionInfo.ClientIpAddress);
            };

            socket.OnMessage = message =>
            {
                _logger.LogInformation("Received: {0}", message);
                // broadcast to all other clients
                foreach (var client in _clients)
                {
                    if (client != socket)
                        client.Send(message);
                }
            };
        });

        _logger.LogInformation("Fleck WebSocket server running on ws://0.0.0.0:5000");

        // Keep the Worker running until cancellation
        return Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
