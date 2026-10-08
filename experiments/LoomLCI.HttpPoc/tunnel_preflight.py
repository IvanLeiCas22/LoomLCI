"""A3.2: offline Secure MCP Tunnel HTTP profile preflight.

This is NOT a ChatGPT/end-to-end test: it uses a synthetic tunnel ID and
a dummy runtime credential so no connection to the real OpenAI control plane
is attempted. All temporary profiles and the HTTP Host are deleted/stopped.
No secret is printed or written into the tunnel profile.
"""
import argparse
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


def subprocess_flags():
    return getattr(subprocess, "CREATE_NO_WINDOW", 0)


def call_health(port, token):
    req = Request(
        f"http://127.0.0.1:{port}/health",
        headers={"Authorization": "Bearer " + token},
    )
    with urlopen(req, timeout=1) as reply:
        return reply.status == 200


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--tunnel-client", type=Path, required=True)
    args = parser.parse_args()
    exe = args.tunnel_client.resolve()
    dll = (
        Path(__file__).resolve().parent
        / "bin"
        / "Release"
        / "net10.0-windows10.0.19041.0"
        / "LoomLCI.HttpPoc.dll"
    )
    if not exe.is_file() or not dll.is_file():
        raise RuntimeError("Missing tunnel-client executable or Release HTTP PoC build.")

    with tempfile.TemporaryDirectory(prefix="loomlci-a32-offline-") as folder:
        root = Path(folder)
        with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as sock:
            sock.bind(("127.0.0.1", 0))
            port = sock.getsockname()[1]

        token = secrets.token_urlsafe(48)
        env = dict(os.environ)
        env.update(
            {
                "LOOMLCI_HTTP_POC_PORT": str(port),
                "LOOMLCI_HTTP_POC_TOKEN": token,
                "ASPNETCORE_ENVIRONMENT": "Production",
                "TUNNEL_CLIENT_PROFILE_DIR": str(root / "profiles"),
                "TUNNEL_CLIENT_STATE_DIR": str(root / "state"),
                "A32_LOCAL_BEARER": "Bearer " + token,
                "A32_TEST_RUNTIME_KEY": "not-a-real-openai-runtime-key",
                "MCP_EXTRA_HEADERS": "Authorization: env:A32_LOCAL_BEARER",
                "MCP_DISCOVERY_EXTRA_HEADERS": "Authorization: env:A32_LOCAL_BEARER",
            }
        )
        host = subprocess.Popen(
            ["dotnet", str(dll)],
            cwd=str(dll.parent),
            env=env,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            creationflags=subprocess_flags(),
        )
        try:
            for _ in range(70):
                if host.poll() is not None:
                    raise RuntimeError("HTTP PoC Host exited prematurely.")
                try:
                    if call_health(port, token):
                        break
                except (URLError, HTTPError, OSError, TimeoutError):
                    pass
                time.sleep(0.15)
            else:
                raise TimeoutError("HTTP PoC Host not ready.")

            profile = "a32-offline-http"
            init_args = [
                str(exe), "init", "--sample", "sample_mcp_with_dcr",
                "--profile", profile,
                "--profile-dir", str(root / "profiles"),
                "--tunnel-id", "tunnel_" + secrets.token_hex(16),
                "--mcp-server-url", f"http://127.0.0.1:{port}/mcp",
                "--control-plane-api-key-ref", "env:A32_TEST_RUNTIME_KEY",
                "--health-listen-addr", "127.0.0.1:0",
                "--force",
            ]
            initialized = subprocess.run(
                init_args,
                env=env,
                capture_output=True,
                text=True,
                timeout=12,
                creationflags=subprocess_flags(),
            )
            if initialized.returncode:
                raise RuntimeError("tunnel-client init failed (exit code only): "
                                   + str(initialized.returncode))

            config = (root / "profiles" / (profile + ".yaml")).read_text("utf-8")
            assert token not in config, "Token must not be present in profile."
            assert "env:A32_TEST_RUNTIME_KEY" in config
            assert f"http://127.0.0.1:{port}/mcp" in config
            print("PASS independent HTTP profile without persistent secrets")

            doctor = subprocess.run(
                [str(exe), "doctor", "--profile", profile, "--profile-dir",
                 str(root / "profiles"), "--explain", "--json"],
                env=env,
                capture_output=True,
                text=True,
                timeout=25,
                creationflags=subprocess_flags(),
            )
            if not doctor.stdout:
                raise RuntimeError("tunnel-client doctor returned no JSON output.")
            report = json.loads(doctor.stdout)
            checks = {row["id"]: row["status"] for row in report["checks"]}
            for check in ("config_source", "profile_load", "mcp_target",
                          "mcp_server_reachable", "oauth_metadata",
                          "health_listener"):
                assert checks.get(check) == "PASS", (check, checks.get(check))
            assert doctor.returncode == 0 and report["result"] == "ok"
            print("PASS tunnel-client doctor (local configuration and discovery)")
            print("NOTE remote tunnel registration, runtime API key validity, "
                  "polling, and ChatGPT parallelism NOT tested")
        finally:
            host.terminate()
            try:
                host.wait(timeout=6)
            except subprocess.TimeoutExpired:
                host.kill()
                host.wait(timeout=6)
            assert host.poll() is not None, "Experimental Host still running."
            print("PASS isolated HTTP Host stopped; temp profile cleaned on exit")


if __name__ == "__main__":
    main()
