#!/usr/bin/env python3
"""Build the fixture library for the SubSync backend test, from the real episode.

Real video + real audio + real subtitle text, cut to 120 s so a full run is quick.
Nothing synthetic except the deliberate defects each fixture carries.
"""
import os
import pathlib
import re
import shutil
import ebml
import subprocess
import sys

EP = "/opt/data/jf12test/media/Helikopterrånet S01E01.mkv"
OUT = pathlib.Path("/opt/data/jf12test/media-fixtures")
OUT.mkdir(exist_ok=True)
FF = "ffmpeg"
FP = "ffprobe"


def run(args, check=True):
    p = subprocess.run(args, capture_output=True, text=True)
    if check and p.returncode != 0:
        raise RuntimeError(" ".join(args) + "\n" + p.stderr[-2000:])
    return p


def dur(path):
    p = run([FP, "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", str(path)])
    try:
        return float(p.stdout.strip())
    except Exception:
        return None


# ------------------------------------------------------------------ subtitle text

TC = re.compile(r"(\d\d):(\d\d):(\d\d)[,.](\d\d\d)")


def parse_srt(text):
    cues = []
    for block in re.split(r"\r?\n\r?\n", text.strip()):
        lines = [l for l in block.splitlines() if l.strip()]
        if len(lines) < 2:
            continue
        m = re.match(r"^(.+?)\s*-->\s*(.+?)(\s+.*)?$", lines[1])
        if not m:
            continue
        a = TC.search(m.group(1))
        b = TC.search(m.group(2))
        if not a or not b:
            continue
        cues.append(((int(a[1]) * 3600 + int(a[2]) * 60 + int(a[3])) * 1000 + int(a[4]),
                     (int(b[1]) * 3600 + int(b[2]) * 60 + int(b[3])) * 1000 + int(b[4]),
                     lines[2:]))
    return cues


def fmt(ms):
    if ms < 0:
        ms = 0
    h, ms2 = divmod(ms, 3600000)
    m, ms2 = divmod(ms2, 60000)
    s, ms3 = divmod(ms2, 1000)
    return "%02d:%02d:%02d,%03d" % (h, m, s, ms3)


def render_srt(cues):
    out = []
    for i, (a, b, txt) in enumerate(cues, 1):
        out.append("%d\n%s --> %s\n%s\n" % (i, fmt(a), fmt(b), "\n".join(txt)))
    return "\n".join(out)


def shift(cues, ms):
    return [(max(0, a + ms), max(0, b + ms), t) for a, b, t in cues]


def window(cues, start_ms, end_ms, rebase=True):
    out = []
    for a, b, t in cues:
        if b <= start_ms or a >= end_ms:
            continue
        out.append((a - start_ms if rebase else a, b - start_ms if rebase else b, t))
    return out


# ------------------------------------------------------------------ base file

def build_base():
    base = OUT / "base120.mkv"
    if base.exists():
        return base
    run([FF, "-v", "error", "-y", "-ss", "600", "-t", "120", "-i", EP,
         "-map", "0:0", "-map", "0:1", "-c", "copy", "-sn", str(base)])
    return base


def build_en_srt():
    p = OUT / "ep-eng.srt"
    if not p.exists():
        run([FF, "-v", "error", "-y", "-i", EP, "-map", "0:s:1", "-f", "srt", str(p)])
    return p


