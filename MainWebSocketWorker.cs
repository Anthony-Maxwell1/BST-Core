using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Fleck; 

public class MainWebSocketWorker : BackgroundService
{
    private readonly ILogger<MainWebSocketWorker> _logger;
    private readonly List<IWebSocketConnection> _clients = new();

    public MainWebSocketWorker(ILogger<MainWebSocketWorker> logger)
    {
        _logger = logger;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var server = new Fleck.WebSocketServer("ws://0.0.0.0:5000");

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

            socket.OnMessage = msg =>
            {
                // dumb broadcast to all other clients
                foreach (var client in _clients)
                {
                    if (client != socket)
                        client.Send(msg);
                }
            };
        });

        _logger.LogInformation("WebSocket server running on ws://0.0.0.0:5000");

        return Task.Delay(Timeout.Infinite, stoppingToken);
    }
}
