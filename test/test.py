import websocket

ws = websocket.WebSocket()
ws.connect("ws://localhost:5000")

ws.send("hello")
print(ws.recv())
