"""
Minimal research client for the Lucid-Stream WebSocket API (schema 1.x).

    pip install websockets numpy
    python ws_client.py --code 123456            # pairing code shown in Lucid-Stream
    python ws_client.py --code 123456 --host 192.168.1.20   # LAN mode

Pairs with the 6-digit code, subscribes to raw EEG (binary) and band power (JSON),
and prints a short summary every second. The paired token is printed so you can reconnect
with --token while the streaming session lasts.
"""
import argparse
import asyncio
import json
import struct
import time

import numpy as np
import websockets

HEADER = struct.Struct("<4sBBHIQdHH")  # magic, version, stream id, flags, seq, index, t0, samples, channels


def decode(frame: bytes):
    magic, version, stream_id, flags, seq, index, t0, n, ch = HEADER.unpack_from(frame, 0)
    if magic != b"LCDF" or version != 1:
        raise ValueError("not a v1 LCDF frame")
    data = np.frombuffer(frame, dtype="<f4", offset=HEADER.size, count=n * ch).reshape(n, ch)
    return stream_id, flags, seq, index, t0, data


async def main(args):
    url = f"ws://{args.host}:{args.port}/v1/stream"
    hello = {"type": "hello", "schema": "1.0", "client": "python-example"}
    if args.token:
        hello["token"] = args.token
    else:
        hello["pairing_code"] = args.code

    async with websockets.connect(url, max_size=2**22) as ws:
        await ws.send(json.dumps(hello))
        welcome = json.loads(await ws.recv())
        if welcome.get("type") != "welcome":
            raise SystemExit(f"unexpected reply: {welcome}")
        streams = {s["id"]: s for s in welcome["streams"]}
        print(f"Connected to {welcome['device']['model']} {welcome['device']['serial']} ({welcome['mode']}), scopes {welcome['scopes']}")
        if "token" in welcome:
            print(f"Paired. Reconnect with: --token {welcome['token']}")

        wanted = []
        if "read:raw" in welcome["scopes"]:
            wanted.append(("eeg.raw", "binary"))
        if "read:processed" in welcome["scopes"]:
            wanted.append(("bandpower", "json"))
        for key, enc in wanted:
            await ws.send(json.dumps({"type": "subscribe", "streams": [key], "encoding": enc}))

        raw_samples, last_bp, last_print, deadline = 0, None, time.time(), time.time() + args.seconds
        while time.time() < deadline:
            msg = await ws.recv()
            if isinstance(msg, bytes):
                sid, flags, seq, index, t0, data = decode(msg)
                if streams[sid]["key"] == "eeg.raw":
                    raw_samples += data.shape[0]
            else:
                m = json.loads(msg)
                if m["type"] == "data" and m["stream"] == "bandpower":
                    last_bp = m["data"][0]
                elif m["type"] == "gap":
                    print(f"gap in {m['stream']}: {m['count']} samples ({m['reason']})")
                elif m["type"] == "error":
                    print("server error:", m["message"])
            if time.time() - last_print >= 1:
                alpha = None if last_bp is None or last_bp[7] is None else round(last_bp[7] * 100)
                print(f"raw samples so far: {raw_samples:6d}   AFz relative alpha: {alpha}%")
                last_print = time.time()
        print("done:", "ok" if raw_samples > 0 or last_bp is not None else "no data received")


if __name__ == "__main__":
    p = argparse.ArgumentParser()
    p.add_argument("--host", default="localhost")
    p.add_argument("--port", type=int, default=8765)
    p.add_argument("--code", help="6-digit pairing code from Lucid-Stream")
    p.add_argument("--token", help="paired token from an earlier run in the same streaming session")
    p.add_argument("--seconds", type=float, default=10)
    a = p.parse_args()
    if not (a.code or a.token):
        p.error("give --code or --token")
    asyncio.run(main(a))