r"""
Servidor de teste local com suporte a Range e limite de velocidade POR CONEXÃO.
Serve para você testar o FastDl sem depender de internet:

    python test_server.py                # 100 MB, 1 MB/s por conexão
    python test_server.py 200 2          # 200 MB, 2 MB/s por conexão

Depois:
    dotnet run -- http://localhost:8000/teste.bin -c 8 -o .\saida

Com 1 MB/s por conexão, 1 conexão leva ~100 s e 8 conexões ~13 s.
Use  python test_server.py --no-range  para simular um servidor sem Range.
"""
import os, re, sys, time, hashlib
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

args = [a for a in sys.argv[1:] if not a.startswith("--")]
NO_RANGE = "--no-range" in sys.argv
SIZE_MB = int(args[0]) if len(args) > 0 else 100
MBPS = float(args[1]) if len(args) > 1 else 1.0
PATH = "teste.bin"
CHUNK = 64 * 1024

if not os.path.exists(PATH) or os.path.getsize(PATH) != SIZE_MB * 1024 * 1024:
    print(f"Gerando {PATH} ({SIZE_MB} MB)...")
    with open(PATH, "wb") as f:
        for _ in range(SIZE_MB):
            f.write(os.urandom(1024 * 1024))

h = hashlib.sha256()
with open(PATH, "rb") as f:
    for block in iter(lambda: f.read(1 << 20), b""):
        h.update(block)
print("SHA-256 do original:", h.hexdigest())

FILE_SIZE = os.path.getsize(PATH)
ETAG = '"' + h.hexdigest()[:16] + '"'


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def do_GET(self):
        if self.path.split("?")[0] != "/" + PATH:
            self.send_error(404)
            return

        start, end, partial = 0, FILE_SIZE - 1, False
        rng = self.headers.get("Range")
        if rng and not NO_RANGE:
            m = re.match(r"bytes=(\d*)-(\d*)", rng)
            if m:
                a, b = m.groups()
                if a == "":
                    start = max(0, FILE_SIZE - int(b))
                else:
                    start = int(a)
                    if b != "":
                        end = min(int(b), FILE_SIZE - 1)
                if start >= FILE_SIZE or start > end:
                    self.send_response(416)
                    self.send_header("Content-Range", f"bytes */{FILE_SIZE}")
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                partial = True

        length = end - start + 1
        self.send_response(206 if partial else 200)
        if not NO_RANGE:
            self.send_header("Accept-Ranges", "bytes")
        if partial:
            self.send_header("Content-Range", f"bytes {start}-{end}/{FILE_SIZE}")
        self.send_header("Content-Length", str(length))
        self.send_header("Content-Type", "application/octet-stream")
        self.send_header("ETag", ETAG)
        self.end_headers()

        delay = CHUNK / (MBPS * 1024 * 1024)
        try:
            with open(PATH, "rb") as f:
                f.seek(start)
                left = length
                while left > 0:
                    data = f.read(min(CHUNK, left))
                    self.wfile.write(data)
                    left -= len(data)
                    time.sleep(delay)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass


print(f"Servindo http://localhost:8000/{PATH}  ({SIZE_MB} MB, {MBPS} MB/s por conexão"
      f"{', SEM Range' if NO_RANGE else ''})")
ThreadingHTTPServer(("127.0.0.1", 8000), Handler).serve_forever()
