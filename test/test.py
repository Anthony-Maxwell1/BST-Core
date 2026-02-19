import json

import websocket

ws = websocket.WebSocket()
ws.connect("ws://localhost:5000")

ws.send(
    json.dumps(
        {
            "type": "cli",
            "command": "close-project",
            "id": "test1234",
        }
    )
)
print(ws.recv())
