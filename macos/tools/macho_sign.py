#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Mach-O 代码签名离线检查 / ad-hoc 签名工具（macOS 交叉打包配套，仅标准库）。

背景：从 Windows 交叉发布 .NET 自包含 macOS 程序时，SDK 会把 apphost 上
微软预置的 ad-hoc 签名整个剥掉（留下"未签名"的可执行文件），Apple Silicon
内核会直接拒绝启动（SIGKILL，表现为"无法打开"）。本工具按 Apple cs_blobs
格式离线补齐 ad-hoc 签名，结构与微软 runtime pack 中的原始签名完全一致：
version 0x20400 / flags CS_ADHOC / SHA-256 / 4KB 页 / execSeg=MAIN_BINARY /
空 Requirements。（签名块内部字段为大端序。）

用法：
  inspect <file>   解析并重算页面哈希，验证签名是否有效
  sign <file>      写入 ad-hoc 签名（缺 LC_CODE_SIGNATURE 时在头部空隙插入；已有签名原地重建）
  scan <dir>       递归列出目录内所有 Mach-O 及其签名状态
"""

import hashlib
import os
import struct
import sys

LC_SEGMENT_64 = 0x19
LC_CODE_SIGNATURE = 0x1D
CSMAGIC_EMBEDDED_SIGNATURE = 0xFADE0CC0
CSMAGIC_CODEDIRECTORY = 0xFADE0C02
CSMAGIC_REQUIREMENTS = 0xFADE0C01
CS_ADHOC = 0x2
CS_EXECSEG_MAIN_BINARY = 0x1
MH_MAGIC_64 = 0xFEEDFACF
CPU_NAMES = {0x0100000C: "arm64", 0x01000007: "x86_64", 0x01000012: "arm64e"}
HASH_ALGOS = {1: "sha1", 2: "sha256", 3: "sha256", 4: "sha384"}


def _u32(d, o):
    return struct.unpack_from("<I", d, o)[0]


def _u64(d, o):
    return struct.unpack_from("<Q", d, o)[0]


def _be32(d, o):
    return struct.unpack_from(">I", d, o)[0]


def _be64(d, o):
    return struct.unpack_from(">Q", d, o)[0]


class MachO:
    """64 位小端 Mach-O 的最小结构视图（Mach-O 头部字段为小端）。"""

    def __init__(self, data):
        self.d = data
        if _u32(data, 0) != MH_MAGIC_64:
            raise ValueError("不是 64 位小端 Mach-O: magic=%08x" % _u32(data, 0))
        self.cputype = _u32(data, 4)
        self.filetype = _u32(data, 12)
        self.ncmds = _u32(data, 16)
        self.sizeofcmds = _u32(data, 20)
        self.flags = _u32(data, 24)
        self.lc_end = 32 + self.sizeofcmds
        self.segments = []
        self.sig_lc_off = None
        self.sig_dataoff = None
        self.sig_datasize = None
        off = 32
        for _ in range(self.ncmds):
            cmd = _u32(data, off)
            size = _u32(data, off + 4)
            if cmd == LC_SEGMENT_64:
                self.segments.append({
                    "name": data[off + 8:off + 24].split(b"\0")[0].decode("utf-8", "replace"),
                    "vmaddr": _u64(data, off + 24),
                    "vmsize": _u64(data, off + 32),
                    "fileoff": _u64(data, off + 40),
                    "filesize": _u64(data, off + 48),
                    "nsects": _u32(data, off + 64),
                    "lc_off": off,
                })
            elif cmd == LC_CODE_SIGNATURE:
                self.sig_lc_off = off
                self.sig_dataoff = _u32(data, off + 8)
                self.sig_datasize = _u32(data, off + 12)
            off += size

    def min_section_offset(self):
        """最小的节文件偏移（用于校验头部空隙）。无节（零填充）返回 None。"""
        best = None
        for s in self.segments:
            base = s["lc_off"] + 72
            for i in range(s["nsects"]):
                so = base + i * 80
                off = _u32(self.d, so + 48)
                size = _u64(self.d, so + 40)
                if size and off:
                    best = off if best is None else min(best, off)
        return best


def parse_codedirectory(blob):
    if _be32(blob, 0) != CSMAGIC_CODEDIRECTORY:
        raise ValueError("不是 CodeDirectory")
    cd = {
        "length": _be32(blob, 4), "version": _be32(blob, 8), "flags": _be32(blob, 12),
        "hash_offset": _be32(blob, 16), "ident_offset": _be32(blob, 20),
        "n_special": _be32(blob, 24), "n_code": _be32(blob, 28), "code_limit": _be32(blob, 32),
        "hash_size": blob[36], "hash_type": blob[37], "platform": blob[38], "page_size": blob[39],
    }
    end = blob.index(b"\0", cd["ident_offset"])
    cd["ident"] = blob[cd["ident_offset"]:end].decode("utf-8", "replace")
    if cd["version"] >= 0x20100:
        cd["scatter_offset"] = _be32(blob, 44)
    if cd["version"] >= 0x20200:
        cd["team_offset"] = _be32(blob, 48)
    if cd["version"] >= 0x20300:
        cd["code_limit64"] = _be64(blob, 56)
    if cd["version"] >= 0x20400:
        cd["exec_seg_base"] = _be64(blob, 64)
        cd["exec_seg_limit"] = _be64(blob, 72)
        cd["exec_seg_flags"] = _be64(blob, 80)
    return cd


def effective_code_limit(cd):
    if cd.get("code_limit64"):
        return cd["code_limit64"]
    return cd["code_limit"]


def analyze(data):
    """分析一份 thin Mach-O 的签名状态。返回 dict。"""
    m = MachO(data)
    info = {
        "cpu": m.cputype, "filetype": m.filetype, "lc_end": m.lc_end,
        "signed": m.sig_lc_off is not None, "valid": None, "mismatch": 0,
        "cd": None, "dataoff": m.sig_dataoff, "datasize": m.sig_datasize,
        "at_end": m.sig_dataoff is not None and m.sig_dataoff + m.sig_datasize == len(data),
        "size": len(data),
    }
    if not info["signed"]:
        return info
    if info["dataoff"] + info["datasize"] > len(data):
        info["valid"] = False
        return info
    blob = data[info["dataoff"]:info["dataoff"] + info["datasize"]]
    if _be32(blob, 0) != CSMAGIC_EMBEDDED_SIGNATURE:
        info["valid"] = False
        return info
    count = _be32(blob, 8)
    cd_blob = None
    for i in range(count):
        t = _be32(blob, 12 + i * 8)
        o = _be32(blob, 16 + i * 8)
        if t == 0:
            cd_blob = blob[o:]
    if cd_blob is None:
        info["valid"] = False
        return info
    cd = parse_codedirectory(cd_blob)
    info["cd"] = cd
    limit = effective_code_limit(cd)
    page = 1 << cd["page_size"] if cd["page_size"] else limit
    algo = HASH_ALGOS.get(cd["hash_type"], "sha256")
    bad = 0
    for i in range(cd["n_code"]):
        chunk = data[i * page:min((i + 1) * page, limit)]
        h = hashlib.new(algo, chunk).digest()[:cd["hash_size"]]
        stored = cd_blob[cd["hash_offset"] + i * cd["hash_size"]:
                         cd["hash_offset"] + (i + 1) * cd["hash_size"]]
        if h != stored:
            bad += 1
    info["mismatch"] = bad
    info["valid"] = bad == 0
    return info


def analyze_file(path):
    """分析任意文件；非 Mach-O 返回 None；fat 二进制返回逐 slice 的列表。"""
    with open(path, "rb") as f:
        data = f.read()
    if len(data) < 4:
        return None
    magic4 = data[:4]
    if magic4 in (b"\xca\xfe\xba\xbe", b"\xca\xfe\xba\xbf"):
        is64 = magic4 == b"\xca\xfe\xba\xbf"
        n = _be32(data, 4)
        results = []
        off = 8
        for _ in range(n):
            cputype = _be32(data, off)
            if is64:
                offset = _be64(data, off + 8)
                size = _be64(data, off + 16)
                off += 32
            else:
                offset = _be32(data, off + 8)
                size = _be32(data, off + 12)
                off += 20
            results.append((cputype, analyze(data[offset:offset + size])))
        return results
    if magic4 == b"\xcf\xfa\xed\xfe":
        return analyze(data)
    return None


def _fmt_info(info):
    cpu = CPU_NAMES.get(info["cpu"], hex(info["cpu"]))
    if not info["signed"]:
        return "%s 无签名(size=%d)" % (cpu, info["size"])
    status = "有效" if info["valid"] else "无效"
    extra = ""
    if info["cd"]:
        cd = info["cd"]
        extra = " flags=%#x adhoc=%s pages=%d%s" % (
            cd["flags"], bool(cd["flags"] & CS_ADHOC), cd["n_code"],
            "" if info["valid"] else " 不对齐页=%d" % info["mismatch"])
    return "%s %s(size=%d, 签名%d@%d%s)" % (
        cpu, status, info["size"], info["datasize"], info["dataoff"], extra)


def inspect(path):
    print("file : %s" % path)
    r = analyze_file(path)
    if r is None:
        print("不是 Mach-O")
        return 1
    if isinstance(r, list):
        for cputype, info in r:
            print("  [fat slice %s] %s" % (CPU_NAMES.get(cputype, hex(cputype)), _fmt_info(info)))
        return 0
    info = r
    print("  %s" % _fmt_info(info))
    if info["signed"] and info["cd"]:
        cd = info["cd"]
        print("  CD version=%#x flags=%#x ident=%r" % (cd["version"], cd["flags"], cd["ident"]))
        print("  hash_type=%d page_log2=%d n_special=%d code_limit=%d" % (
            cd["hash_type"], cd["page_size"], cd["n_special"], effective_code_limit(cd)))
        if "exec_seg_base" in cd:
            print("  execSeg base=%d limit=%d flags=%#x" % (
                cd["exec_seg_base"], cd["exec_seg_limit"], cd["exec_seg_flags"]))
    return 0


def sign_file(path, ident=None, page_log2=12):
    """写入 ad-hoc 签名（原地）。返回 analyze() 结果。"""
    raw = bytearray(open(path, "rb").read())
    m = MachO(bytes(raw))
    if ident is None:
        ident = os.path.splitext(os.path.basename(path))[0]

    if m.sig_lc_off is not None:
        lc_off = m.sig_lc_off
        content = bytearray(raw[:m.sig_dataoff])
    else:
        lc_off = m.lc_end
        content = bytearray(raw)
        if bytes(content[lc_off:lc_off + 16]) != b"\0" * 16:
            raise SystemExit("头部空隙不足，无法插入 LC_CODE_SIGNATURE: @%d" % lc_off)
        min_sect = m.min_section_offset()
        if min_sect is not None and lc_off + 16 > min_sect:
            raise SystemExit("新增载入命令会与节重叠: lc_end=%d first_sect=%d" % (lc_off, min_sect))
        struct.pack_into("<I", content, 16, m.ncmds + 1)
        struct.pack_into("<I", content, 20, m.sizeofcmds + 16)

    dataoff = (len(content) + 15) & ~15
    content.extend(b"\0" * (dataoff - len(content)))

    ident_b = ident.encode("utf-8") + b"\0"
    page = 1 << page_log2
    n_code = (dataoff + page - 1) // page
    cd_len = 88 + len(ident_b) + n_code * 32
    req_len = 12
    total = 12 + 2 * 8 + cd_len + req_len  # SuperBlob 头 + 2 索引 + CD + Requirements

    # 先回填 LC 与 __LINKEDIT（都要参与页面哈希）
    struct.pack_into("<IIII", content, lc_off, LC_CODE_SIGNATURE, 16, dataoff, total)
    new_eof = dataoff + total
    for seg in m.segments:
        if seg["name"] == "__LINKEDIT":
            new_size = new_eof - seg["fileoff"]
            if new_size > seg["vmsize"]:
                struct.pack_into("<Q", content, seg["lc_off"] + 32, (new_size + 0x3FFF) & ~0x3FFF)
            struct.pack_into("<Q", content, seg["lc_off"] + 48, new_size)

    # 页面哈希（覆盖 [0, dataoff) 的全部最终字节）
    text = next(s for s in m.segments if s["name"] == "__TEXT")

    cd = bytearray(cd_len)
    struct.pack_into(">9I", cd, 0, CSMAGIC_CODEDIRECTORY, cd_len, 0x20400, CS_ADHOC,
                     88 + len(ident_b), 88, 0, n_code, dataoff)
    cd[36] = 32  # hashSize
    cd[37] = 2   # hashType SHA-256
    cd[38] = 0   # platform
    cd[39] = page_log2
    # spare2/scatter/team/spare3/codeLimit64 全 0；execSeg = __TEXT 段
    struct.pack_into(">QQQ", cd, 64, 0, text["filesize"], CS_EXECSEG_MAIN_BINARY)
    cd[88:88 + len(ident_b)] = ident_b
    for i in range(n_code):
        h = hashlib.sha256(bytes(content[i * page:min((i + 1) * page, dataoff)])).digest()
        off = 88 + len(ident_b) + i * 32
        cd[off:off + 32] = h

    sb = bytearray(12 + 2 * 8)
    struct.pack_into(">3I", sb, 0, CSMAGIC_EMBEDDED_SIGNATURE, total, 2)
    struct.pack_into(">2I", sb, 12, 0, 28)
    struct.pack_into(">2I", sb, 20, 2, 28 + cd_len)
    req = struct.pack(">3I", CSMAGIC_REQUIREMENTS, req_len, 0)
    blob = bytes(sb) + bytes(cd) + req
    assert len(blob) == total, (len(blob), total)

    with open(path, "wb") as f:
        f.write(content)
        f.write(blob)
    return analyze(bytes(content) + blob)


def scan(root):
    rc = 0
    for dirpath, _dirnames, filenames in os.walk(root):
        for name in sorted(filenames):
            p = os.path.join(dirpath, name)
            r = analyze_file(p)
            if r is None:
                continue
            rel = os.path.relpath(p, root)
            if isinstance(r, list):
                for cputype, info in r:
                    line = _fmt_info(info)
                    print("%-52s [fat %s] %s" % (rel, CPU_NAMES.get(cputype, hex(cputype)), line))
                    if not info["signed"] or not info["valid"]:
                        rc = 1
            else:
                print("%-52s %s" % (rel, _fmt_info(r)))
                if not r["signed"] or not r["valid"]:
                    rc = 1
    return rc


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    cmd, target = sys.argv[1], sys.argv[2]
    if cmd == "inspect":
        return inspect(target)
    if cmd == "scan":
        return scan(target)
    if cmd == "sign":
        info = sign_file(target)
        ok = info["valid"]
        print("%s -> %s" % (target, _fmt_info(info)))
        return 0 if ok else 1
    print("未知命令: %s" % cmd)
    return 2


if __name__ == "__main__":
    sys.exit(main())
