"""Matrix driver helpers: run a sync job / batch and capture everything the report needs."""
import json
import os
import pathlib
import re
import subprocess
import time

import ss

ITEM = {
    "test_movie": "cdeab6ea086501aa1dc99a1e54ea38a3",
    "embedded": "3ba2f1803ba81a3a302e00b82abfa2cc",
    "signs": "d3c9918c3eb575b564631d6e90f8dbd5",
    "wide": "a959ab31172bb05536d6464c0ad9f287",
    "heli_movie": "7dffdc11-79c6-0bc0-d778-5db9dbd958f2",
    "ep1": "09ec2bcb5fe2907ba657aff310c07b9e",
    "ep2": "1cc6d368b4bedff44f247e42a3ab3730",
    "series": None,
    "season": None,
    "mixed": "4b3073af-489b-08fc-cac6-0ae126688242",
}
for it in ss.items(types=["Series"]):
    if it["Name"].startswith("The Helicopter"):
        ITEM["series"] = it["Id"]
for it in ss.items(types=["Season"]):
    ITEM["season"] = it["Id"]

FILE = {
    "test_movie": "/opt/data/jf12test/media/Test Movie (2026).mkv",
    "embedded": "/opt/data/jf12test/media/Embedded Test (2026).mkv",
    "signs": "/opt/data/jf12test/media/Signs Test (2026).mkv",
    "wide": "/opt/data/jf12test/media/Wide Multi-Language (2026).mkv",
    "heli_movie": "/opt/data/jf12test/media/Helikopterrånet S01E01.mkv",
    "ep1": "/opt/data/jf12test/media-tv/The Helicopter Heist/Season 1/The Helicopter Heist S01E01.mkv",
    "ep2": "/opt/data/jf12test/media-tv/The Helicopter Heist/Season 1/The Helicopter Heist S01E02.mkv",
    "mixed": "/opt/data/jf12test/media/Mixed Index Test (2026).mkv",
}


def tracks(item_id):
    st, tr, _ = ss.get("/SubSync/Subtitles/%s" % item_id)
    return tr if isinstance(tr, list) else []


def queue(item_id, index, mode=None, label=None):
    body = {"itemId": item_id, "subtitleIndex": index}
    if mode:
        body["mode"] = mode
    return ss.post("/SubSync/Sync", body)


def run_single(item_id, index, mode=None, timeout=1800, label="single"):
    """Queue one subtitle and wait for it. Returns a measurement dict."""
    off = ss.bookmark()
    t0 = time.time()
    st, resp, qms = queue(item_id, index, mode)
    if st != 200:
        return {"label": label, "queued": False, "status": st, "body": resp,
                "wall_ms": (time.time() - t0) * 1000.0}
    job_id = None
    if isinstance(resp, dict):
        job_id = resp.get("JobId") or resp.get("jobId") or resp.get("Id") or resp.get("id")
    if not job_id:
        return {"label": label, "queued": False, "status": st, "body": resp, "note": "no job id"}
    job = ss.wait_job(job_id, timeout=timeout)
    wall = (time.time() - t0) * 1000.0
    log = ss.log_since(off)
    out = {"label": label, "job_id": job_id, "status_job": job.get("Status"),
           "phase": job.get("Phase"), "outcome": job.get("Outcome"), "error": job.get("Error"),
           "output": job.get("OutputPath"), "queue_api_ms": qms, "wall_ms": wall,
           "log": [l for l in log.splitlines() if job_id[:8] in l or "extract" in l or "job " in l]}
    ss.record(label, out)
    return out


def run_batch(tasks, label="batch", timeout=3600, worker_note=None):
    """tasks: list of {"ItemId":..,"SubtitleIndex":..}. Returns measurement dict."""
    off = ss.bookmark()
    t0 = time.time()
    st, resp, post_ms = ss.post("/SubSync/Batch", {"Label": label, "Tasks": tasks})
    if st != 200:
        return {"label": label, "created": False, "status": st, "body": resp}
    bid = resp.get("BatchId") or resp.get("batchId") or resp.get("Id")
    b = ss.wait_batch(bid, timeout=timeout)
    wall = (time.time() - t0) * 1000.0
    tasks_out = b.get("Tasks") or []
    by_status = {}
    for t in tasks_out:
        by_status[t.get("Status")] = by_status.get(t.get("Status"), 0) + 1
    log = ss.log_since(off)
    out = {"label": label, "batch_id": bid, "created": True, "mode": b.get("Mode"),
           "wall_ms": wall, "post_ms": post_ms, "counts": by_status, "total": b.get("Total"),
           "ok": b.get("Ok"), "failed": b.get("Failed"), "cancelled": b.get("Cancelled"),
           "worker_note": worker_note,
           "fails": [{"i": t.get("SubtitleIndex"), "err": t.get("Error")}
                     for t in tasks_out if t.get("Status") == "Failed"],
           "log": log.splitlines()}
    ss.record(label, {k: v for k, v in out.items() if k != "log"})
    with open(ss.WORK / ("log-%s.txt" % label), "w") as f:
        f.write(log)
    return out


def orphan_check():
    """Anything left in the scratch root that is not a job the server still tracks?"""
    st, jobs, _ = ss.get("/SubSync/Jobs")
    live = {j.get("Id") for j in jobs} if isinstance(jobs, list) else set()
    leftovers = []
    if ss.CACHE.exists():
        for name in sorted(os.listdir(ss.CACHE)):
            p = ss.CACHE / name
            if name == "ref":
                n = sum(len(f) for _, _, f in os.walk(p))
                leftovers.append("ref/ (%d files)" % n)
            elif p.is_dir() and name not in live:
                n = sum(len(f) for _, _, f in os.walk(p))
                leftovers.append("%s/ (%d files)" % (name, n))
    return leftovers


def synced_files(folder):
    out = []
    for root, dirs, files in os.walk(folder):
        for f in files:
            if ".SYNCED" in f or f.endswith(".bak.subsync"):
                out.append(os.path.join(root, f))
    return sorted(out)


def cue_count(path):
    """Count SRT cues by the blank-line-separated blocks with a timecode."""
    n = 0
    try:
        for line in open(path, encoding="utf-8", errors="replace"):
            if re.match(r"^\d\d:\d\d:\d\d[,.]\d\d\d\s*-->", line.strip()):
                n += 1
    except FileNotFoundError:
        return None
    return n
