"""A3.1 HTTP smoke: isolated, local-only, without extra packages or persistent secrets.

Usage:
    python smoke.py --baseline-host "C:\\...\\LoomLCI.Host.exe"
The HTTP PoC must be built with dotnet build -c Release first.
"""
import argparse
import concurrent.futures
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


def request(url, *, token=None, body=None, headers=None, timeout=15):
    h = dict(headers or {})
    if token is not None:
        h["Authorization"] = "Bearer " + token
    if body is not None:
        h.update({"Content-Type": "application/json", "Accept": "application/json, text/event-stream",
                  "MCP-Protocol-Version": "2025-11-25"})
    req = Request(url, data=json.dumps(body).encode("utf-8") if body is not None else None, headers=h,
                  method="POST" if body is not None else "GET")
    try:
        with urlopen(req, timeout=timeout) as resp:
            payload = resp.read().decode("utf-8", "replace")
            mime = resp.headers.get("Content-Type", "")
            if mime.startswith("text/event-stream"):
                events = [json.loads(line[6:]) for line in payload.splitlines() if line.startswith("data: ")]
                return resp.status, events[-1] if events else None
            return resp.status, json.loads(payload)
    except HTTPError as exc:
        return exc.code, None


def stdio_contract(executable):
    proc = subprocess.Popen([str(executable)], cwd=str(executable.parent), stdin=subprocess.PIPE,
                            stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    try:
        def exchange(message):
            proc.stdin.write((json.dumps(message) + "\n").encode("utf-8"))
            proc.stdin.flush()
            while True:
                packet = json.loads(proc.stdout.readline())
                if packet.get("id") == message["id"]:
                    assert "error" not in packet, packet.get("error")
                    return packet

        exchange({"jsonrpc": "2.0", "id": 1, "method": "initialize",
                  "params": {"protocolVersion": "2025-11-25", "capabilities": {},
                             "clientInfo": {"name": "a31-stdio-comparator", "version": "1"}}})
        proc.stdin.write(b'{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}\n')
        proc.stdin.flush()
        return exchange({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})["result"]["tools"]
    finally:
        proc.stdin.close()
        try:
            proc.wait(timeout=5)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=5)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline-host", type=Path,
                        help="Optional installed STDIO Host for exact tool contract comparison")
    opts = parser.parse_args()
    dll = Path(__file__).resolve().parent / "bin" / "Release" / "net10.0-windows10.0.19041.0" / "LoomLCI.HttpPoc.dll"
    if not dll.is_file():
        raise RuntimeError("Build the HTTP PoC in Release first.")
    if opts.baseline_host is not None and not opts.baseline_host.is_file():
        raise RuntimeError("Baseline STDIO Host not found.")

    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(("127.0.0.1", 0))
        port = s.getsockname()[1]
    token = secrets.token_urlsafe(48)
    root = f"http://127.0.0.1:{port}"
    env = dict(os.environ, LOOMLCI_HTTP_POC_TOKEN=token,
               LOOMLCI_HTTP_POC_PORT=str(port), ASPNETCORE_ENVIRONMENT="Production")
    proc = subprocess.Popen(["dotnet", str(dll)], cwd=str(dll.parent), env=env,
                            stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0))
    work_id = None
    next_id = 0

    def call(method, params=None):
        nonlocal next_id
        next_id += 1
        status, payload = request(root + "/mcp", token=token,
                                  body={"jsonrpc": "2.0", "id": next_id,
                                        "method": method, "params": params or {}}, timeout=16)
        assert status == 200 and payload and "result" in payload, (method, status, payload)
        return payload["result"]

    try:
        start = time.monotonic()
        while time.monotonic() - start < 12:
            assert proc.poll() is None, "Experimental Host exited prematurely"
            try:
                if request(root + "/health", token=token, timeout=1)[0] == 200:
                    break
            except (URLError, OSError, TimeoutError):
                pass
            time.sleep(0.15)
        else:
            raise AssertionError("HTTP Host did not become healthy")

        assert request(root + "/health")[0] == 401
        assert request(root + "/health", token="incorrect" * 8)[0] == 401
        assert request(root + "/health", token=token, headers={"Origin": "https://evil.example"})[0] == 403
        assert request(root + "/health", token=token, headers={"Host": f"evil.example:{port}"})[0] == 403
        assert request(root + "/health", token=token)[0] == 200
        assert request(root + "/mcp", token="incorrect" * 8,
                       body={"jsonrpc": "2.0", "id": 1, "method": "tools/list", "params": {}})[0] == 401
        print("PASS security (token, Origin, Host, health, MCP)")

        init = call("initialize", {"protocolVersion": "2025-11-25", "capabilities": {},
                                   "clientInfo": {"name": "a31-smoke", "version": "1"}})
        assert init["protocolVersion"] == "2025-11-25"
        tools = call("tools/list")["tools"]
        assert len(tools) == 25 and len({t["name"] for t in tools}) == 25
        if opts.baseline_host:
            baseline = {t["name"]: t for t in stdio_contract(opts.baseline_host)}
            assert baseline == {t["name"]: t for t in tools}, "STDIO and HTTP contracts differ"
            print("PASS exact parity with installed STDIO host (25 tools)")
        else:
            print("PASS HTTP MCP initialization and 25 tools (no baseline comparison)")

        created = call("tools/call", {"name": "work_create", "arguments": {
            "baseDirectory": str(Path(__file__).resolve().parents[2]), "label": "A3.1 smoke read-only"}})
        work_id = created["structuredContent"]["result"]["workId"]
        assert work_id
        upd = call("tools/call", {"name": "work_plan_update", "arguments": {
            "workId": work_id, "expectedRevision": 0,
            "steps": [{"text": "A3.1 stateless persistence", "status": "active"}]}})
        assert upd["structuredContent"]["result"]["revision"] == 1
        plan = call("tools/call", {"name": "work_plan_get", "arguments": {"workId": work_id}})
        assert plan["structuredContent"]["result"]["revision"] == 1
        read = call("tools/call", {"name": "filesystem_read_files", "arguments": {
            "workId": work_id, "files": [{"path": "global.json", "limit": 8}]}})
        assert read["structuredContent"]["ok"] is True
        print("PASS cross-request WorkSession, Work Plan and Filesystem")

        def job(i):
            status, packet = request(root + "/mcp", token=token,
                                     body={"jsonrpc": "2.0", "id": 300 + i, "method": "tools/call",
                                           "params": {"name": "process_run", "arguments": {
                                               "workId": work_id, "executable": "powershell.exe",
                                               "arguments": ["-NoProfile", "-NonInteractive", "-Command",
                                                             f"Start-Sleep -Milliseconds 1200; Write-Output A31_{i}_OK"],
                                               "timeoutSeconds": 12, "maxOutputChars": 160}}}, timeout=16)
            assert status == 200 and packet is not None
            out = packet["result"]["structuredContent"]
            assert out["ok"] and out["result"]["exitCode"] == 0
            assert out["result"]["stdout"].strip() == f"A31_{i}_OK"
            return out["result"]

        t0 = time.monotonic()
        seq = [job(i) for i in (1, 2, 3)]
        seq_elapsed = time.monotonic() - t0
        t0 = time.monotonic()
        with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
            par = list(pool.map(job, (4, 5, 6)))
        par_elapsed = time.monotonic() - t0
        assert all(item["exitCode"] == 0 for item in seq + par)
        assert par_elapsed < seq_elapsed * 0.8, (seq_elapsed, par_elapsed)
        print(f"PASS concurrency: serial={seq_elapsed:.3f}s concurrent={par_elapsed:.3f}s")
    finally:
        if work_id:
            try:
                closed = call("tools/call", {"name": "work_close", "arguments": {"workId": work_id}})
                assert closed["structuredContent"]["ok"] is True
                print("PASS work_close")
            except Exception as exc:
                print("CLEANUP_WARNING", type(exc).__name__, str(exc)[:180])
        proc.terminate()
        try:
            proc.wait(timeout=6)
        except subprocess.TimeoutExpired:
            proc.kill()
            proc.wait(timeout=6)
        print("PASS isolated host stopped" if proc.poll() is not None else "FAIL host still running")


if __name__ == "__main__":
    main()
