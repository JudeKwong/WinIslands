from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter
import numpy as np
import random

OUT = Path(r'E:\MyFiles\Program\WinIsland\website\assets\promos')
OUT.mkdir(parents=True, exist_ok=True)
random.seed(20261122)

SS = 2  # supersampling


def gradient_bg(size, top=(6,8,15), bottom=(17,21,37), accent=(112,96,240), accent_strength=0.22):
    w, h = size
    yy, xx = np.mgrid[0:h, 0:w]
    t = yy / max(1, h - 1)
    top = np.array(top, dtype=np.float32)
    bottom = np.array(bottom, dtype=np.float32)
    rgb = top[None, None, :] * (1 - t[:, :, None]) + bottom[None, None, :] * t[:, :, None]
    diag = 1 - (xx + yy) / (w + h)
    rgb *= (0.94 + 0.12 * diag[:, :, None])
    cx, cy = w * 0.5, h * 0.46
    dist = np.sqrt(((xx - cx) / (w * 0.68)) ** 2 + ((yy - cy) / (h * 0.62)) ** 2)
    radial = np.clip(1 - dist, 0, 1) ** 2
    accent = np.array(accent, dtype=np.float32)
    rgb += accent[None, None, :] * radial[:, :, None] * accent_strength
    vign = np.clip(1 - dist * 0.8, 0, 1) ** 1.6
    rgb *= (0.70 + 0.30 * vign[:, :, None])
    img = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), 'RGB')
    # subtle grain to prevent banding
    noise = Image.effect_noise((w, h), 12).convert('RGB')
    img = Image.blend(img, noise, 0.012)
    return img.convert('RGBA')


def rounded_mask(size, radius):
    m = Image.new('L', size, 0)
    d = ImageDraw.Draw(m)
    d.rounded_rectangle((0, 0, size[0]-1, size[1]-1), radius=radius, fill=255)
    return m


def linear_gradient(size, c1, c2, angle=25):
    w, h = size
    yy, xx = np.mgrid[0:h, 0:w]
    t = (xx * np.cos(np.deg2rad(angle)) + yy * np.sin(np.deg2rad(angle))) / (w + h)
    t = np.clip((t - t.min()) / (np.ptp(t) if np.ptp(t) else 1), 0, 1)
    c1 = np.array(c1, dtype=np.float32)
    c2 = np.array(c2, dtype=np.float32)
    rgb = c1[None, None, :] * (1 - t[:, :, None]) + c2[None, None, :] * t[:, :, None]
    return Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), 'RGB')


