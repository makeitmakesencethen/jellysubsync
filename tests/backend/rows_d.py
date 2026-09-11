#!/usr/bin/env python3
"""Matrix section D — settings and filters, each alone and in combination."""
import json
import time

import ss, drive

OUT = {}
start = time.time()


def row(name, fn):
    t0 = time.time()
    try:
        r = fn()
    except Exception as e:
        r = {"exception": "%s: %s" % (type(e).__name__, e)}
    r["row_ms"] = (time.time() - t0) * 1000.0
    OUT[name] = r
    print("### %s -> %s" % (name, json.dumps({k: v for k, v in r.items() if k != "log"})[:800]), flush=True)
    ss.record("row-" + name, {k: v for k, v in r.items() if k != "log"})


def base_cfg():
    c = ss.plugin_config()
    return c


def run(name, item, index, expect_note=None):
    def f():
        cfg = base_cfg()
        st, body = ss.set_plugin_config(cfg)
        ss.clear_cache()
        r = drive.run_single(item, index, label=name)
        r["stored_config"] = ss.plugin_config()
        return r
    return f


def tracks_of(item):
    return [t["Index"] for t in drive.tracks(item)]


def main():
    EP1 = drive.ITEM["ep1"]
    EP2 = drive.ITEM["ep2"]

    # ---- 29. SyncLanguages -------------------------------------------------
    def lang(filter_list, item, index):
        cfg = base_cfg()
        cfg["SyncLanguages"] = filter_list
        st, _ = ss.set_plugin_config(cfg)
        stored = ss.plugin_config()["SyncLanguages"]
        st2, r, _ = ss.post("/SubSync/Sync", {"itemId": item, "subtitleIndex": index})
        out = {"filter": filter_list, "stored": stored, "status": st2, "body": r}
        cfg = base_cfg()
        cfg["SyncLanguages"] = []
        ss.set_plugin_config(cfg)
        return out

    row("D29-lang-match", lambda: lang(["eng"], EP1, 4))
    row("D29-lang-nomatch", lambda: lang(["swe"], EP1, 7))       # index 7 is Arabic
    row("D29-lang-mixedcase", lambda: lang(["ENG"], EP1, 4))
    row("D29-lang-unknown", lambda: lang(["qq", "!!!@#"], EP1, 4))
    row("D29-lang-empty", lambda: lang([], EP1, 4))

    # ---- 32. MaxOffsetSeconds / MaxSubtitleSeconds -------------------------
    def numeric(field, value, item, index):
        cfg = base_cfg()
        cfg[field] = value
        ss.set_plugin_config(cfg)
        stored = ss.plugin_config()[field]
        off = ss.bookmark()
        r = drive.run_single(item, index, label="D32-%s-%s" % (field, value))
        log = ss.log_since(off)
        argv = [l for l in log.splitlines() if "ffsubsync start" in l]
        cfg = base_cfg()
        cfg[field] = {"MaxOffsetSeconds": 60, "MaxSubtitleSeconds": 10}[field]
        ss.set_plugin_config(cfg)
        return {"field": field, "typed": value, "stored": stored,
                "job": r.get("status_job"), "outcome": r.get("outcome"),
                "error": r.get("error"), "argv": argv[0][:400] if argv else None}

    row("D32-offset-negative", lambda: numeric("MaxOffsetSeconds", -5, EP2, 4))
    row("D32-offset-absurd", lambda: numeric("MaxOffsetSeconds", 100000, EP2, 4))
    row("D32-offset-zero", lambda: numeric("MaxOffsetSeconds", 0, EP2, 4))
    row("D32-subsec-absurd", lambda: numeric("MaxSubtitleSeconds", 100000, EP2, 4))

    # ---- 30. ParallelWorkers ----------------------------------------------
    def workers(v):
        cfg = base_cfg()
        cfg["ParallelWorkers"] = v
        ss.set_plugin_config(cfg)
        return {"typed": v, "stored": ss.plugin_config()["ParallelWorkers"]}

    for v in (0, 1, 2, 4, 8, 64, 65, 999):
        row("D30-workers-%s" % v, (lambda vv: (lambda: workers(vv)))(v))

    # ---- 31. MultiSyncMode -------------------------------------------------
    def mode(m, n=6):
        cfg = base_cfg()
        cfg["MultiSyncMode"] = m
        ss.set_plugin_config(cfg)
        ss.clear_cache()
        idx = tracks_of(EP2)[:n]
        st, resp, _ = ss.post("/SubSync/Batch", {"Label": "mode-" + m,
                                                 "Tasks": [{"ItemId": EP2, "SubtitleIndex": i} for i in idx]})
        if st != 200:
            return {"mode": m, "status": st, "body": resp}
        bid = resp.get("BatchId") or resp.get("batchId") or resp.get("Id")
        t0 = time.time()
        b = ss.wait_batch(bid, timeout=1200)
        out = {"mode_setting": m, "stored": ss.plugin_config()["MultiSyncMode"], "resolved": b.get("Mode"),
               "wall_s": time.time() - t0, "tasks": len(idx), "ok": b.get("Ok"), "failed": b.get("Failed")}
        cfg = base_cfg()
        cfg["MultiSyncMode"] = "auto"
        ss.set_plugin_config(cfg)
        return out

    for m in ("auto", "normal", "ultimate"):
        row("D31-mode-%s" % m, (lambda mm: (lambda: mode(mm)))(m))

    # ---- 33. VAD / golden section / framerate ------------------------------
    def opt(field, value, item, index):
        cfg = base_cfg()
        cfg[field] = value
        ss.set_plugin_config(cfg)
        ss.clear_cache()
        off = ss.bookmark()
        r = drive.run_single(item, index, label="D33-%s-%s" % (field, value))
        log = ss.log_since(off)
        argv = [l for l in log.splitlines() if "ffsubsync start" in l]
        cfg = base_cfg()
        cfg[field] = {"VadMethod": "subs_then_webrtc", "UseGoldenSectionSearch": True,
                      "FixFramerate": False}[field]
        ss.set_plugin_config(cfg)
        return {"field": field, "typed": value, "stored_value": True,
                "job": r.get("status_job"), "outcome": r.get("outcome"),
                "argv": argv[0][argv[0].find("args="):][:300] if argv else None}

    row("D33-vad-webrtc", lambda: opt("VadMethod", "webrtc", EP2, 4))
    row("D33-vad-silero", lambda: opt("VadMethod", "silero", EP2, 4))
    row("D33-vad-bogus", lambda: opt("VadMethod", "not-a-method", EP2, 4))
    row("D33-gss-on", lambda: opt("UseGoldenSectionSearch", True, EP2, 4))
    row("D33-fixframerate-on", lambda: opt("FixFramerate", True, EP2, 4))
    row("D33-gss-with-framerate", lambda: opt("UseGoldenSectionSearch", True, EP2, 4))

    # ---- 34. ExtractionTimeoutMinutes --------------------------------------
    def timeout(v):
        cfg = base_cfg()
        cfg["ExtractionTimeoutMinutes"] = v
        ss.set_plugin_config(cfg)
        stored = ss.plugin_config()["ExtractionTimeoutMinutes"]
        cfg = base_cfg()
        cfg["ExtractionTimeoutMinutes"] = 20
        ss.set_plugin_config(cfg)
        return {"typed": v, "stored": stored}

    for v in (0, 1, 240, 241, 100000):
        row("D34-timeout-%s" % v, (lambda vv: (lambda: timeout(vv)))(v))

    # ---- 35. FfSubSyncPath / FfmpegPath ------------------------------------
    def path(field, value, item, index):
        cfg = base_cfg()
        cfg[field] = value
        ss.set_plugin_config(cfg)
        stored = ss.plugin_config()[field]
        off = ss.bookmark()
        r = drive.run_single(item, index, label="D35-%s" % field)
        log = ss.log_since(off)
        cfg = base_cfg()
        cfg[field] = {"FfSubSyncPath": "ffsubsync", "FfmpegPath": ""}[field]
        ss.set_plugin_config(cfg)
        return {"field": field, "typed": value, "stored": stored, "status": r.get("status_job"),
                "error": r.get("error"), "api_status": r.get("status"),
                "log": [l[-160:] for l in log.splitlines() if "ffsubsync" in l or "ERROR" in l][:4]}

    row("D35-ffsubsync-missing", lambda: path("FfSubSyncPath", "/no/such/binary-xyz", EP2, 4))
    row("D35-ffsubsync-nonexec", lambda: path("FfSubSyncPath", "/etc/hostname", EP2, 4))
    row("D35-ffmpeg-missing", lambda: path("FfmpegPath", "/no/such/ffmpeg-xyz", EP2, 4))

    # ---- 36. SyncModeCopy --------------------------------------------------
    def copy_mode(replace: bool, item, index):
        import os
        cfg = base_cfg()
        cfg["SyncModeCopy"] = not replace
        ss.set_plugin_config(cfg)
        ss.clear_cache()
        folder = os.path.dirname([t["Path"] for t in ss.items(types=["Movie"])
                                  if t["Id"] == item][0]) if False else None
        before = {}
        import glob
        src = None
        for t in ss.items(types=["Movie", "Episode"]):
            if t["Id"] == item:
                src = t["Path"]
        for f in glob.glob(os.path.splitext(src)[0] + "*"):
            if os.path.isfile(f):
                before[f] = (os.path.getsize(f), ss.sha256(f))
        r = drive.run_single(item, index, label="D36-copy" if not replace else "D36-replace")
        after = {}
        for f in glob.glob(os.path.splitext(src)[0] + "*"):
            if os.path.isfile(f):
                after[f] = (os.path.getsize(f), ss.sha256(f))
        cfg = base_cfg()
        cfg["SyncModeCopy"] = True
        ss.set_plugin_config(cfg)
        return {"replace": replace, "job": r.get("status_job"), "outcome": r.get("outcome"),
                "output": r.get("output"),
                "files_before": sorted(before), "files_after": sorted(after),
                "appeared": sorted(set(after) - set(before)),
                "changed": sorted(k for k in set(after) & set(before) if after[k] != before[k])}

    row("D36-copy-mode", lambda: copy_mode(False, "810991dd105ad0097a92522366411fe0", 0))

    print("\n=== D section done in %.1f s ===" % (time.time() - start))


if __name__ == "__main__":
    main()
