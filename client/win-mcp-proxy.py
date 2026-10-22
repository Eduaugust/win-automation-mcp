#!/usr/bin/env python3
"""
win-mcp-proxy: stdio <-> `ssh -W` bridge for win-automation-mcp that survives tunnel drops.

Use it as the MCP server command in Claude Code instead of a bare `ssh -W ...`:

    claude mcp add windows-automation -- python3 /path/to/win-mcp-proxy.py --host win-vm --port 8765

Behaviour
  * ssh runs with ServerAliveInterval/CountMax so a dead tunnel is noticed within about a minute.
  * If the tunnel drops (VM slept or rebooted, network blip), requests that were in flight get a
    JSON-RPC error immediately (so the client does not hang), the proxy reconnects with backoff and
    replays the cached `initialize` request silently, then keeps forwarding. The client never sees
    "Connection closed".
  * Requests that arrive while disconnected wait up to --connect-timeout seconds for a new tunnel.
The server keeps no per-connection state, so replaying initialize is all that a new connection needs.
"""
import argparse, json, subprocess, sys, threading, time

ap = argparse.ArgumentParser()
ap.add_argument("--host", default="win-vm")
ap.add_argument("--port", type=int, default=8765)
ap.add_argument("--connect-timeout", type=float, default=60.0)
ap.add_argument("--log", default=None, help="optional log file")
args = ap.parse_args()

lock = threading.Lock()
out_lock = threading.Lock()
proc = None            # current ssh process
generation = 0         # increments on each (re)connect
pending = {}           # id (json string) -> generation it was sent on
init_req = None        # cached initialize request (dict)
swallow = set()        # ids of replayed requests whose response must not reach the client
connected = threading.Condition(lock)

def log(msg):
    line = time.strftime("%Y-%m-%d %H:%M:%S ") + msg
    if args.log:
        with open(args.log, "a") as f: f.write(line + "\n")
    else:
        print(line, file=sys.stderr, flush=True)

def emit(obj):
    with out_lock:
        sys.stdout.write(json.dumps(obj) + "\n"); sys.stdout.flush()

def spawn():
    return subprocess.Popen(
        ["ssh", "-o", "BatchMode=yes", "-o", "ServerAliveInterval=15", "-o", "ServerAliveCountMax=4",
         "-o", "ConnectTimeout=10", "-W", f"127.0.0.1:{args.port}", args.host],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, bufsize=0)

def reader(p, gen):
    buf = b""
    while True:
        chunk = p.stdout.read(65536)
        if not chunk: break
        buf += chunk
        while b"\n" in buf:
            line, buf = buf.split(b"\n", 1)
            if not line.strip(): continue
            try: msg = json.loads(line)
            except Exception: continue
            mid = json.dumps(msg.get("id")) if "id" in msg and "method" not in msg else None
            with lock:
                if mid is not None:
                    pending.pop(mid, None)
                    if mid in swallow:
                        swallow.discard(mid); continue
            emit(msg)
    # EOF: tunnel is gone
    with lock:
        global proc
        if proc is p: proc = None
        dead = [k for k, g in pending.items() if g == gen]
        for k in dead: pending.pop(k, None)
    for k in dead:
        emit({"jsonrpc": "2.0", "id": json.loads(k), "error": {"code": -32000, "message": "Connection to the Windows VM was lost; the proxy is reconnecting. Retry the call."}})
    log(f"tunnel gen {gen} closed; {len(dead)} in-flight request(s) failed")

def ensure_connected(deadline):
    """Return the live ssh process, (re)connecting with backoff until deadline."""
    global proc, generation
    delay = 1.0
    while True:
        with lock:
            if proc is not None and proc.poll() is None: return proc
        try:
            p = spawn()
        except Exception as e:
            log(f"spawn failed: {e}"); p = None
        if p is not None:
            time.sleep(0.6)
            if p.poll() is None:
                with lock:
                    generation += 1; gen = generation; proc = p
                threading.Thread(target=reader, args=(p, gen), daemon=True).start()
                log(f"tunnel gen {gen} up")
                if init_req is not None:
                    rid = json.dumps(init_req.get("id"))
                    with lock: swallow.add(rid)
                    try:
                        p.stdin.write((json.dumps(init_req) + "\n").encode())
                        p.stdin.write((json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n").encode())
                    except Exception: pass
                return p
        if time.time() + delay > deadline: return None
        time.sleep(delay); delay = min(delay * 2, 10)

def main():
    global init_req
    ensure_connected(time.time() + args.connect_timeout)
    for raw in sys.stdin.buffer:
        raw = raw.strip()
        if not raw: continue
        try: msg = json.loads(raw)
        except Exception: continue
        if msg.get("method") == "initialize": init_req = msg
        rid = json.dumps(msg["id"]) if "id" in msg and "method" in msg else None
        p = ensure_connected(time.time() + args.connect_timeout)
        if p is None:
            if rid is not None:
                emit({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32000, "message": f"Cannot reach the Windows VM through ssh {args.host} within {args.connect_timeout:.0f}s"}})
            continue
        with lock:
            if rid is not None: pending[rid] = generation
        try:
            p.stdin.write(raw + b"\n")
        except Exception as e:
            log(f"write failed: {e}")
            with lock: pending.pop(rid, None) if rid else None
            if rid is not None:
                emit({"jsonrpc": "2.0", "id": msg["id"], "error": {"code": -32000, "message": "Connection to the Windows VM was lost while sending; retry the call."}})
    if proc is not None:
        try: proc.stdin.close()
        except Exception: pass

main()
