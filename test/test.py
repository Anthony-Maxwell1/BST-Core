import json

import websocket

ws = websocket.WebSocket()
ws.connect("ws://localhost:5000")

ws.send(
    json.dumps(
        {
            "type": "cli",
            "command": "open-project",
            "id": "test1234",
            "args": {
                "name": "Place1"
            }
        }
    )
)
print(ws.recv())
