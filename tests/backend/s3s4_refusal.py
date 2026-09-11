#!/usr/bin/env python3
"""S3 and S4 — the two refusals that keep a wrong or empty file out of the library.

S3: a reference-derived shift bigger than `MaxSubtitleReferenceOffsetSeconds` (default 30 s) must be
    refused with the measured numbers instead of written. Repro: the forced eng track of the 2.38 GB
    episode, whose alignment against sibling track `s:1` comes out at -59 080 ms. What is checked:
    the job's status/error, the plugin log's REFUSED line, and that the sidecar already in the library
    is byte-for-byte the same file afterwards (or absent, and still absent).

S4: a job that produces no subtitles must leave nothing next to the media. Repro: the `Empty Track`
    fixture, whose embedded track holds no text. What is checked: the job fails with the verification
    message and `*.SYNCED*.srt` / `*.part` files are accounted for in the folder afterwards, with no
    0-byte subtitle among them.

Usage: python3 s3s4_refusal.py --label after
"""
import argparse
import json
import os
import pathlib
import time

import ss, drive

EPISODE = drive.FILE["heli_movie"]
FORCED_TRACK_INDEX = 4          # the forced eng track whose reference alignment is -59 080 ms
EPISODE_SIDECAR = pathlib.Path(str(EPISODE)).with_name("Helikopterrånet S01E01.SYNCED.eng.srt")
FIXTURE_DIR = pathlib.Path("/opt/data/jf12test/media-fixtures")


def snapshot(path):
    p = pathlib.Path(path)
    if not p.exists():
        return None
    return {"size": p.stat().st_size, "sha256": ss.sha256(str(p))}


def sidecars_in(folder):
    out = []
    for name in sorted(os.listdir(folder)):
        if ".SYNCED" in name and name.endswith((".srt", ".part")):
            p = pathlib.Path(folder) / name
            out.append({"name": name, "size": p.stat().st_size})
    return out


def run_one(item_id, index, label, timeout=900):
    off = ss.bookmark()
    st, resp, _ = ss.post("/SubSync/Sync", {"itemId": item_id, "subtitleIndex": index})
    # The queue answers with the job record itself: {"Id": …, "Status": "Queued", …}.
    job_id = (resp or {}).get("Id") if isinstance(resp, dict) else None
    job = ss.wait_job(job_id, timeout=timeout) if job_id else {"Status": "no-job-id", "body": resp}
    log = ss.log_since(off)
    return {
        "label": label,
        "http": st,
        "job_id": job_id,
        "status": job.get("Status"),
        "phase": job.get("Phase"),
        "error": (job.get("Error") or "")[:400],
        "outcome": (job.get("Outcome") or "")[:300],
        "output": job.get("OutputPath"),
        "refused_log": [l[-260:] for l in log.splitlines() if "REFUSED" in l][:3],
        "notes": [l[-200:] for l in log.splitlines() if "note:" in l][:3],
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--label", default="after")
    args = ap.parse_args()

    result = {"label": args.label, "episode_dll": ss.get("/SubSync/InstallationStatus")[1],
              "measured_at": time.strftime("%Y-%m-%dT%H:%M:%S")}

    # ---------------- S3: a -59 s reference alignment must be refused ----------------
    # The track is picked by what it is, not by its index: Jellyfin's MediaStream.Index is renumbered
    # whenever the item's stream list changes (adding a sidecar moved this item's subtitles from 4..54
    # to 8..57), so a hard-coded index silently measures a different track.
    before = snapshot(EPISODE_SIDECAR)
    tracks = drive.tracks(drive.ITEM["heli_movie"])
    forced = [t for t in tracks if t.get("IsForced") and (t.get("Language") or "") == "eng"]
    forced_index = forced[0]["Index"] if forced else FORCED_TRACK_INDEX
    s3 = run_one(drive.ITEM["heli_movie"], forced_index, "S3")
    after = snapshot(EPISODE_SIDECAR)
    s3["sidecar_before"] = before
    s3["sidecar_after"] = after
    s3["sidecar_unchanged"] = before == after
    s3["track"] = forced[0] if forced else None
    result["S3"] = s3

    # ---------------- S4: a job with no subtitle text must leave nothing ----------------
    empty_item = None
    for it in ss.items(types=["Movie"]):
        if (it.get("Name") or "").startswith("Empty Track"):
            empty_item = it
    s4 = {"fixture": empty_item["Path"] if empty_item else None}
    if empty_item:
        folder = pathlib.Path(empty_item["Path"]).parent
        s4["sidecars_before"] = sidecars_in(folder)
        t = drive.tracks(empty_item["Id"])
        s4["run"] = run_one(empty_item["Id"], t[0]["Index"], "S4") if t else {"error": "no tracks"}
        time.sleep(1)
        s4["sidecars_after"] = sidecars_in(folder)
        s4["zero_byte_left"] = [s for s in s4["sidecars_after"] if s["size"] == 0]
    result["S4"] = s4

    out = pathlib.Path(__file__).with_name("s3s4-%s.json" % args.label)
    out.write_text(json.dumps(result, indent=2))
    ss.record("s3s4-%s" % args.label, result)
    print(json.dumps(result, indent=2))
    print("written: %s" % out)


if __name__ == "__main__":
    main()
