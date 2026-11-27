from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter
import numpy as np

OUT = Path(r'E:\MyFiles\Program\WinIsland\website\assets\promos')
OUT.mkdir(parents=True, exist_ok=True)
SS = 2


def bg(size, top=(5,7,14), bottom=(16,20,36), accent=(112,96,240), strength=0.20):
    w,h=size
    yy,xx=np.mgrid[0:h,0:w]
    t=yy/max(1,h-1)
    top=np.array(top,dtype=np.float32); bottom=np.array(bottom,dtype=np.float32)
    rgb=top[None,None,:]*(1-t[:,:,None])+bottom[None,None,:]*t[:,:,None]
    diag=1-(xx+yy)/(w+h)
    rgb*=0.96+0.08*diag[:,:,None]
    cx,cy=w*.5,h*.46
    dist=np.sqrt(((xx-cx)/(w*.7))**2+((yy-cy)/(h*.65))**2)
    radial=np.clip(1-dist,0,1)**2
    rgb+=np.array(accent,dtype=np.float32)[None,None,:]*radial[:,:,None]*strength
    vign=np.clip(1-dist*.8,0,1)**1.6
    rgb*=0.74+0.26*vign[:,:,None]
    img=Image.fromarray(np.clip(rgb,0,255).astype(np.uint8),'RGB')
    noise=Image.effect_noise((w,h),10).convert('RGB')
    img=Image.blend(img,noise,0.008)
    return img.convert('RGBA')


def mask_round(size,r):
    m=Image.new('L',size,0); ImageDraw.Draw(m).rounded_rectangle((0,0,size[0]-1,size[1]-1),radius=r,fill=255); return m


def lin_grad(size,c1,c2,angle=30):
    w,h=size; yy,xx=np.mgrid[0:h,0:w]
    t=(xx*np.cos(np.deg2rad(angle))+yy*np.sin(np.deg2rad(angle)))/(w+h)
    t=np.clip((t-t.min())/(np.ptp(t) if np.ptp(t) else 1),0,1)
    c1=np.array(c1,dtype=np.float32); c2=np.array(c2,dtype=np.float32)
    rgb=c1[None,None,:]*(1-t[:,:,None])+c2[None,None,:]*t[:,:,None]
    return Image.fromarray(np.clip(rgb,0,255).astype(np.uint8),'RGB')


def capsule(canvas, box, accent=(112,96,240), reflection=True):
    x0,y0,x1,y1=[int(v*SS) for v in box]; w,h=x1-x0,y1-y0; r=min(w,h)//2
    # glow
    glow=Image.new('RGBA',canvas.size,(0,0,0,0))
    gm=mask_round((w,h),r).filter(ImageFilter.GaussianBlur(max(14,w//24)))
    solid=Image.new('RGBA',(w,h),(*accent,85))
    glow.paste(solid,(x0,y0),gm); canvas.alpha_composite(glow)
    # body
    bm=mask_round((w,h),r)
    fill=lin_grad((w,h),(30,38,60),(10,14,24),angle=40).convert('RGBA')
    canvas.paste(fill,(x0,y0),bm)
    # border
    bw,bh=w+6,h+6; bm2=mask_round((bw,bh),r+3); diff=Image.new('L',(bw,bh),0); diff.paste(255,(0,0),bm2); diff.paste(0,(3,3),bm)
    bl=Image.new('RGBA',canvas.size,(0,0,0,0)); bl.paste(Image.new('RGBA',(bw,bh),(255,255,255,45)),(x0-3,y0-3),diff); canvas.alpha_composite(bl)
    # top highlight
    hl=Image.new('RGBA',canvas.size,(0,0,0,0)); hw,hh=int(w*.84),max(8,int(h*.16)); hm=mask_round((hw,hh),hh//2).filter(ImageFilter.GaussianBlur(7))
    hl.paste(Image.new('RGBA',(hw,hh),(255,255,255,28)),(x0+(w-hw)//2,y0+int(h*.09)),hm); canvas.alpha_composite(hl)
    # bottom reflection
    if reflection:
        refl=Image.new('RGBA',canvas.size,(0,0,0,0))
        rm=mask_round((w,h),r).transpose(Image.FLIP_TOP_BOTTOM).filter(ImageFilter.GaussianBlur(9))
        solid=Image.new('RGBA',(w,h),(255,255,255,12))
        refl.paste(solid,(x0,y1+int(h*.18)),rm)
        canvas.alpha_composite(refl)
    return canvas


def finish(img,name):
    img=img.resize((img.width//SS,img.height//SS),Image.LANCZOS)
    img.convert('RGB').save(OUT/name,'JPEG',quality=91,optimize=True,progressive=True)
    print(OUT/name,(img.width,img.height))

# hero
size=(1920*SS,1080*SS)
c=bg(size,top=(4,6,13),bottom=(15,19,35),accent=(112,96,240),strength=.22)
capsule(c,(430,410,1490,650),accent=(112,96,240),reflection=True)
finish(c,'winislands-hero-v6-1920x1080.jpg')

# components
c=bg(size,top=(4,7,14),bottom=(12,19,36),accent=(96,130,240),strength=.20)
capsule(c,(330,300,810,420),accent=(96,130,240),reflection=True)
capsule(c,(1110,300,1590,420),accent=(178,132,255),reflection=True)
capsule(c,(620,610,1300,730),accent=(96,176,178),reflection=True)
finish(c,'winislands-components-v8-1920x1080.jpg')

# lyrics
c=bg(size,top=(5,7,15),bottom=(18,16,38),accent=(158,118,240),strength=.20)
capsule(c,(480,390,1440,630),accent=(158,118,240),reflection=True)
l=Image.new('RGBA',c.size,(0,0,0,0)); d=ImageDraw.Draw(l)
for i,(x0,x1,y) in enumerate([(640,860,470),(900,1120,500),(700,920,530),(980,1200,560)]):
    d.rounded_rectangle((x0*SS,y*SS,x1*SS,(y+10)*SS),radius=5*SS,fill=(255,255,255,68 if i%2 else 100))
l=l.filter(ImageFilter.GaussianBlur(2)); c.alpha_composite(l)
finish(c,'winislands-lyrics-v5-1920x1080.jpg')

# api
c=bg(size,top=(4,8,14),bottom=(11,21,34),accent=(96,176,178),strength=.20)
capsule(c,(470,400,1450,640),accent=(96,176,178),reflection=True)
l=Image.new('RGBA',c.size,(0,0,0,0)); d=ImageDraw.Draw(l)
nodes=[(620,520),(760,450),(960,560),(1160,470),(1300,540)]
for a,b in zip(nodes[:-1],nodes[1:]): d.line((a[0]*SS,a[1]*SS,b[0]*SS,b[1]*SS),fill=(255,255,255,34),width=2*SS)
for x,y in nodes:
    r=10*SS; d.ellipse((x*SS-r,y*SS-r,x*SS+r,y*SS+r),fill=(255,255,255,85))
l=l.filter(ImageFilter.GaussianBlur(2)); c.alpha_composite(l)
finish(c,'winislands-api-v5-1920x1080.jpg')

# share
size=(1200*SS,1200*SS)
c=bg(size,top=(5,7,14),bottom=(16,19,35),accent=(112,96,240),strength=.22)
capsule(c,(200,500,1000,680),accent=(112,96,240),reflection=True)
finish(c,'winislands-share-v5-1200x1200.jpg')
