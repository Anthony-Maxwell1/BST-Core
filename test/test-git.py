"""
test_git_client.py
------------------
Basic test script for GitClientWorker.
Sends clone, commit, and pull actions and prints raw responses.

pip install websockets
"""

import asyncio
import json
import uuid
import websockets

# ---------------------------------------------------------------------------
# Hardcoded config — edit these before running
# ---------------------------------------------------------------------------

WS_URL       = "ws://localhost:5000"
REMOTE_URL   = "git"
USERNAME     = "username"
PASSWORD     = "password"
AUTHOR_NAME  = "Test Bot"
AUTHOR_EMAIL = "testbot@localhost"
PROJECT_NAME = "projectname"

TIMEOUT = 10  # seconds to wait for responses after each send

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def make_message(action: str, extra: dict = None) -> str:
    return json.dumps({
        "type": "git",
        "id":   str(uuid.uuid4()),
        "args": {
            "action":      action,
            "remoteUrl":   REMOTE_URL,
            "username":    USERNAME,
            "password":    PASSWORD,
            "authorName":  AUTHOR_NAME,
            "authorEmail": AUTHOR_EMAIL,
            "projectName": PROJECT_NAME,
            **(extra or {}),
        },
    })


async def send(ws, action: str, extra: dict = None):
    msg = make_message(action, extra)
    print(f"\n>>> Sending '{action}':")
    print(json.dumps(json.loads(msg), indent=2))

    await ws.send(msg)

    print(f"\n<<< Responses (waiting up to {TIMEOUT}s):")
    got_any = False
    try:
        deadline = asyncio.get_event_loop().time() + TIMEOUT
        while True:
            remaining = deadline - asyncio.get_event_loop().time()
            if remaining <= 0:
                break
            raw = await asyncio.wait_for(ws.recv(), timeout=remaining)
            print(raw)
            got_any = True
    except asyncio.TimeoutError:
        pass

    if not got_any:
        print("(no response — check server logs)")

# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------

async def run():
    print(f"Connecting to {WS_URL} ...")
    async with websockets.connect(WS_URL) as ws:
        print("Connected.\n" + "="*60)

        # await send(ws, "clone")
        # print("="*60)

        await send(ws, "commit", {"message": "test commit"})
        print("="*60)

        # await send(ws, "pull")
        # print("="*60)

    print("\nDone.")


if __name__ == "__main__":
    asyncio.run(run())