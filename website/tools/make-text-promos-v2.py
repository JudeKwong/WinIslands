# -*- coding: utf-8 -*-
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageChops
import numpy as np

ROOT = Path(r'E:\MyFiles\Program\WinIsland')
BASE = ROOT / 'build' / 'ui-capture-316beta3' / 'roundtrip-latest'
OUT = ROOT / 'website' / 'assets' / 'promos'
OUT.mkdir(parents=True, exist_ok=True)

FONT_UI = r'C:\Windows\Fonts\NotoSansSC-VF.ttf'
FONT_SEGOE = r'C:\Windows\Fonts\segoeui.ttf'
FONT_SEGOE_SB = r'C:\Windows\Fonts\seguisb.ttf'
FONT_SEGOE_B = r'C:\Windows\Fonts\segoeuib.ttf'
FONT_MONO = r'C:\Windows\Fonts\consola.ttf'
FONT_MONO_B = r'C:\Windows\Fonts\consolab.ttf'
SS = 2

EXPANDED = BASE / 'roundtrip-3-expanded.png'
COLLAPSED = BASE / 'roundtrip-3-collapsed.png'


def font(path, size, weight=None):
    f = ImageFont.truetype(path, int(size * SS))
    if weight is not None:
        try:
            f.set_variation_by_axes([weight])
        except Exception:
            pass
    return f


def make_bg(w, h, glows, top=(6, 8, 15), bottom=(10, 14, 26), seed=31):
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    t = yy / max(1, h - 1)
    c0 = np.array(top, dtype=np.float32)
    c1 = np.array(bottom, dtype=np.float32)
    rgb = c0[None, None, :] * (1 - t[:, :, None]) + c1[None, None, :] * t[:, :, None]

    # Very soft vertical sheen, like a dark studio backdrop.
    edge = np.abs(xx / max(1, w - 1) - 0.5) * 2.0
    rgb *= (1.03 - 0.055 * edge[:, :, None])

    nx = xx / max(1, w - 1)
    ny = yy / max(1, h - 1)
    for cx, cy, rx, ry, color, strength, power in glows:
        dist = ((nx - cx) / rx) ** 2 + ((ny - cy) / ry) ** 2
        field = np.exp(-dist * 2.1) ** power
        rgb += np.array(color, dtype=np.float32)[None, None, :] * field[:, :, None] * strength

    # Gentle vignette keeps the edges quiet and the object luminous.
    vx = (nx - 0.5) / 0.72
    vy = (ny - 0.48) / 0.72
    vign = np.clip(1.0 - (vx * vx + vy * vy) * 0.46, 0.0, 1.0) ** 1.35
    rgb *= (0.78 + 0.22 * vign[:, :, None])

    rng = np.random.default_rng(seed)
    grain = rng.normal(0, 1.05, (h, w, 1)).astype(np.float32)
    rgb += grain
    return Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), 'RGB').convert('RGBA')


def load_island(path, crop=None, scale=1.0, sharpen=True):
    im = Image.open(path).convert('RGBA')
    if crop:
        im = im.crop(crop)
    if scale != 1.0:
        im = im.resize((max(1, round(im.width * scale)), max(1, round(im.height * scale))), Image.LANCZOS)
        if sharpen:
            im = im.filter(ImageFilter.UnsharpMask(radius=1.15, percent=72, threshold=3))
    return im


