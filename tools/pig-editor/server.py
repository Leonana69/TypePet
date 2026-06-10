#!/usr/bin/env python3
"""Pig pose editor.

Serves an interactive editor (drag the pig's parts or type offsets) and, on Save,
renders the posed frame through the REAL chargen rasterizer (`--pig-pose`), so the
PNG is pixel-identical to what the asset pipeline produces. Outputs land in ./out/
as <name>.png + <name>.json (the pose spec, reloadable via the Saved-poses panel).
Keepers go into Assets/DefaultCharacters/Pig: copy the PNG into Body/ and add a frame
entry to manifest.json using the crop placement the save log prints.

Run:  python server.py [port]     (default port 8631; chargen is built once at startup)
Note: restart the server after editing tools/chargen so saves use the fresh build.
"""
import json
import re
import subprocess
import sys
import time
from http.server import BaseHTTPRequestHandler, HTTPServer
from pathlib import Path

HERE = Path(__file__).resolve().parent
CHARGEN = HERE.parent / "chargen"
OUT = HERE / "out"
PORT = int(sys.argv[1]) if len(sys.argv) > 1 else 8631


def build_chargen():
    print("building chargen ...")
    r = subprocess.run(
        ["dotnet", "build", str(CHARGEN / "chargen.csproj"), "-v", "q", "--nologo"],
        capture_output=True, text=True)
    if r.returncode != 0:
        print(r.stdout)
        print(r.stderr)
        sys.exit("chargen build failed")


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        path = self.path.split("?")[0]  # ignore query strings (e.g. the editor's ?debug=anchors)
        if path in ("/", "/index.html"):
            self._send_file(HERE / "index.html", "text/html; charset=utf-8")
        elif path == "/poses":
            # saved pose specs in out/ (newest first) for the editor's load dropdown
            files = sorted(OUT.glob("*.json"), key=lambda p: p.stat().st_mtime, reverse=True)
            self._send_json({"poses": [p.stem for p in files]})
        elif path.startswith("/out/"):
            name = Path(path[len("/out/"):]).name  # basename only - no traversal
            p = OUT / name
            if p.is_file():
                ctype = "image/png" if p.suffix == ".png" else "application/json"
                self._send_file(p, ctype)
            else:
                self.send_error(404)
        else:
            self.send_error(404)

    def do_POST(self):
        if self.path != "/save":
            self.send_error(404)
            return
        try:
            length = int(self.headers.get("Content-Length", 0))
            pose = json.loads(self.rfile.read(length))
            name = re.sub(r"[^A-Za-z0-9_-]", "_", str(pose.pop("name", "pose"))) or "pose"

            OUT.mkdir(exist_ok=True)
            jpath = OUT / f"{name}.json"
            ppath = OUT / f"{name}.png"
            jpath.write_text(json.dumps(pose, indent=2))

            r = subprocess.run(
                ["dotnet", "run", "--project", str(CHARGEN), "--no-build", "--",
                 "--pig-pose", str(jpath), str(ppath)],
                capture_output=True, text=True)
            ok = r.returncode == 0 and ppath.is_file()
            self._send_json({
                "ok": ok,
                "png": f"/out/{name}.png?t={int(time.time() * 1000)}",
                "json": f"/out/{name}.json",
                "log": (r.stdout + r.stderr).strip(),
            }, 200 if ok else 500)
        except Exception as e:  # report rather than drop the connection
            self._send_json({"ok": False, "log": f"{type(e).__name__}: {e}"}, 500)

    def _send_file(self, path, ctype):
        body = path.read_bytes()
        self.send_response(200)
        self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def _send_json(self, obj, status=200):
        body = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, fmt, *args):  # keep the console to save events only
        if self.command == "POST":
            super().log_message(fmt, *args)


if __name__ == "__main__":
    build_chargen()
    OUT.mkdir(exist_ok=True)
    print(f"pig editor -> http://localhost:{PORT}")
    HTTPServer(("127.0.0.1", PORT), Handler).serve_forever()
