import websocket

ws = websocket.WebSocket()
ws.connect("ws://localhost:5000")

ws.send("{\"type\": \"cli\", \"command\": \"status\"}")
print(ws.recv())