def add_shadow_and_glow(canvas, im, pos, glow=(96, 116, 255), glow_alpha=52, shadow=True):
    alpha = im.getchannel('A')
    if glow_alpha:
        gm = alpha.filter(ImageFilter.GaussianBlur(max(24, im.width // 26)))
        layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        layer.paste(Image.new('RGBA', im.size, (*glow, glow_alpha)), pos, gm)
        canvas.alpha_composite(layer)
    if shadow:
        sm = alpha.filter(ImageFilter.GaussianBlur(max(12, im.width // 100)))
        layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        layer.paste(Image.new('RGBA', im.size, (0, 0, 0, 185)), (pos[0], pos[1] + max(12, im.height // 32)), sm)
        canvas.alpha_composite(layer)
    canvas.alpha_composite(im, pos)


def edge_highlight(canvas, im, pos, alpha=34):
    a = im.getchannel('A')
    eroded = a.filter(ImageFilter.MinFilter(3))
    edge = ImageChops.subtract(a, eroded)
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    layer.paste(Image.new('RGBA', im.size, (255, 255, 255, alpha)), pos, edge)
    canvas.alpha_composite(layer)


def clip_round(im, radius):
    mask = Image.new('L', (im.width * SS, im.height * SS), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, im.width * SS - 1, im.height * SS - 1), radius=radius * SS, fill=255)
    mask = mask.resize(im.size, Image.LANCZOS)
    out = im.copy()
    out.putalpha(ImageChops.multiply(im.getchannel('A'), mask))
    return out

def text_mask(size, text, fnt, xy, anchor='la', tracking=0):
    mask = Image.new('L', (size[0] * SS, size[1] * SS), 0)
    d = ImageDraw.Draw(mask)
    x, y = xy[0] * SS, xy[1] * SS
    if tracking == 0:
        d.text((x, y), text, font=fnt, fill=255, anchor=anchor)
    else:
        # Tracking is only used for short Latin labels.
        widths = [d.textlength(ch, font=fnt) for ch in text]
        total = sum(widths) + tracking * SS * max(0, len(text) - 1)
        if anchor[0] == 'm':
            x -= total / 2
        elif anchor[0] == 'r':
            x -= total
        for ch, cw in zip(text, widths):
            d.text((x, y), ch, font=fnt, fill=255, anchor=anchor)
            x += cw + tracking * SS
    return mask.resize(size, Image.LANCZOS)


def add_headline(canvas, text, xy, size, weight=600, max_width=None, tracking=0, color_top=(255,255,255), color_bottom=(211,220,238), shadow=True):
    f = font(FONT_UI, size, weight)
    # Shrink only if the headline would leave the safe margins.
    if max_width:
        tmp = ImageDraw.Draw(Image.new('RGB', (10, 10)))
        length = tmp.textlength(text, font=f) / SS
        if length > max_width:
            size = int(size * max_width / length)
            f = font(FONT_UI, size, weight)
    mask = text_mask(canvas.size, text, f, xy, anchor='ma', tracking=tracking)
    if shadow:
        sh = mask.filter(ImageFilter.GaussianBlur(18)).point(lambda p: int(p * 0.52))
        layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        layer.paste(Image.new('RGBA', canvas.size, (0, 0, 0, 160)), (0, 9), sh)
        canvas.alpha_composite(layer)
    yy, xx = np.mgrid[0:canvas.height, 0:canvas.width].astype(np.float32)
    t = yy / max(1, canvas.height - 1)
    c0 = np.array(color_top, dtype=np.float32)
    c1 = np.array(color_bottom, dtype=np.float32)
    grad = c0[None, None, :] * (1 - t[:, :, None]) + c1[None, None, :] * t[:, :, None]
    grad = Image.fromarray(np.clip(grad, 0, 255).astype(np.uint8), 'RGB').convert('RGBA')
    canvas.paste(grad, (0, 0), mask)


def add_subtitle(canvas, text, xy, size=38, color=(166,176,197,232), tracking=0):
    f = font(FONT_UI, size, 400)
    mask = text_mask(canvas.size, text, f, xy, anchor='ma', tracking=tracking)
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    layer.paste(Image.new('RGBA', canvas.size, color), (0, 0), mask)
    canvas.alpha_composite(layer)


def add_text(canvas, text, xy, size, path=FONT_UI, weight=400, color=(255,255,255,255), anchor='la', tracking=0):
    f = font(path, size, weight)
    mask = text_mask(canvas.size, text, f, xy, anchor=anchor, tracking=tracking)
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    layer.paste(Image.new('RGBA', canvas.size, color), (0, 0), mask)
    canvas.alpha_composite(layer)


def panel(canvas, box, radius=30, fill=(11,15,24,220), border=(255,255,255,24), glow=(80,120,255), glow_alpha=34, top_highlight=True):
    x0, y0, x1, y1 = box
    w, h = x1 - x0, y1 - y0
    mask = Image.new('L', (w * SS, h * SS), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w * SS - 1, h * SS - 1), radius=radius * SS, fill=255)
    mask = mask.resize((w, h), Image.LANCZOS)
    if glow_alpha:
        gm = mask.filter(ImageFilter.GaussianBlur(max(24, min(w, h) // 8)))
        layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        layer.paste(Image.new('RGBA', (w, h), (*glow, glow_alpha)), (x0, y0), gm)
        canvas.alpha_composite(layer)
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    layer.paste(Image.new('RGBA', (w, h), fill), (x0, y0), mask)
    canvas.alpha_composite(layer)
    if top_highlight:
        hm = Image.new('L', (w * SS, h * SS), 0)
        hd = ImageDraw.Draw(hm)
        hd.rounded_rectangle((0, 0, w * SS - 1, 18 * SS), radius=radius * SS, fill=255)
        hm = hm.resize((w, h), Image.LANCZOS).filter(ImageFilter.GaussianBlur(3))
        layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        layer.paste(Image.new('RGBA', (w, h), (255,255,255,12)), (x0, y0), hm)
        canvas.alpha_composite(layer)
    bm = Image.new('L', (w * SS, h * SS), 0)
    ImageDraw.Draw(bm).rounded_rectangle((1, 1, w * SS - 2, h * SS - 2), radius=radius * SS, outline=255, width=2 * SS)
    bm = bm.resize((w, h), Image.LANCZOS)
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    layer.paste(Image.new('RGBA', (w, h), border), (x0, y0), bm)
    canvas.alpha_composite(layer)


def add_hairline(canvas, box, color=(255,255,255,20)):
    d = ImageDraw.Draw(canvas)
    d.rounded_rectangle(box, radius=1, fill=color)


def save(canvas, name, quality=92):
    canvas.convert('RGB').save(OUT / name, 'JPEG', quality=quality, optimize=True, progressive=True)
    print(OUT / name, canvas.size)


# -----------------------------------------------------------------------------
# 1. Hero: one real expanded island, generous space, studio-like lighting.
# -----------------------------------------------------------------------------
W, H = 1920, 1200
c = make_bg(W, H, [
    (0.50, 0.57, 0.52, 0.42, (78, 91, 255), 0.20, 1.1),
    (0.17, 0.28, 0.45, 0.50, (42, 113, 248), 0.075, 1.4),
    (0.84, 0.72, 0.45, 0.45, (135, 76, 214), 0.065, 1.4),
], top=(6, 8, 14), bottom=(9, 13, 24), seed=101)
add_headline(c, '灵动来到 Windows。', (960, 225), 112, weight=600, max_width=1520)
add_subtitle(c, '把 iPhone 的灵动岛，带到你的桌面。', (960, 395), 39)
island = load_island(EXPANDED, scale=2.78)
iw, ih = island.size
pos = ((W - iw) // 2, 565)
add_shadow_and_glow(c, island, pos, glow=(92, 105, 255), glow_alpha=58)
edge_highlight(c, island, pos, 30)
save(c, 'winislands-hero-text-v2-1920x1200.jpg')

# -----------------------------------------------------------------------------
# 2. Components: collapsed + expanded real states, separated and clean.
# -----------------------------------------------------------------------------
c = make_bg(W, H, [
    (0.47, 0.53, 0.55, 0.46, (72, 119, 255), 0.18, 1.1),
    (0.77, 0.36, 0.42, 0.44, (56, 150, 241), 0.075, 1.4),
    (0.22, 0.75, 0.44, 0.42, (117, 79, 223), 0.055, 1.4),
], top=(6, 9, 16), bottom=(9, 14, 27), seed=202)
add_headline(c, '一切，皆可上岛。', (960, 185), 108, weight=600, max_width=1480)
add_subtitle(c, '组件、歌词、提醒，全部收纳。', (960, 355), 38)
csmall = load_island(COLLAPSED, scale=2.56)
cw, ch = csmall.size
cpos = ((W - cw) // 2, 470)
add_shadow_and_glow(c, csmall, cpos, glow=(76, 125, 255), glow_alpha=34)
edge_highlight(c, csmall, cpos, 32)
big = load_island(EXPANDED, scale=1.96)
bw, bh = big.size
bpos = ((W - bw) // 2, 720)
add_shadow_and_glow(c, big, bpos, glow=(78, 119, 255), glow_alpha=42)
edge_highlight(c, big, bpos, 26)
save(c, 'winislands-components-text-v2-1920x1200.jpg')

# -----------------------------------------------------------------------------
# 3. Lyrics: a large, faithful crop of the current UI's lyric column.
# -----------------------------------------------------------------------------
c = make_bg(W, H, [
    (0.52, 0.51, 0.52, 0.42, (125, 82, 255), 0.19, 1.1),
    (0.77, 0.28, 0.38, 0.38, (195, 95, 184), 0.06, 1.5),
    (0.21, 0.72, 0.42, 0.42, (70, 103, 240), 0.06, 1.4),
], top=(7, 8, 16), bottom=(11, 12, 27), seed=303)
add_headline(c, '歌词，跟着光走。', (960, 185), 108, weight=600, max_width=1480)
add_subtitle(c, '逐字高亮，像现场一样。', (960, 355), 38)
lyric = load_island(EXPANDED, scale=3.25)
lw, lh = lyric.size
lpos = ((W - lw) // 2, 465)
add_shadow_and_glow(c, lyric, lpos, glow=(142, 93, 255), glow_alpha=70)
edge_highlight(c, lyric, lpos, 34)
save(c, 'winislands-lyrics-text-v2-1920x1200.jpg')

# -----------------------------------------------------------------------------
# 4. API: real endpoint shown beside the real island states.
# -----------------------------------------------------------------------------
c = make_bg(W, H, [
    (0.28, 0.48, 0.48, 0.45, (37, 164, 181), 0.12, 1.2),
    (0.70, 0.56, 0.50, 0.45, (61, 100, 245), 0.15, 1.1),
    (0.90, 0.20, 0.34, 0.35, (49, 126, 218), 0.05, 1.5),
], top=(5, 9, 16), bottom=(8, 16, 26), seed=404)
add_headline(c, '开发者，也能上岛。', (960, 155), 104, weight=600, max_width=1470)
add_subtitle(c, '本地 API，一行推送。', (960, 330), 37)

code_box = (142, 408, 1090, 1030)
panel(c, code_box, radius=34, fill=(8, 13, 21, 216), border=(255,255,255,25), glow=(52, 161, 177), glow_alpha=16)
# Code editor content.
add_text(c, 'POST', (198, 465), 31, path=FONT_MONO_B, color=(177, 140, 255, 255))
add_text(c, ' /v1/island/push', (280, 465), 31, path=FONT_MONO, color=(102, 197, 255, 255))
add_text(c, 'Content-Type: application/json', (198, 530), 25, path=FONT_MONO, color=(130, 141, 160, 255))
add_text(c, '{', (198, 600), 30, path=FONT_MONO, color=(220, 226, 237, 255))
add_text(c, '  "title":   "Download complete",', (198, 660), 27, path=FONT_MONO, color=(214, 221, 235, 255))
add_text(c, '  "message": "report.pdf saved",', (198, 720), 27, path=FONT_MONO, color=(214, 221, 235, 255))
add_text(c, '  "seconds": 6', (198, 780), 27, path=FONT_MONO, color=(214, 221, 235, 255))
add_text(c, '}', (198, 805), 30, path=FONT_MONO, color=(220, 226, 237, 255))
add_text(c, '// 任意脚本、网页或原生应用均可接入', (198, 910), 25, path=FONT_UI, weight=400, color=(119, 132, 154, 255))

# Right-hand live island states.
cr = load_island(COLLAPSED, scale=1.38)
crw, crh = cr.size
crpos = (1190, 438)
add_shadow_and_glow(c, cr, crpos, glow=(61, 166, 180), glow_alpha=32)
edge_highlight(c, cr, crpos, 28)
er = load_island(EXPANDED, scale=1.18)
erw, erh = er.size
erpos = (1190, 610)
add_shadow_and_glow(c, er, erpos, glow=(72, 123, 238), glow_alpha=30)
edge_highlight(c, er, erpos, 22)
save(c, 'winislands-api-text-v2-1920x1200.jpg')

# -----------------------------------------------------------------------------
# 5. Square social share card.
# -----------------------------------------------------------------------------
S = 1200
c = make_bg(S, S, [
    (0.50, 0.55, 0.58, 0.50, (83, 91, 244), 0.21, 1.1),
    (0.27, 0.22, 0.42, 0.35, (43, 121, 240), 0.07, 1.4),
    (0.76, 0.78, 0.45, 0.42, (137, 76, 210), 0.06, 1.4),
], top=(6, 8, 15), bottom=(10, 13, 25), seed=505)
mark = Image.open(ROOT / 'logo' / 'exports' / 'WinIslands-Mark-1440x640.png').convert('RGBA')
mark = mark.resize((330, round(mark.height * 330 / mark.width)), Image.LANCZOS)
c.alpha_composite(mark, ((S - mark.width) // 2, 145))
add_text(c, 'WinIslands', (600, 375), 88, path=FONT_SEGOE_SB, color=(249, 250, 255, 255), anchor='ma')
add_text(c, 'Windows 上的灵动岛', (600, 488), 37, path=FONT_UI, weight=400, color=(171, 182, 203, 240), anchor='ma')
share_island = load_island(EXPANDED, scale=1.56)
sw, sh = share_island.size
spos = ((S - sw) // 2, 620)
add_shadow_and_glow(c, share_island, spos, glow=(98, 102, 255), glow_alpha=50)
edge_highlight(c, share_island, spos, 26)
add_text(c, '免费 · 开源 · 无遥测', (600, 1060), 26, path=FONT_UI, weight=400, color=(132, 143, 164, 235), anchor='ma')
save(c, 'winislands-share-text-v2-1200x1200.jpg')

# -----------------------------------------------------------------------------
# 6. Wide gallery variant of the social card (square remains the OG/social asset).
# -----------------------------------------------------------------------------
c = make_bg(W, H, [
    (0.50, 0.55, 0.58, 0.50, (83, 91, 244), 0.21, 1.1),
    (0.27, 0.22, 0.42, 0.35, (43, 121, 240), 0.07, 1.4),
    (0.76, 0.78, 0.45, 0.42, (137, 76, 210), 0.06, 1.4),
], top=(6, 8, 15), bottom=(10, 13, 25), seed=606)
mark_w = Image.open(ROOT / 'logo' / 'exports' / 'WinIslands-Mark-1440x640.png').convert('RGBA')
mark_w = mark_w.resize((330, round(mark_w.height * 330 / mark_w.width)), Image.LANCZOS)
c.alpha_composite(mark_w, ((W - mark_w.width) // 2, 120))
add_text(c, 'WinIslands', (960, 335), 88, path=FONT_SEGOE_SB, color=(249, 250, 255, 255), anchor='ma')
add_text(c, 'Windows 上的灵动岛', (960, 448), 37, path=FONT_UI, weight=400, color=(171, 182, 203, 240), anchor='ma')
share_wide = load_island(EXPANDED, scale=3.0)
sw, sh = share_wide.size
spos = ((W - sw) // 2, 525)
add_shadow_and_glow(c, share_wide, spos, glow=(98, 102, 255), glow_alpha=58)
edge_highlight(c, share_wide, spos, 30)
add_text(c, '免费 · 开源 · 无遥测', (960, 1120), 26, path=FONT_UI, weight=400, color=(132, 143, 164, 235), anchor='ma')
save(c, 'winislands-share-text-v2-1920x1200.jpg')
