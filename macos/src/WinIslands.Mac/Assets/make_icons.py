# -*- coding: utf-8 -*-
"""生成 WinIslands Mac 版图标（纯 zlib 手写 PNG，无第三方依赖）。
- tray.png: 44x44 单色模板图标（macOS 菜单栏，黑色+透明，系统自动反色）
- app.png: 256x256 应用图标（深色圆角方块 + 紫渐变胶囊）
- AppIcon.icns: 多尺寸应用图标容器（16~512，供 .app 打包使用）
"""
import struct, zlib, os, math

OUT = os.path.join(os.path.dirname(__file__))


def make_png_bytes(width, height, rgba):
    def chunk(tag, data):
        c = struct.pack('>I', len(data)) + tag + data
        c += struct.pack('>I', zlib.crc32(tag + data) & 0xffffffff)
        return c
    raw = b''
    for y in range(height):
        raw += b'\x00' + bytes(rgba[y * width * 4:(y + 1) * width * 4])
    png = b'\x89PNG\r\n\x1a\n'
    png += chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, 8, 6, 0, 0, 0))
    png += chunk(b'IDAT', zlib.compress(raw, 9))
    png += chunk(b'IEND', b'')
    return png


def write_png(path, width, height, rgba):
    with open(path, 'wb') as f:
        f.write(make_png_bytes(width, height, rgba))
    print('wrote', path, width, 'x', height)


def rounded_rect_alpha(x, y, w, h, rx, ry, rw, rh):
    """点(x,y)在圆角矩形内的覆盖率（简单 4x4 超采样）。"""
    hits = 0
    for sx in range(4):
        for sy in range(4):
            px = x + (sx + 0.5) / 4
            py = y + (sy + 0.5) / 4
            cx = min(max(px, rx + rw - rw), rx + rw)  # unused
            # 圆角矩形 SDF 风格判定
            qx = abs(px - (rx + rw / 2)) - (rw / 2 - rh)
            qy = abs(py - (ry + rh / 2)) - (rh / 2 - rh)
            dx = max(qx, 0)
            dy = max(qy, 0)
            d = math.hypot(dx, dy) + min(max(qx, qy), 0)
            if d <= 0:
                hits += 1
    return hits / 16


def capsule(x, y, W, H, pad_x, pad_y, alpha=255):
    """水平胶囊：中心带 + 两侧半圆。返回 (r,g,b,a) 或 None。"""
    cx = W / 2
    cy = H / 2
    half_len = (W - 2 * pad_x) / 2 - (H / 2 - pad_y)
    r = H / 2 - pad_y
    ax = min(abs(x + 0.5 - cx), half_len)
    d = math.hypot(max(abs(x + 0.5 - cx) - half_len, 0), y + 0.5 - cy)
    cov = 0
    for sx in (0.125, 0.375, 0.625, 0.875):
        for sy in (0.125, 0.375, 0.625, 0.875):
            px, py = x + sx, y + sy
            dd = math.hypot(max(abs(px - cx) - half_len, 0), py - cy)
            if dd <= r:
                cov += 1
    if cov == 0:
        return None
    return alpha * cov // 16


def make_tray(path):
    S = 44
    px = bytearray(S * S * 4)
    for y in range(S):
        for x in range(S):
            a = capsule(x, y, S, S, pad_x=3, pad_y=12)
            if a:
                i = (y * S + x) * 4
                px[i:i + 4] = bytes((0, 0, 0, a))
    write_png(path, S, S, px)


def render_app_icon(S):
    """渲染 SxS 应用图标像素（RGBA bytearray）。几何全部按比例缩放。"""
    px = bytearray(S * S * 4)
    corner = S * 56 / 256          # 圆角半径
    cap_w, cap_h = S * 200 / 256, S * 76 / 256
    for y in range(S):
        for x in range(S):
            i = (y * S + x) * 4
            # 背景：圆角方块深色
            bg = 0
            for sx in (0.125, 0.375, 0.625, 0.875):
                for sy in (0.125, 0.375, 0.625, 0.875):
                    px_, py_ = x + sx, y + sy
                    qx = abs(px_ - S / 2) - (S / 2 - corner)
                    qy = abs(py_ - S / 2) - (S / 2 - corner)
                    d = math.hypot(max(qx, 0), max(qy, 0)) + min(max(qx, qy), 0)
                    if d <= 0:
                        bg += 1
            if bg == 0:
                continue
            bg_a = bg * 255 // 16
            # 底色 #17171B
            r, g, b = 0x17, 0x17, 0x1B
            # 胶囊：垂直居中，紫渐变
            a = 0
            cx, cy = S / 2, S / 2
            half_len = cap_w / 2 - cap_h / 2
            rr = cap_h / 2
            for sx in (0.125, 0.375, 0.625, 0.875):
                for sy in (0.125, 0.375, 0.625, 0.875):
                    px_, py_ = x + sx, y + sy
                    dd = math.hypot(max(abs(px_ - cx) - half_len, 0), py_ - cy)
                    if dd <= rr:
                        a += 255
            a //= 16
            if a > 0:
                t = (x / S)
                # 渐变 #6C5CE7 -> #8E7BFF
                pr = int(0x6C + (0x8E - 0x6C) * t)
                pg = int(0x5C + (0x7B - 0x5C) * t)
                pb = int(0xE7 + (0xFF - 0xE7) * t)
                r = (r * (255 - a) + pr * a) // 255
                g = (g * (255 - a) + pg * a) // 255
                b = (b * (255 - a) + pb * a) // 255
            px[i:i + 4] = bytes((r, g, b, bg_a))
    return px


def downsample(px, S):
    """2x 盒式降采样（S 为偶数）。"""
    half = S // 2
    out = bytearray(half * half * 4)
    for y in range(half):
        for x in range(half):
            o = (y * half + x) * 4
            acc = [0, 0, 0, 0]
            for dy in (0, 1):
                for dx in (0, 1):
                    i = ((y * 2 + dy) * S + (x * 2 + dx)) * 4
                    for k in range(4):
                        acc[k] += px[i + k]
            out[o:o + 4] = bytes(v // 4 for v in acc)
    return out


def make_app(path):
    S = 256
    write_png(path, S, S, render_app_icon(S))


def make_icns(path):
    """写 PNG 压缩的 .icns（icp4/icp5/icp6/ic07/ic08/ic09/ic10）。"""
    base = 512
    pixels = {base: render_app_icon(base)}
    cur, size = pixels[base], base
    while size > 16:
        cur = downsample(cur, size)
        size //= 2
        pixels[size] = cur

    entries = [
        (b'icp4', 16), (b'icp5', 32), (b'icp6', 64),
        (b'ic07', 128), (b'ic08', 256), (b'ic09', 512), (b'ic10', 512),
    ]
    body = b''
    for tag, s in entries:
        png = make_png_bytes(s, s, pixels[s])
        body += tag + struct.pack('>I', len(png) + 8) + png
    icns = b'icns' + struct.pack('>I', len(body) + 8) + body
    with open(path, 'wb') as f:
        f.write(icns)
    print('wrote', path, len(icns), 'bytes')


make_tray(os.path.join(OUT, 'tray.png'))
make_app(os.path.join(OUT, 'app.png'))
make_icns(os.path.join(OUT, 'AppIcon.icns'))
