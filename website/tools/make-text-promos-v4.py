# -*- coding: utf-8 -*-
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageFont, ImageChops
import numpy as np

ROOT = Path(r'E:\MyFiles\Program\WinIsland')
BASE = ROOT / 'build' / 'ui-capture-316' / 'out'
WEBSITE = ROOT / 'WinIsland-main' / 'website'
OUT = WEBSITE / 'assets' / 'promos'
MASTER = ROOT / 'build' / 'promo-v4-master'
OUT.mkdir(parents=True, exist_ok=True)
MASTER.mkdir(parents=True, exist_ok=True)

FONT_UI = r'C:\Windows\Fonts\NotoSansSC-VF.ttf'
FONT_SEGOE = r'C:\Windows\Fonts\segoeui.ttf'
FONT_SEGOE_SB = r'C:\Windows\Fonts\seguisb.ttf'
FONT_SEGOE_B = r'C:\Windows\Fonts\segoeuib.ttf'
FONT_MONO = r'C:\Windows\Fonts\consola.ttf'
FONT_MONO_B = r'C:\Windows\Fonts\consolab.ttf'

SS = 2
DS = 2560 / 1920
EXPANDED = BASE / 'music-expanded-12x.png'
COVER = OUT / 'cover-shallwetalk.jpg'


def font(path, size, weight=None):
    f = ImageFont.truetype(path, int(size * SS * DS))
    if weight is not None:
        try:
            f.set_variation_by_axes([weight])
        except Exception:
            pass
    return f


def make_bg(w, h, glows, top=(5, 7, 13), bottom=(9, 12, 22), seed=31):
    w, h = round(w * DS), round(h * DS)
    yy, xx = np.mgrid[0:h, 0:w].astype(np.float32)
    t = yy / max(1, h - 1)
    c0 = np.array(top, dtype=np.float32)
    c1 = np.array(bottom, dtype=np.float32)
    rgb = c0[None, None, :] * (1 - t[:, :, None]) + c1[None, None, :] * t[:, :, None]

    edge = np.abs(xx / max(1, w - 1) - 0.5) * 2.0
    rgb *= (1.025 - 0.05 * edge[:, :, None])

    nx = xx / max(1, w - 1)
    ny = yy / max(1, h - 1)
    for cx, cy, rx, ry, color, strength, power in glows:
        dist = ((nx - cx) / rx) ** 2 + ((ny - cy) / ry) ** 2
        field = np.exp(-dist * 2.05) ** power
        rgb += np.array(color, dtype=np.float32)[None, None, :] * field[:, :, None] * strength

    vx = (nx - 0.5) / 0.74
    vy = (ny - 0.47) / 0.74
    vign = np.clip(1.0 - (vx * vx + vy * vy) * 0.50, 0.0, 1.0) ** 1.32
    rgb *= (0.76 + 0.24 * vign[:, :, None])

    rng = np.random.default_rng(seed)
    grain = rng.normal(0, 0.95, (h, w, 1)).astype(np.float32)
    rgb += grain
    return Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), 'RGB').convert('RGBA')


def load_island(width_du):
    """Real 12x WPF render -> one Lanczos downscale to the final card size."""
    im = Image.open(EXPANDED).convert('RGBA')
    tw = max(1, round(width_du * DS))
    th = max(1, round(im.height * tw / im.width))

    # Resize in premultiplied alpha space so the rounded corners do not pick up dark fringes.
    arr = np.asarray(im).astype(np.float32)
    a = arr[..., 3:4] / 255.0
    arr[..., :3] *= a
    pre = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8), 'RGBA').resize((tw, th), Image.LANCZOS)
    arr = np.asarray(pre).astype(np.float32)
    a = arr[..., 3:4] / 255.0
    arr[..., :3] = np.where(a > 1e-5, arr[..., :3] / np.maximum(a, 1e-5), 0)
    out = Image.fromarray(np.clip(arr, 0, 255).astype(np.uint8), 'RGBA')

    # Very light output sharpening: enough to restore icon strokes without ringing.
    rgb = out.convert('RGB').filter(ImageFilter.UnsharpMask(radius=0.82, percent=38, threshold=2))
    out = rgb.convert('RGBA')
    out.putalpha(pre.getchannel('A'))

    # The in-app artwork is a bitmap upscaled by WPF, so it always reads soft.
    # Re-render the official cover into the exact artwork rect for a crisp result.
    out = replace_cover_art(out, im.width)
    return out