def main():
    base = build_base()
    en = build_en_srt()
    cues = window(parse_srt(en.read_text(errors="replace")), 600000, 720000)
    print("base %s dur=%s, windowed english cues: %d" % (base, dur(base), len(cues)))
    (OUT / "eng_120.srt").write_text(render_srt(cues))

    # 1/2. external sidecar fixtures: copy mode (+5 s) and replace mode (+7 s)
    for name, ms in (("External Copy (2026)", 5000), ("External Replace (2026)", 7000)):
        mk = OUT / (name + ".mkv")
        srt = OUT / (name + ".en.srt")
        if not mk.exists():
            shutil.copy2(base, mk)
        srt.write_text(render_srt(shift(cues, ms)))

    # 9. a file with a single subtitle track (no second track to use as a reference)
    one = OUT / "Single Track (2026).mkv"
    if not one.exists():
        run([FF, "-v", "error", "-y", "-i", str(base), "-i", str(OUT / "eng_120.srt"),
             "-map", "0:v", "-map", "0:a", "-map", "1:s", "-c", "copy",
             "-c:s", "srt", "-metadata:s:s:0", "language=eng",
             "-metadata:s:s:0", "title=English", str(one)])

    # 5. MP4 with embedded mov_text subtitles
    mp4 = OUT / "Mp4 Test (2026).mp4"
    if not mp4.exists():
        run([FF, "-v", "error", "-y", "-i", str(base), "-i", str(OUT / "eng_120.srt"),
             "-map", "0:v", "-map", "0:a", "-map", "1:s", "-c:v", "copy", "-c:a", "aac",
             "-c:s", "mov_text", "-metadata:s:s:0", "language=eng", str(mp4)])

    # 6. bitmap subtitles (DVD SUB) — built by make_vobsub.py (no bitmap subtitle encoder exists)

    # 7. a subtitle track that carries no text at all
    empty = OUT / "Empty Track (2026).mkv"
    if not empty.exists():
        # ffmpeg refuses an entirely empty SRT, so give the track one cue with no visible text
        (OUT / "empty.srt").write_text("1\n00:00:00,000 --> 00:00:12,000\n \n\n")
        run([FF, "-v", "error", "-y", "-i", str(base), "-i", str(OUT / "empty.srt"),
             "-map", "0:v", "-map", "0:a", "-map", "1:s", "-c", "copy", "-c:s", "srt",
             "-metadata:s:s:0", "language=eng", str(empty)])

    # 8. ASS/SSA text subtitles
    ass = OUT / "Ass Track (2026).mkv"
    if not ass.exists():
        run([FF, "-v", "error", "-y", "-i", str(OUT / "eng_120.srt"), str(OUT / "eng_120.ass")])
        run([FF, "-v", "error", "-y", "-i", str(base), "-i", str(OUT / "eng_120.ass"),
             "-map", "0:v", "-map", "0:a", "-map", "1:s", "-c", "copy", "-c:s", "ass",
             "-metadata:s:s:0", "language=eng", str(ass)])

    # 3. no cue index at all -> the cluster walk
    noidx = OUT / "No Index (2026).mkv"
    if not noidx.exists():
        run([FF, "-v", "error", "-y", "-i", str(base), "-i", str(OUT / "eng_120.srt"),
             "-map", "0:v", "-map", "0:a", "-map", "1:s", "-c", "copy", "-c:s", "srt",
             "-metadata:s:s:0", "language=eng", str(noidx)])
        n = ebml.strip_cues(noidx)
        print("  stripped %d bytes of Cues from %s" % (n, noidx.name))

    # 4. a truncated / garbage tail
    trunc = OUT / "Truncated (2026).mkv"
    if not trunc.exists():
        data = (OUT / "eng_120.srt")
        run([FF, "-v", "error", "-y", "-i", str(base), "-i", str(data),
             "-map", "0:v", "-map", "0:a", "-map", "1:s", "-c", "copy", "-c:s", "srt",
             "-metadata:s:s:0", "language=eng", str(trunc)])
        with open(trunc, "ab") as f:
            f.write(b"\x1a\x45\xdf\xa3" + b"\xff" * 4096)

    # 10. the D17 mixed-index fixture, rebuilt from the fixture base so it is small
    mixed_src = OUT / "Mixed Index (2026).mkv"
    if not mixed_src.exists():
        run([FF, "-v", "error", "-y", "-i", str(base), "-i", str(OUT / "eng_120.srt"),
             "-map", "0:v", "-map", "0:a", "-map", "1:s", "-c", "copy", "-c:s", "srt",
             "-metadata:s:s:0", "language=eng", str(mixed_src)])

    # true cue count of the real 2-minute subtitle, for parity checks
    print("eng_120 cue count:", len(cues))
    for p in sorted(OUT.iterdir()):
        print("  %-40s %10d" % (p.name, p.stat().st_size))


if __name__ == "__main__":
    main()
