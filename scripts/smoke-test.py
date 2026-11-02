#!/usr/bin/env python3
"""Test the real receiver in an isolated directory and on a free loopback port."""
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

binary = Path(sys.argv[1]).resolve()
with tempfile.TemporaryDirectory(prefix="routemeter-smoke-") as directory:
    root = Path(directory)
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    key, token = secrets.token_hex(32), secrets.token_hex(32)
    (root / "config.json").write_text(json.dumps({"key": key, "scriptToken": token, "port": port}))
    environment = dict(os.environ, ROUTEMETER_DATA_DIR=directory)
    process = subprocess.Popen([str(binary), "--headless"], env=environment, stdout=subprocess.DEVNULL, stderr=subprocess.PIPE)
    base = f"http://127.0.0.1:{port}"
    opener = urllib.request.build_opener(urllib.request.ProxyHandler({}))

    def request(path, data=None, headers=None, expected=200):
        req = urllib.request.Request(base+path, data=data, headers=headers or {})
        try:
            with opener.open(req, timeout=3) as response:
                assert response.status == expected, response.status
                return response.read()
        except urllib.error.HTTPError as error:
            assert error.code == expected, (path, error.code, expected)
            return b""

    try:
        for _ in range(100):
            if process.poll() is not None:
                raise RuntimeError(process.stderr.read().decode())
            try:
                request("/config.json", expected=404)
                break
            except urllib.error.URLError:
                time.sleep(0.1)
        else:
            raise RuntimeError("Receiver did not start")
        request("/statistics-push.js", expected=404)
        script = request(f"/bridge/{token}/statistics-push.js")
        assert key.encode() in script and b"__LOCAL_KEY__" not in script
        assert b"console.log" not in script
        request("/statistics", b"{}", expected=403)
        headers = {"X-Route-Meter-Key": key, "Content-Type": "application/json"}
        request("/statistics", b"invalid", headers, expected=400)
        request("/statistics", b"{}", dict(headers, Origin="https://example.invalid"), expected=400)
        request("/statistics", b"{}", dict(headers, Host="attacker.invalid"), expected=403)

        def sample(counter, stamp):
            e = lambda name, down, up: dict(name=name,type="tcp",rx_transfer=down,tx_transfer=up,rx_current_rate=10,tx_current_rate=20)
            return json.dumps(dict(version=1,timestamp=stamp,statistics=[e("direct",counter,counter),e("Test node",counter*2,counter)])).encode()

        stamp = time.time()*1000
        request("/statistics", sample(100,stamp), headers, expected=204)
        request("/statistics", sample(110,stamp+1), headers, expected=204)
        for _ in range(100):
            state = root / "state.json"
            if state.exists():
                saved = json.loads(state.read_text())["traffic"]
                if saved["directTotal"]["down"] == 10:
                    break
            time.sleep(0.02)
        else:
            raise AssertionError("Real receiver did not persist classified totals")
        assert saved["proxyTotal"] == {"down":20,"up":10}
        assert root.stat().st_mode & 0o777 == 0o700
        assert (root/"config.json").stat().st_mode & 0o777 == 0o600
        assert state.stat().st_mode & 0o777 == 0o600
        print("PASS real HTTP receiver, resource token, authorization, validation, classified persistence, private permissions")
    finally:
        process.terminate()
        try:
            process.wait(timeout=5)
        except subprocess.TimeoutExpired:
            process.kill()