def replace_cover_art(canvas, src_width):
    s = canvas.width / float(src_width)
    ax, ay, asz, rad = 698, 668, 580, 46
    rect = (round(ax * s), round(ay * s), round((ax + asz) * s), round((ay + asz) * s))
    w, h = rect[2] - rect[0], rect[3] - rect[1]
    if w < 8 or h < 8 or not COVER.exists():
        return canvas
    cov = Image.open(COVER).convert('RGB').resize((w, h), Image.LANCZOS)
    cov = cov.filter(ImageFilter.UnsharpMask(radius=0.8, percent=55, threshold=1))
    mask = Image.new('L', (w * 4, h * 4), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w * 4 - 1, h * 4 - 1),
                                           radius=max(2, round(rad * s * 4)), fill=255)
    mask = mask.resize((w, h), Image.LANCZOS)
    canvas.paste(cov, (rect[0], rect[1]), mask)
    return canvas


def add_island(canvas, im, x_px, y_px, glow=(90, 110, 255), glow_alpha=52, shadow=True, edge=28):
    alpha = im.getchannel('A')
    if glow_alpha:
        blur = max(22, im.width // 30)
        pad = blur * 3
        ap = Image.new('L', (im.width + pad * 2, im.height + pad * 2), 0)
        ap.paste(alpha, (pad, pad))
        gm = ap.filter(ImageFilter.GaussianBlur(blur))
        layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        layer.paste(Image.new('RGBA', ap.size, (*glow, glow_alpha)), (x_px - pad, y_px - pad), gm)
        canvas.alpha_composite(layer)
    if shadow:
        blur = max(16, im.width // 90)
        off = max(14, im.height // 36)
        pad = blur * 3 + off
        ap = Image.new('L', (im.width + pad * 2, im.height + pad * 2), 0)
        ap.paste(alpha, (pad, pad))
        sm = ap.filter(ImageFilter.GaussianBlur(blur))
        layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        layer.paste(Image.new('RGBA', ap.size, (0, 0, 0, 175)), (x_px - pad, y_px - pad + off), sm)
        canvas.alpha_composite(layer)
    if edge:
        eroded = alpha.filter(ImageFilter.MinFilter(3))
        rim = ImageChops.subtract(alpha, eroded)
        layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        layer.paste(Image.new('RGBA', im.size, (255, 255, 255, edge)), (x_px, y_px), rim)
        canvas.alpha_composite(layer)
    canvas.alpha_composite(im, (x_px, y_px))


def add_island_at(canvas, width_du, top_du, cx_du=960, **kwargs):
    im = load_island(width_du)
    x = round((cx_du - width_du / 2) * DS)
    y = round(top_du * DS)
    add_island(canvas, im, x, y, **kwargs)
    return im, x, y


def text_mask(size, text, fnt, xy, anchor='la', tracking=0):
    mask = Image.new('L', (size[0] * SS, size[1] * SS), 0)
    d = ImageDraw.Draw(mask)
    x, y = xy[0] * SS * DS, xy[1] * SS * DS
    if tracking == 0:
        d.text((x, y), text, font=fnt, fill=255, anchor=anchor)
    else:
        widths = []
        for ch in text:
            widths.append(d.textlength(ch, font=fnt) + tracking * SS * DS)
        total = sum(widths) - tracking * SS * DS
        if anchor in ('ma', 'mm'):
            x -= total / 2
        for ch, w in zip(text, widths):
            d.text((x, y), ch, font=fnt, fill=255, anchor=anchor)
            x += w
    return mask.resize(size, Image.LANCZOS)


def add_headline(canvas, text, xy, size=108, weight=600, max_width=1600,
                 color_top=(255, 255, 255, 255), color_bottom=(222, 229, 248, 255), tracking=0.0):
    f = font(FONT_UI, size, weight)
    while f.getlength(text) / (SS * DS) > max_width and size > 40:
        size -= 2
        f = font(FONT_UI, size, weight)
    mask = text_mask(canvas.size, text, f, xy, anchor='ma', tracking=tracking)
    sh = mask.filter(ImageFilter.GaussianBlur(round(12 * DS))).point(lambda p: int(p * 0.42))
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    layer.paste(Image.new('RGBA', canvas.size, (0, 0, 0, 120)), (0, round(7 * DS)), sh)
    canvas.alpha_composite(layer)
    yy, xx = np.mgrid[0:canvas.height, 0:canvas.width].astype(np.float32)
    t = yy / max(1, canvas.height - 1)
    c0 = np.array(color_top, dtype=np.float32)
    c1 = np.array(color_bottom, dtype=np.float32)
    grad = c0[None, None, :] * (1 - t[:, :, None]) + c1[None, None, :] * t[:, :, None]
    grad = Image.fromarray(np.clip(grad, 0, 255).astype(np.uint8), 'RGB').convert('RGBA')
    canvas.paste(grad, (0, 0), mask)


def add_text(canvas, text, xy, size, path=FONT_UI, weight=400, color=(255, 255, 255, 255), anchor='la', tracking=0):
    f = font(path, size, weight)
    mask = text_mask(canvas.size, text, f, xy, anchor=anchor, tracking=tracking)
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    layer.paste(Image.new('RGBA', canvas.size, color), (0, 0), mask)
    canvas.alpha_composite(layer)


def add_subtitle(canvas, text, xy, size=38, color=(166, 176, 197, 236), tracking=0):
    add_text(canvas, text, xy, size, path=FONT_UI, weight=400, color=color, anchor='ma', tracking=tracking)


def add_eyebrow(canvas, text, xy, size=21, color=(137, 150, 174, 220), tracking=4.5):
    if any(ord(ch) > 127 for ch in text):
        add_text(canvas, text, xy, size, path=FONT_UI, weight=500, color=color,
                 anchor='ma', tracking=min(tracking, 2.0))
    else:
        add_text(canvas, text, xy, size, path=FONT_SEGOE_SB, color=color,
                 anchor='ma', tracking=tracking)


def panel(canvas, box, radius=32, fill=(8, 12, 20, 214), border=(255, 255, 255, 24),
          glow=(56, 148, 176), glow_alpha=14, top_highlight=True):
    x0, y0, x1, y1 = [round(v * DS) for v in box]
    radius = round(radius * DS)
    w, h = x1 - x0, y1 - y0
    mask = Image.new('L', (w * SS, h * SS), 0)
    ImageDraw.Draw(mask).rounded_rectangle((0, 0, w * SS - 1, h * SS - 1), radius=radius * SS, fill=255)
    mask = mask.resize((w, h), Image.LANCZOS)
    if glow_alpha:
        gm = mask.filter(ImageFilter.GaussianBlur(max(24, min(w, h) // 9)))
        layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        layer.paste(Image.new('RGBA', (w, h), (*glow, glow_alpha)), (x0, y0), gm)
        canvas.alpha_composite(layer)
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    layer.paste(Image.new('RGBA', (w, h), fill), (x0, y0), mask)
    canvas.alpha_composite(layer)
    if top_highlight:
        hm = Image.new('L', (w * SS, h * SS), 0)
        ImageDraw.Draw(hm).rounded_rectangle((0, 0, w * SS - 1, round(16 * DS) * SS), radius=radius * SS, fill=255)
        hm = hm.resize((w, h), Image.LANCZOS).filter(ImageFilter.GaussianBlur(3))
        layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        layer.paste(Image.new('RGBA', (w, h), (255, 255, 255, 11)), (x0, y0), hm)
        canvas.alpha_composite(layer)
    bm = Image.new('L', (w * SS, h * SS), 0)
    ImageDraw.Draw(bm).rounded_rectangle((1, 1, w * SS - 2, h * SS - 2), radius=radius * SS,
                                        outline=255, width=max(1, round(2 * DS * SS)))
    bm = bm.resize((w, h), Image.LANCZOS)
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    layer.paste(Image.new('RGBA', (w, h), border), (x0, y0), bm)
    canvas.alpha_composite(layer)


def save_master(canvas, name):
    p = MASTER / name
    canvas.save(p)
    print('master', p, canvas.size)


def save_webp(canvas, name, quality=92):
    p = OUT / name
    canvas.convert('RGB').save(p, 'WEBP', quality=quality, method=6)
    print('webp', p, canvas.size, p.stat().st_size)


def save_jpg(canvas, name, quality=95):
    p = OUT / name
    canvas.convert('RGB').save(p, 'JPEG', quality=quality, optimize=True, progressive=True, subsampling=0)
    print('jpg', p, canvas.size, p.stat().st_size)


W, H = 1920, 1200

# -----------------------------------------------------------------------------
# 1. Hero
# -----------------------------------------------------------------------------
c = make_bg(W, H, [
    (0.50, 0.55, 0.52, 0.44, (74, 88, 255), 0.20, 1.08),
    (0.16, 0.28, 0.44, 0.50, (38, 108, 246), 0.072, 1.4),
    (0.85, 0.70, 0.44, 0.46, (135, 72, 214), 0.062, 1.4),
], top=(5, 7, 13), bottom=(9, 12, 23), seed=411)
add_eyebrow(c, 'WINISLANDS 3.1.9', (960, 92), 21)
add_headline(c, '灵动来到 Windows。', (960, 148), 108, weight=600, max_width=1580)
add_subtitle(c, '把 iPhone 的灵动岛，带到你的桌面。', (960, 308), 38)
add_island_at(c, 1560, 386, glow=(92, 105, 255), glow_alpha=54, edge=28)
add_text(c, '免费 · 开源 · 无遥测', (960, 1052), 25, path=FONT_UI, weight=400,
         color=(131, 143, 165, 232), anchor='ma', tracking=1.0)
save_master(c, 'hero-v4.png')
save_webp(c, 'winislands-hero-v4-2560x1600.webp', 93)

# -----------------------------------------------------------------------------
# 2. Lyrics
# -----------------------------------------------------------------------------
c = make_bg(W, H, [
    (0.51, 0.50, 0.53, 0.44, (126, 82, 255), 0.20, 1.08),
    (0.78, 0.27, 0.38, 0.38, (197, 92, 184), 0.058, 1.5),
    (0.20, 0.73, 0.43, 0.44, (66, 102, 240), 0.06, 1.4),
], top=(6, 7, 14), bottom=(10, 11, 25), seed=422)
add_eyebrow(c, 'LYRICS · 逐字高亮', (960, 84), 21, color=(158, 146, 204, 224))
add_headline(c, '歌词，跟着光走。', (960, 138), 108, weight=600, max_width=1580)
add_subtitle(c, '逐字高亮，像现场一样。', (960, 298), 38)
add_island_at(c, 1620, 376, glow=(142, 93, 255), glow_alpha=62, edge=30)
add_text(c, '逐字高亮 · 自动滚动 · Apple Music / 本地 LRC', (960, 1060), 23,
         path=FONT_UI, weight=400, color=(128, 139, 162, 226), anchor='ma', tracking=0.6)
save_master(c, 'lyrics-v4.png')
save_webp(c, 'winislands-lyrics-v4-2560x1600.webp', 93)

# -----------------------------------------------------------------------------
# 3. Components
# -----------------------------------------------------------------------------
c = make_bg(W, H, [
    (0.47, 0.52, 0.55, 0.46, (68, 118, 255), 0.18, 1.08),
    (0.78, 0.35, 0.42, 0.44, (52, 150, 241), 0.072, 1.4),
    (0.21, 0.76, 0.44, 0.42, (112, 76, 222), 0.052, 1.4),
], top=(5, 8, 15), bottom=(9, 13, 26), seed=433)
add_eyebrow(c, 'ONE ISLAND · EVERYTHING', (960, 86), 21, color=(132, 154, 202, 220))
add_headline(c, '一切，皆可上岛。', (960, 140), 108, weight=600, max_width=1580)
add_subtitle(c, '组件、歌词、提醒，全部收纳。', (960, 300), 38)
add_island_at(c, 1540, 380, glow=(78, 119, 255), glow_alpha=48, edge=26)
for i, label in enumerate(['实时歌词', '播放控制', '本地推送']):
    cx = 640 + i * 320
    add_text(c, label, (cx, 1040), 24, path=FONT_UI, weight=500,
             color=(154, 166, 190, 235), anchor='ma')
    if i < 2:
        add_text(c, '·', (cx + 160, 1038), 24, path=FONT_UI, weight=400,
                 color=(96, 108, 132, 200), anchor='ma')
save_master(c, 'components-v4.png')
save_webp(c, 'winislands-components-v4-2560x1600.webp', 93)

# -----------------------------------------------------------------------------
# 4. API
# -----------------------------------------------------------------------------
c = make_bg(W, H, [
    (0.27, 0.48, 0.48, 0.45, (34, 164, 181), 0.12, 1.2),
    (0.70, 0.55, 0.50, 0.46, (58, 98, 244), 0.15, 1.1),
    (0.91, 0.19, 0.34, 0.35, (48, 124, 216), 0.048, 1.5),
], top=(4, 8, 15), bottom=(8, 15, 25), seed=444)
add_eyebrow(c, 'LOCAL API · 127.0.0.1:9840', (960, 78), 21, color=(124, 190, 202, 226))
add_headline(c, '开发者，也能上岛。', (960, 128), 104, weight=600, max_width=1560)
add_subtitle(c, '本地 API，一行推送。', (960, 286), 37)

panel(c, (104, 352, 1002, 1032), radius=34, fill=(7, 12, 20, 218), border=(255, 255, 255, 24),
      glow=(48, 158, 174), glow_alpha=14)
add_text(c, 'POST', (160, 410), 31, path=FONT_MONO_B, color=(178, 140, 255, 255))
add_text(c, ' /v1/island/push', (245, 410), 31, path=FONT_MONO, color=(100, 198, 255, 255))
add_text(c, 'Content-Type: application/json', (160, 472), 24, path=FONT_MONO, color=(130, 141, 160, 255))
add_text(c, '{', (160, 540), 30, path=FONT_MONO, color=(220, 226, 237, 255))
add_text(c, '  "title": "Download complete",', (160, 600), 27, path=FONT_MONO, color=(216, 223, 236, 255))
add_text(c, '  "body": "report.pdf saved",', (160, 660), 27, path=FONT_MONO, color=(216, 223, 236, 255))
add_text(c, '  "icon": "\\uE896",', (160, 720), 27, path=FONT_MONO, color=(216, 223, 236, 255))
add_text(c, '  "type": "success",', (160, 780), 27, path=FONT_MONO, color=(216, 223, 236, 255))
add_text(c, '  "duration_seconds": 6', (160, 840), 27, path=FONT_MONO, color=(216, 223, 236, 255))
add_text(c, '}', (160, 900), 30, path=FONT_MONO, color=(220, 226, 237, 255))
add_text(c, '// 任意脚本、网页或原生应用均可接入', (160, 962), 23, path=FONT_UI, weight=400,
         color=(116, 130, 152, 255))
add_island_at(c, 820, 430, cx_du=1462, glow=(61, 166, 180), glow_alpha=40, edge=24)
add_text(c, '真实本地接口 · 无需云端', (1462, 790), 24, path=FONT_UI, weight=400,
         color=(136, 151, 174, 230), anchor='ma', tracking=0.5)
save_master(c, 'api-v4.png')
save_webp(c, 'winislands-api-v4-2560x1600.webp', 93)

# -----------------------------------------------------------------------------
# 5. Wide share / gallery brand card
# -----------------------------------------------------------------------------
c = make_bg(W, H, [
    (0.50, 0.54, 0.58, 0.50, (82, 90, 244), 0.21, 1.08),
    (0.26, 0.22, 0.42, 0.36, (42, 120, 240), 0.068, 1.4),
    (0.77, 0.77, 0.45, 0.42, (136, 74, 210), 0.058, 1.4),
], top=(5, 7, 14), bottom=(9, 12, 24), seed=455)
mark = Image.open(ROOT / 'logo' / 'exports' / 'WinIslands-Mark-1440x640.png').convert('RGBA')
mark = mark.resize((round(300 * DS), round(mark.height * 300 * DS / mark.width)), Image.LANCZOS)
c.alpha_composite(mark, ((c.width - mark.width) // 2, round(86 * DS)))
add_text(c, 'WinIslands', (960, 228), 86, path=FONT_SEGOE_SB, color=(249, 250, 255, 255), anchor='ma')
add_subtitle(c, 'Windows 上的灵动岛', (960, 336), 33, color=(170, 181, 203, 240))
add_island_at(c, 1280, 402, glow=(98, 102, 255), glow_alpha=54, edge=26)
add_text(c, '免费 · 开源 · 无遥测', (960, 990), 24, path=FONT_UI, weight=400,
         color=(130, 141, 163, 230), anchor='ma', tracking=1.0)
save_master(c, 'share-wide-v4.png')
save_webp(c, 'winislands-share-v4-2560x1600.webp', 93)

# -----------------------------------------------------------------------------
# 6. Square social / OG / README share
# -----------------------------------------------------------------------------
S = 1200
c = make_bg(S, S, [
    (0.50, 0.55, 0.58, 0.50, (83, 91, 244), 0.21, 1.08),
    (0.27, 0.22, 0.42, 0.35, (43, 121, 240), 0.068, 1.4),
    (0.76, 0.78, 0.45, 0.42, (137, 76, 210), 0.058, 1.4),
], top=(5, 7, 14), bottom=(10, 13, 25), seed=466)
mark = Image.open(ROOT / 'logo' / 'exports' / 'WinIslands-Mark-1440x640.png').convert('RGBA')
mark = mark.resize((round(280 * DS), round(mark.height * 280 * DS / mark.width)), Image.LANCZOS)
c.alpha_composite(mark, ((c.width - mark.width) // 2, round(96 * DS)))
add_text(c, 'WinIslands', (600, 252), 84, path=FONT_SEGOE_SB, color=(249, 250, 255, 255), anchor='ma')
add_subtitle(c, 'Windows 上的灵动岛', (600, 356), 32, color=(171, 182, 203, 240))
add_island_at(c, 1060, 430, cx_du=600, glow=(98, 102, 255), glow_alpha=54, edge=26)
add_text(c, '免费 · 开源 · 无遥测', (600, 946), 24, path=FONT_UI, weight=400,
         color=(132, 143, 164, 232), anchor='ma', tracking=1.0)
save_master(c, 'share-square-v4.png')
save_jpg(c, 'winislands-share-v4-1600x1600.jpg', 95)

print('done')
