from pathlib import Path
from PIL import Image, ImageFilter
import numpy as np

BASE = Path(r'E:\MyFiles\Program\WinIsland\build\ui-capture-316beta3\roundtrip-latest')
OUT = Path(r'E:\MyFiles\Program\WinIsland\website\assets\promos')
OUT.mkdir(parents=True, exist_ok=True)


def cutout_black(path: Path) -> Image.Image:
    im = Image.open(path).convert('RGB')
    a = np.asarray(im).astype(np.float32)
    lum = a.max(axis=2)
    alpha = np.clip((lum - 6) / (34 - 6), 0, 1)
    alpha = alpha ** 1.35
    rgba = np.dstack([a, alpha * 255]).astype(np.uint8)
    out = Image.fromarray(rgba, 'RGBA')
    bbox = out.getbbox()
    if bbox:
        out = out.crop(bbox)
    return out


def make_bg(w: int, h: int, top=(6, 8, 15), bottom=(16, 19, 35), glow=(108, 92, 236), glow_strength=0.22):
    yy, xx = np.mgrid[0:h, 0:w]
    t = yy / max(1, h - 1)
    top = np.array(top, dtype=np.float32)
    bottom = np.array(bottom, dtype=np.float32)
    rgb = top[None, None, :] * (1 - t[:, :, None]) + bottom[None, None, :] * t[:, :, None]
    diag = 1 - (xx + yy) / (w + h)
    rgb *= (0.92 + 0.16 * diag[:, :, None])
    cx, cy = w * 0.5, h * 0.46
    dist = np.sqrt(((xx - cx) / (w * 0.65)) ** 2 + ((yy - cy) / (h * 0.60)) ** 2)
    radial = np.clip(1 - dist, 0, 1) ** 2
    glow = np.array(glow, dtype=np.float32)
    rgb += glow[None, None, :] * radial[:, :, None] * glow_strength
    vign = np.clip(1 - dist * 0.75, 0, 1) ** 1.5
    rgb *= (0.72 + 0.28 * vign[:, :, None])
    return Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), 'RGB')


def compose(src: Path, out_name: str, size=(1920, 1080), target_w=1600, top=(6, 8, 15), bottom=(16, 19, 35), glow=(108, 92, 236), glow_strength=0.22, crop=None, position=None):
    island = cutout_black(src)
    if crop:
        island = island.crop(crop)
    scale = target_w / island.width
    target_h = max(1, round(island.height * scale))
    island = island.resize((target_w, target_h), Image.LANCZOS)
    canvas = make_bg(size[0], size[1], top, bottom, glow, glow_strength).convert('RGBA')
    glow_img = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    mask = island.split()[3].filter(ImageFilter.GaussianBlur(max(18, target_w // 32)))
    solid = Image.new('RGBA', island.size, (*glow, 105))
    pos = position or ((canvas.width - target_w)//2, (canvas.height - target_h)//2)
    glow_img.paste(solid, pos, mask)
    canvas.alpha_composite(glow_img)
    shadow = Image.new('RGBA', canvas.size, (0, 0, 0, 0))
    smask = island.split()[3].filter(ImageFilter.GaussianBlur(max(10, target_w // 90)))
    solid_shadow = Image.new('RGBA', island.size, (0, 0, 0, 120))
    shadow.paste(solid_shadow, (pos[0] + 12, pos[1] + 24), smask)
    canvas.alpha_composite(shadow)
    canvas.alpha_composite(island, pos)
    canvas.convert('RGB').save(OUT / out_name, 'JPEG', quality=88, optimize=True, progressive=True)
    print(OUT / out_name, size, target_w, target_h)


expanded = BASE / 'roundtrip-1-expanded.png'
collapsed = BASE / 'roundtrip-1-collapsed.png'

compose(expanded, 'winislands-hero-v4-1920x1080.jpg', (1920, 1080), 1600,
        top=(5, 7, 14), bottom=(15, 18, 34), glow=(112, 96, 240), glow_strength=0.24)
compose(collapsed, 'winislands-components-v6-1920x1080.jpg', (1920, 1080), 1200,
        top=(4, 7, 14), bottom=(13, 19, 36), glow=(96, 130, 240), glow_strength=0.22,
        position=(360, 430))
lyrics_box = (168, 68, 430, 132)
compose(expanded, 'winislands-lyrics-v3-1920x1080.jpg', (1920, 1080), 1500,
        top=(5, 7, 15), bottom=(18, 16, 38), glow=(158, 118, 240), glow_strength=0.20,
        crop=lyrics_box, position=(210, 290))
compose(expanded, 'winislands-api-v3-1920x1080.jpg', (1920, 1080), 1550,
        top=(4, 8, 14), bottom=(12, 22, 34), glow=(96, 176, 178), glow_strength=0.20)
compose(expanded, 'winislands-share-v3-1200x1200.jpg', (1200, 1200), 1000,
        top=(5, 7, 14), bottom=(16, 19, 35), glow=(112, 96, 240), glow_strength=0.22,
        position=(100, 330))
