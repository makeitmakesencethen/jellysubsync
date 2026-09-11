#!/usr/bin/env python3
"""S8 — who does an alignment use as its ruler? (settled with the user 2026-09-11)

Two cases, both measured against the live server:

1. **An external .srt next to a file that has an embedded text track.** Before this change the external
   path always used the audio, even when a sibling track was right there; now the sibling is the ruler,
   which is the difference between a result that can be checked and a guess. A +5 s sidecar is built for
   the test so the run has something to write, and the plugin log has to name the sibling reference.

2. **An embedded track in a file with no sibling at all.** The audio is the only ruler, and the result
   cannot be checked against anything — measured earlier at +1780 ms on a subtitle that was already in
   sync. Per the decision, nothing is written: the job reports "unverified" and the library is untouched.

Usage: python3 s8_alignment.py --label after
"""
import argparse
import json
import os
import pathlib
import re
import time

import ss, drive

MEDIA = pathlib.Path("/opt/data/jf12test/media")
SIDECAR = MEDIA / "Helikopterrånet S01E01.swe.srt"
SINGLE_SIDECAR = pathlib.Path("/opt/data/jf12test/media-fixtures/Single Track (2026).SYNCED.eng.srt")


def shift_srt(text, delta_ms):
    """Adds `delta_ms` to every cue's start/end time, so the run has a real shift to apply."""
    def fix(m):
        h, mi, s, ms = (int(x) for x in re.match(r"(\d+):(\d+):(\d+),(\d+)", m.group(0)).groups())
        total = ((h * 60 + mi) * 60 + s) * 1000 + ms + delta_ms
        total = max(0, total)
        return "%02d:%02d:%02d,%03d" % (total // 3600000, total // 60000 % 60, total // 1000 % 60, total % 1000)
    return re.sub(r"\d\d:\d\d:\d\d,\d\d\d", fix, text)


def ensure_sidecar():
    """Writes a +5 s swe sidecar next to the episode, from one the plugin itself produced."""
    source = MEDIA / "Helikopterrånet S01E01.SYNCED.ukr.srt"
    if not source.exists():
        candidates = sorted(MEDIA.glob("Helikopterrånet S01E01.SYNCED.*.srt"))
        source = candidates[0] if candidates else None
    if source is None:
        raise SystemExit("no synced sidecar to build the test fixture from")
    SIDECAR.write_text(shift_srt(source.read_text(errors="replace"), 5000), encoding="utf-8")
    return {"built_from": str(source), "size": SIDECAR.stat().st_size}


def find_item(prefix):
    for it in ss.items(types=["Movie"]):
        if (it.get("Name") or "").startswith(prefix):
            return it
    return None


def run(item_id, index, timeout=1200):
    off = ss.bookmark()
    st, resp, _ = ss.post("/SubSync/Sync", {"itemId": item_id, "subtitleIndex": index})
    job_id = (resp or {}).get("Id") if isinstance(resp, dict) else None
    job = ss.wait_job(job_id, timeout=timeout) if job_id else {"Status": "no-job-id"}
    log = ss.log_since(off)
    return {
        "job_id": job_id,
        "status": job.get("Status"),
        "phase": job.get("Phase"),
        "outcome": (job.get("Outcome") or "")[:240],
        "error": (job.get("Error") or "")[:300],
        "output": job.get("OutputPath"),
        "reference_lines": [l[-220:] for l in log.splitlines()
                            if "reference" in l and ("ffsubsync start" in l or "aligning against" in l)][:4],
        "unverified_lines": [l[-220:] for l in log.splitlines() if "UNVERIFIED" in l][:2],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--label", default="after")
    args = ap.parse_args()
    result = {"label": args.label, "measured_at": time.strftime("%Y-%m-%dT%H:%M:%S")}

    # ---------------- 1. external .srt with a sibling track available ----------------
    movie = find_item("Helikopterrånet")

    # The fixture is built once: rebuilding it every run re-triggers a library scan of a 2.38 GB item,
    # and that scan takes about three minutes (a 180 s poll was not enough and the case measured
    # nothing). If the track is already listed, use it as it stands.
    def swe_tracks():
        return [t for t in drive.tracks(movie["Id"])
                if (t.get("Language") or "").lower() == "swe" and t.get("IsExternal")]

    fixture = None
    target = swe_tracks()
    if not target:
        fixture = ensure_sidecar()
        ss.post("/Library/Refresh")
        deadline = time.time() + 420
        while time.time() < deadline and not target:
            time.sleep(10)
            target = swe_tracks()
    if not target:
        print("WARNING: the swe sidecar never appeared as a track — the scan did not pick it up", flush=True)

    before = sorted(p.name for p in MEDIA.glob("Helikopterrånet S01E01.swe.SYNCED*.srt"))
    case1 = {"fixture": fixture, "external_track": target[0] if target else None,
             "sidecars_before": before}
    if target:
        case1.update(run(movie["Id"], target[0]["Index"]))
    case1["sidecars_after"] = sorted(p.name for p in MEDIA.glob("Helikopterrånet S01E01.swe.SYNCED*.srt"))
    case1["used_a_sibling_reference"] = any("s:" in (l.split("reference=")[-1][:4] if "reference=" in l else "")
                                            for l in case1.get("reference_lines", [])) \
        or any("instead of the audio" in l for l in case1.get("reference_lines", []))
    result["external_with_sibling"] = case1

    # ---------------- 2. embedded track, no sibling: audio only, nothing written ----------------
    single = find_item("Single Track")
    before2 = None
    if SINGLE_SIDECAR.exists():
        before2 = {"size": SINGLE_SIDECAR.stat().st_size, "sha256": ss.sha256(str(SINGLE_SIDECAR))}
    tracks2 = drive.tracks(single["Id"]) if single else []
    case2 = {"sidecar_before": before2, "track": tracks2[0] if tracks2 else None}
    if tracks2:
        case2.update(run(single["Id"], tracks2[0]["Index"]))
    if SINGLE_SIDECAR.exists():
        case2["sidecar_after"] = {"size": SINGLE_SIDECAR.stat().st_size,
                                  "sha256": ss.sha256(str(SINGLE_SIDECAR))}
    case2["sidecar_untouched"] = case2.get("sidecar_before") == case2.get("sidecar_after")
    result["embedded_without_sibling"] = case2

    out = pathlib.Path(__file__).with_name("s8-%s.json" % args.label)
    out.write_text(json.dumps(result, indent=2))
    ss.record("s8-%s" % args.label, result)
    print(json.dumps(result, indent=2))
    print("written: %s" % out)


if __name__ == "__main__":
    main()
