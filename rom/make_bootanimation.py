"""Gera a animação de boot do TabletMonitor (bootanimation.zip, formato Android: PNGs + desc.txt, sem compressão)."""
import io, math, sys, zipfile
from PIL import Image, ImageDraw

S = 720          # frames quadrados, centralizados na tela pelo Android
FPS = 30
BG = (0, 0, 0)
FG = (232, 238, 242)
ACC = (61, 220, 151)

def logo(d, scale, alpha):
    """Monitor + tablet (mesmo desenho do ícone), centralizado."""
    def c(col): return tuple(int(BG[i] + (col[i] - BG[i]) * alpha) for i in range(3))
    k = scale
    cx, cy = S / 2, S / 2 - 40
    def R(x0, y0, x1, y1): return [cx + x0 * k, cy + y0 * k, cx + x1 * k, cy + y1 * k]
    d.rounded_rectangle(R(-36, -24, 18, 12), radius=4 * k, outline=c(FG), width=max(2, int(4 * k)))
    d.rectangle(R(-16, 12, -2, 20), fill=c(FG))
    d.rectangle(R(-22, 20, 4, 24), fill=c(FG))
    d.rounded_rectangle(R(6, -12, 34, 30), radius=4 * k, fill=c(ACC))
    d.rectangle(R(9, -7, 31, 24), fill=BG)

def frame(i, total, part):
    im = Image.new("RGB", (S, S), BG)
    d = ImageDraw.Draw(im)
    if part == 0:  # entrada: aparece e cresce levemente
        t = i / (total - 1)
        e = 1 - (1 - t) ** 3
        logo(d, 4.0 + 0.6 * e, e)
    else:          # carregando: barrinha que vai e volta
        logo(d, 4.6, 1.0)
        t = i / total
        w, h, y = 260, 6, S / 2 + 150
        x0 = S / 2 - w / 2
        d.rounded_rectangle([x0, y, x0 + w, y + h], radius=3, fill=(30, 36, 42))
        seg = 70
        pos = (math.sin(t * 2 * math.pi - math.pi / 2) + 1) / 2 * (w - seg)
        d.rounded_rectangle([x0 + pos, y, x0 + pos + seg, y + h], radius=3, fill=ACC)
    buf = io.BytesIO(); im.save(buf, "PNG", optimize=True); return buf.getvalue()

out = sys.argv[1]
with zipfile.ZipFile(out, "w", zipfile.ZIP_STORED) as z:
    z.writestr("desc.txt", f"{S} {S} {FPS}\np 1 0 part0\np 0 0 part1\n")
    for i in range(24): z.writestr(f"part0/{i:03d}.png", frame(i, 24, 0))
    for i in range(36): z.writestr(f"part1/{i:03d}.png", frame(i, 36, 1))
print("ok", out)