def draw_capsule(canvas, box, radius=None, accent=(112,96,240), glow=True, highlight=True):
    x0, y0, x1, y1 = [int(v * SS) for v in box]
    w, h = x1 - x0, y1 - y0
    if radius is None:
        radius = min(w, h) // 2
    # outer glow
    if glow:
        glow_layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        mask = rounded_mask((w, h), radius).filter(ImageFilter.GaussianBlur(max(16, w // 20)))
        solid = Image.new('RGBA', (w, h), (*accent, 110))
        glow_layer.paste(solid, (x0, y0), mask)
        canvas.alpha_composite(glow_layer)
    # capsule body
    body_mask = rounded_mask((w, h), radius)
    fill = linear_gradient((w, h), (35,44,67), (13,18,29), angle=35).convert('RGBA')
    canvas.paste(fill, (x0, y0), body_mask)
    # border
    border_layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    bm = rounded_mask((w, h), radius)
    # slightly larger border
    bw, bh = w + 6, h + 6
    bm2 = rounded_mask((bw, bh), radius + 3)
    diff = Image.new('L', (bw, bh), 0)
    diff.paste(255, (0, 0), bm2)
    diff.paste(0, (3, 3), bm)
    border_color = Image.new('RGBA', (bw, bh), (255, 255, 255, 52))
    border_layer.paste(border_color, (x0 - 3, y0 - 3), diff)
    canvas.alpha_composite(border_layer)
    # top highlight
    if highlight:
        hl = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
        hw, hh = int(w * 0.82), max(10, int(h * 0.18))
        hm = rounded_mask((hw, hh), hh // 2).filter(ImageFilter.GaussianBlur(8))
        solid = Image.new('RGBA', (hw, hh), (255, 255, 255, 34))
        hl.paste(solid, (x0 + (w - hw)//2, y0 + int(h * 0.08)), hm)
        canvas.alpha_composite(hl)
    return canvas


def draw_orb(canvas, center, radius, color, alpha=110):
    x, y = [int(v * SS) for v in center]
    r = int(radius * SS)
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    d.ellipse((x-r, y-r, x+r, y+r), fill=(*color, alpha))
    layer = layer.filter(ImageFilter.GaussianBlur(max(6, r // 3)))
    canvas.alpha_composite(layer)
    return canvas


def draw_particles(canvas, count=45, area=(0.05, 0.1, 0.95, 0.9), colors=((255,255,255),(124,140,255),(178,132,255))):
    x0, y0, x1, y1 = area
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    for _ in range(count):
        x = random.uniform(x0, x1) * canvas.width
        y = random.uniform(y0, y1) * canvas.height
        r = random.uniform(1.2, 4.5) * SS
        c = random.choice(colors)
        a = random.randint(18, 72)
        d.ellipse((x-r, y-r, x+r, y+r), fill=(*c, a))
    layer = layer.filter(ImageFilter.GaussianBlur(2))
    canvas.alpha_composite(layer)
    return canvas


def finish(img, out_name):
    target = (img.width // SS, img.height // SS)
    img = img.resize(target, Image.LANCZOS)
    img.convert('RGB').save(OUT / out_name, 'JPEG', quality=90, optimize=True, progressive=True)
    print(OUT / out_name, target)


def hero():
    size = (1920 * SS, 1080 * SS)
    canvas = gradient_bg(size, top=(5,7,14), bottom=(16,20,36), accent=(112,96,240), accent_strength=0.24)
    draw_particles(canvas, count=70)
    draw_capsule(canvas, (430, 410, 1490, 650), accent=(112,96,240))
    # two small accent orbs on the capsule, like abstract UI indicators
    draw_orb(canvas, (560, 530), 16, (124,140,255), 150)
    draw_orb(canvas, (1360, 530), 16, (178,132,255), 150)
    finish(canvas, 'winislands-hero-v5-1920x1080.jpg')


def components():
    size = (1920 * SS, 1080 * SS)
    canvas = gradient_bg(size, top=(4,7,14), bottom=(13,19,36), accent=(96,130,240), accent_strength=0.22)
    draw_particles(canvas, count=55)
    # three balanced glass capsules, Apple-like rhythm
    draw_capsule(canvas, (330, 300, 810, 420), accent=(96,130,240))
    draw_capsule(canvas, (1110, 300, 1590, 420), accent=(178,132,255))
    draw_capsule(canvas, (620, 610, 1300, 730), accent=(96,176,178))
    finish(canvas, 'winislands-components-v7-1920x1080.jpg')


def lyrics():
    size = (1920 * SS, 1080 * SS)
    canvas = gradient_bg(size, top=(5,7,15), bottom=(19,16,38), accent=(158,118,240), accent_strength=0.22)
    draw_particles(canvas, count=48)
    draw_capsule(canvas, (480, 390, 1440, 630), accent=(158,118,240))
    # abstract lyric lines: soft glowing rounded bars
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    for i, (x0, x1, y) in enumerate([(640, 860, 470), (900, 1120, 500), (700, 920, 530), (980, 1200, 560)]):
        d.rounded_rectangle((x0*SS, y*SS, x1*SS, (y+10)*SS), radius=5*SS, fill=(255, 255, 255, 72 if i % 2 else 108))
    layer = layer.filter(ImageFilter.GaussianBlur(3))
    canvas.alpha_composite(layer)
    finish(canvas, 'winislands-lyrics-v4-1920x1080.jpg')


def api():
    size = (1920 * SS, 1080 * SS)
    canvas = gradient_bg(size, top=(4,8,14), bottom=(12,22,34), accent=(96,176,178), accent_strength=0.22)
    draw_particles(canvas, count=60)
    draw_capsule(canvas, (470, 400, 1450, 640), accent=(96,176,178))
    # abstract API nodes: glass dots and thin connecting lines
    layer = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    nodes = [(620, 520), (760, 450), (960, 560), (1160, 470), (1300, 540)]
    for a, b in zip(nodes[:-1], nodes[1:]):
        d.line((a[0]*SS, a[1]*SS, b[0]*SS, b[1]*SS), fill=(255,255,255,38), width=2*SS)
    for x, y in nodes:
        r = 10 * SS
        d.ellipse((x*SS-r, y*SS-r, x*SS+r, y*SS+r), fill=(255,255,255,90))
    layer = layer.filter(ImageFilter.GaussianBlur(3))
    canvas.alpha_composite(layer)
    finish(canvas, 'winislands-api-v4-1920x1080.jpg')


def share():
    size = (1200 * SS, 1200 * SS)
    canvas = gradient_bg(size, top=(5,7,14), bottom=(16,19,35), accent=(112,96,240), accent_strength=0.24)
    draw_particles(canvas, count=60, area=(0.08, 0.12, 0.92, 0.88))
    draw_capsule(canvas, (200, 500, 1000, 680), accent=(112,96,240))
    draw_orb(canvas, (320, 590), 14, (124,140,255), 150)
    draw_orb(canvas, (880, 590), 14, (178,132,255), 150)
    finish(canvas, 'winislands-share-v4-1200x1200.jpg')


hero()
components()
lyrics()
api()
share()
