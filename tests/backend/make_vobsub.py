#!/usr/bin/env python3
"""Generate a minimal VobSub (.idx + .sub) pair, then mux it into an MKV as a dvd_subtitle
track — the bitmap-subtitle case that has to be refused cleanly.

There is no PGS/VobSub *encoder* in ffmpeg, so the SPU packets are written by hand.
"""
import pathlib
import struct
import subprocess
import sys

OUT = pathlib.Path("/opt/data/jf12test/media-fixtures")


def spu(width=720, height=480):
    """One SPU: a filled rectangle of colour 1 for the whole 5 s at 0 s."""
    # 2-bit pixels, one 4-pixel group per byte. 720 px -> 180 bytes per line, 1 line.
    line = bytes([0xF1]) * (width // 4)          # run of 16 px of colour 1, x45 lines
    pixels = b""
    for _ in range(4):
        pixels += line + bytes([0xF0])            # 0xF0 = end of line, fill colour 0
    # control sequence: date, palette, alpha, coords, offsets, duration, end
    ctrl = b"\x00\x00"                            # date
    ctrl += b"\x00\x01" + bytes([0xFF, 0xFF, 0x00])   # hmm placeholder, fixed below
    ctrl = b"\x00\x00"
    ctrl += b"\x01" + bytes([0x07, 0x07, 0x07, 0x07])         # set palette (4 entries)
    ctrl += b"\x02" + bytes([0x0F, 0x0F, 0x0F, 0x0F])         # set alpha (4 entries)
    ctrl += b"\x03" + struct.pack(">HHH", (1 << 12) + 719, (2 << 12) + 399, 720)
    ctrl += b"\x04" + struct.pack(">HH", 2, 2 + len(pixels))  # pixel data offsets
    ctrl += b"\x05" + struct.pack(">HH", 2, 2 + len(pixels))
    ctrl += b"\xFF"
    payload = struct.pack(">H", 2) + pixels + ctrl + b"\x00\x00"
    return struct.pack(">H", len(payload)) + payload


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    sub = OUT / "bitmap.sub"
    idx = OUT / "bitmap.idx"
    pkt = spu()
    sub.write_bytes(pkt)
    idx.write_text(
        "VobSub index file, v7 (do not modify this line!)\n"
        "size: 720x480\n"
        "palette: 000000, 828282, 828282, 828282, 828282, 828282, 828282, 828282, "
        "828282, 828282, 828282, 828282, 828282, 828282, 828282, 828282\n"
        "langidx: 0\n"
        "id: en, index: 0\n"
        "timestamp: 00:00:00:000, filepos: 000000000\n"
        "timestamp: 00:00:05:000, filepos: 000000000\n"
    )
    base = OUT / "base120.mkv"
    dst = OUT / "Bitmap Subs (2026).mkv"
    p = subprocess.run(["ffmpeg", "-v", "error", "-y", "-i", str(base), "-i", str(idx),
                        "-map", "0:v", "-map", "0:a", "-map", "1:s", "-c:v", "copy",
                        "-c:a", "copy", "-c:s", "copy", "-metadata:s:s:0", "language=eng",
                        str(dst)], capture_output=True, text=True)
    print("ffmpeg rc", p.returncode, p.stderr[-800:])
    q = subprocess.run(["ffprobe", "-v", "error", "-show_entries",
                        "stream=index,codec_type,codec_name", "-of", "csv", str(dst)],
                       capture_output=True, text=True)
    print(q.stdout or q.stderr)


if __name__ == "__main__":
    main()
