# Cliente falso que imita o app Android (protocolo v2): handshake, recebe vídeo, manda toques e ping.
#   python3 fake_tablet.py HOST PORTA SAIDA.h264 [limite_kBps]
# Com limite, simula um link lento (Bluetooth) para testar o descarte de quadros.
import socket, struct, sys, time
host, port, out = sys.argv[1], int(sys.argv[2]), sys.argv[3]
limit = float(sys.argv[4]) * 1024 if len(sys.argv) > 4 else 0
if not limit:
    u = socket.socket(socket.AF_INET, socket.SOCK_DGRAM); u.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    u.bind(("", 27184)); u.settimeout(3)
    try: print("beacon:", u.recvfrom(256)[0].decode())
    except Exception as e: print("beacon: nenhum", e)
s = socket.socket()
if limit: s.setsockopt(socket.SOL_SOCKET, socket.SO_RCVBUF, 16384)
s.connect((host, port)); s.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
name = b"Fake SM-T520"
payload = struct.pack(">BHHHHH", 2, 2560, 1600, 320, 1920, 1200) + name
s.sendall(b"TMON" + struct.pack(">BI", 0, len(payload)) + payload)
got = 0; t_start = time.time()
def rx(n):
    global got
    b = b""
    while len(b) < n:
        c = s.recv(min(n - len(b), 4096))
        if not c: raise EOFError
        b += c; got += len(c)
        if limit:
            ahead = got / limit - (time.time() - t_start)
            if ahead > 0: time.sleep(ahead)
    return b
f = open(out, "wb"); frames = keys = 0; t0 = time.time(); lags = []; first = None
sent_touch = False
while time.time() - t0 < (8 if limit else 4):
    t, l = struct.unpack(">BI", rx(5)); p = rx(l)
    if t == 1: print("CONFIG", struct.unpack(">HHH", p[:6]), "codec", p[6] if len(p) > 6 else 0)
    elif t == 2: print("CSD", len(p), "bytes"); f.write(p)
    elif t == 3:
        flags, pts = struct.unpack(">BQ", p[:9]); f.write(p[9:]); frames += 1; keys += flags & 1
        now = time.time()
        if first is None: first = (now, pts)
        lags.append((now - first[0]) - (pts - first[1]) / 1e6)
    elif t == 0x21: print("PONG", struct.unpack(">Q", p)[0])
    if frames == 10 and not sent_touch:
        sent_touch = True
        for a, x, y in [(0, .25, .5), (1, .30, .55), (2, .30, .55)]:
            s.sendall(struct.pack(">BIBBHH", 0x10, 6, a, 0, int(x * 65535), int(y * 65535)))
        s.sendall(struct.pack(">BIQ", 0x20, 8, 123456789))
f.close(); s.close()
n = len(lags)
print(f"frames={frames} keyframes={keys} atraso acumulado: início={lags[min(5,n-1)]*1000:.0f}ms meio={lags[n//2]*1000:.0f}ms fim={lags[-1]*1000:.0f}ms")
