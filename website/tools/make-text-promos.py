from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter, ImageFont
import numpy as np

OUT = Path(r'E:\MyFiles\Program\WinIsland\website\assets\promos')
OUT.mkdir(parents=True, exist_ok=True)
SS = 2
FONT_BOLD = r'C:\Windows\Fonts\msyhbd.ttc'
FONT_REG = r'C:\Windows\Fonts\msyh.ttc'


def bg(size, top=(5,7,14), bottom=(16,20,36), accent=(112,96,240), strength=0.20):
    w, h = size
    yy, xx = np.mgrid[0:h, 0:w]
    t = yy / max(1, h - 1)
    top = np.array(top, dtype=np.float32)
    bottom = np.array(bottom, dtype=np.float32)
    rgb = top[None, None, :] * (1 - t[:, :, None]) + bottom[None, None, :] * t[:, :, None]
    diag = 1 - (xx + yy) / (w + h)
    rgb *= 0.96 + 0.08 * diag[:, :, None]
    cx, cy = w * .5, h * .46
    dist = np.sqrt(((xx - cx)/(w*.72))**2 + ((yy - cy)/(h*.68))**2)
    radial = np.clip(1 - dist, 0, 1) ** 2
    rgb += np.array(accent, dtype=np.float32)[None, None, :] * radial[:, :, None] * strength
    vign = np.clip(1 - dist * .82, 0, 1) ** 1.6
    rgb *= 0.76 + 0.24 * vign[:, :, None]
    img = Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), 'RGB')
    noise = Image.effect_noise((w, h), 10).convert('RGB')
    img = Image.blend(img, noise, 0.008)
    return img.convert('RGBA')


def mask_round(size, r):
    m = Image.new('L', size, 0)
    ImageDraw.Draw(m).rounded_rectangle((0, 0, size[0]-1, size[1]-1), radius=r, fill=255)
    return m


def lin_grad(size, c1, c2, angle=30):
    w, h = size
    yy, xx = np.mgrid[0:h, 0:w]
    t = (xx*np.cos(np.deg2rad(angle)) + yy*np.sin(np.deg2rad(angle))) / (w+h)
    t = np.clip((t - t.min())/(np.ptp(t) if np.ptp(t) else 1), 0, 1)
    c1 = np.array(c1, dtype=np.float32)
    c2 = np.array(c2, dtype=np.float32)
    rgb = c1[None, None, :]*(1-t[:, :, None]) + c2[None, None, :]*t[:, :, None]
    return Image.fromarray(np.clip(rgb, 0, 255).astype(np.uint8), 'RGB')


def capsule(canvas, box, accent=(112,96,240), reflection=True):
    x0, y0, x1, y1 = [int(v*SS) for v in box]
    w, h = x1-x0, y1-y0
    r = min(w, h)//2
    glow = Image.new('RGBA', canvas.size, (0,0,0,0))
    gm = mask_round((w,h), r).filter(ImageFilter.GaussianBlur(max(14, w//24)))
    solid = Image.new('RGBA', (w,h), (*accent, 85))
    glow.paste(solid, (x0,y0), gm)
    canvas.alpha_composite(glow)
    bm = mask_round((w,h), r)
    fill = lin_grad((w,h), (30,38,60), (10,14,24), angle=40).convert('RGBA')
    canvas.paste(fill, (x0,y0), bm)
    bw, bh = w+6, h+6
    bm2 = mask_round((bw,bh), r+3)
    diff = Image.new('L', (bw,bh), 0)
    diff.paste(255, (0,0), bm2)
    diff.paste(0, (3,3), bm)
    bl = Image.new('RGBA', canvas.size, (0,0,0,0))
    bl.paste(Image.new('RGBA', (bw,bh), (255,255,255,45)), (x0-3,y0-3), diff)
    canvas.alpha_composite(bl)
    hl = Image.new('RGBA', canvas.size, (0,0,0,0))
    hw, hh = int(w*.84), max(8, int(h*.16))
    hm = mask_round((hw,hh), hh//2).filter(ImageFilter.GaussianBlur(7))
    hl.paste(Image.new('RGBA',(hw,hh),(255,255,255,28)), (x0+(w-hw)//2, y0+int(h*.09)), hm)
    canvas.alpha_composite(hl)
    if reflection:
        refl = Image.new('RGBA', canvas.size, (0,0,0,0))
        rm = mask_round((w,h), r).transpose(Image.FLIP_TOP_BOTTOM).filter(ImageFilter.GaussianBlur(9))
        refl.paste(Image.new('RGBA',(w,h),(255,255,255,12)), (x0, y1+int(h*.18)), rm)
        canvas.alpha_composite(refl)
    return canvas


def text_block(canvas, title, subtitle, y=400, title_size=96, sub_size=36, accent_line=True):
    d = ImageDraw.Draw(canvas)
    title_font = ImageFont.truetype(FONT_BOLD, int(title_size*SS))
    sub_font = ImageFont.truetype(FONT_REG, int(sub_size*SS))
    cx = canvas.width // 2
    # soft shadow
    shadow = Image.new('RGBA', canvas.size, (0,0,0,0))
    ds = ImageDraw.Draw(shadow)
    ds.text((cx, int(y*SS)), title, font=title_font, fill=(0,0,0,110), anchor='mm')
    shadow = shadow.filter(ImageFilter.GaussianBlur(14))
    canvas.alpha_composite(shadow)
    d.text((cx, int(y*SS)), title, font=title_font, fill=(255,255,255,240), anchor='mm')
    if accent_line:
        line_w = int(72*SS)
        line_h = max(3, int(3*SS))
        d.rounded_rectangle((cx-line_w//2, int((y+72)*SS), cx+line_w//2, int((y+72)*SS)+line_h), radius=line_h//2, fill=(178,132,255,150))
    shadow2 = Image.new('RGBA', canvas.size, (0,0,0,0))
    ds2 = ImageDraw.Draw(shadow2)
    ds2.text((cx, int((y+112)*SS)), subtitle, font=sub_font, fill=(0,0,0,90), anchor='mm')
    shadow2 = shadow2.filter(ImageFilter.GaussianBlur(10))
    canvas.alpha_composite(shadow2)
    d.text((cx, int((y+112)*SS)), subtitle, font=sub_font, fill=(226,230,240,215), anchor='mm')
    return canvas


def finish(img, name):
    img = img.resize((img.width//SS, img.height//SS), Image.LANCZOS)
    img.convert('RGB').save(OUT/name, 'JPEG', quality=91, optimize=True, progressive=True)
    print(OUT/name, (img.width, img.height))


# hero
size = (1920*SS, 1080*SS)
c = bg(size, top=(4,6,13), bottom=(15,19,35), accent=(112,96,240), strength=.22)
text_block(c, '灵动来到 Windows', '把 iPhone 的灵动岛，带到你的桌面', y=370)
capsule(c, (620, 660, 1300, 800), accent=(112,96,240), reflection=True)
finish(c, 'winislands-hero-text-v1-1920x1080.jpg')

# components
c = bg(size, top=(4,7,14), bottom=(12,19,36), accent=(96,130,240), strength=.20)
text_block(c, '一切皆可上岛', '组件、歌词、提醒，全部收纳', y=340)
capsule(c, (540, 620, 830, 720), accent=(96,130,240), reflection=True)
capsule(c, (1090, 620, 1380, 720), accent=(178,132,255), reflection=True)
capsule(c, (815, 760, 1105, 860), accent=(96,176,178), reflection=True)
finish(c, 'winislands-components-text-v1-1920x1080.jpg')

# lyrics
c = bg(size, top=(5,7,15), bottom=(18,16,38), accent=(158,118,240), strength=.20)
text_block(c, '歌词跟着光走', '逐字高亮，像现场一样', y=340)
capsule(c, (620, 640, 1300, 780), accent=(158,118,240), reflection=True)
l = Image.new('RGBA', c.size, (0,0,0,0)); d = ImageDraw.Draw(l)
for i,(x0,x1,y) in enumerate([(760,900,680),(920,1060,710),(840,980,740),(1000,1140,770)]):
    d.rounded_rectangle((x0*SS,y*SS,x1*SS,(y+10)*SS), radius=5*SS, fill=(255,255,255,70 if i%2 else 105))
l = l.filter(ImageFilter.GaussianBlur(2)); c.alpha_composite(l)
finish(c, 'winislands-lyrics-text-v1-1920x1080.jpg')

# api
c = bg(size, top=(4,8,14), bottom=(11,21,34), accent=(96,176,178), strength=.20)
text_block(c, '开发者也能上岛', '本地 API，一行推送', y=340)
capsule(c, (620, 640, 1300, 780), accent=(96,176,178), reflection=True)
l = Image.new('RGBA', c.size, (0,0,0,0)); d = ImageDraw.Draw(l)
nodes = [(760,700),(860,660),(960,710),(1060,670),(1160,700)]
for a,b in zip(nodes[:-1], nodes[1:]):
    d.line((a[0]*SS,a[1]*SS,b[0]*SS,b[1]*SS), fill=(255,255,255,34), width=2*SS)
for x,y in nodes:
    r=10*SS; d.ellipse((x*SS-r,y*SS-r,x*SS+r,y*SS+r), fill=(255,255,255,85))
l = l.filter(ImageFilter.GaussianBlur(2)); c.alpha_composite(l)
finish(c, 'winislands-api-text-v1-1920x1080.jpg')

# share
size = (1200*SS, 1200*SS)
c = bg(size, top=(5,7,14), bottom=(16,19,35), accent=(112,96,240), strength=.22)
text_block(c, 'WinIslands', 'Windows 上的灵动岛', y=460, title_size=88, sub_size=34)
capsule(c, (330, 720, 870, 850), accent=(112,96,240), reflection=True)
finish(c, 'winislands-share-text-v1-1200x1200.jpg')
