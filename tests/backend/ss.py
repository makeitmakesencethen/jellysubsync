"""Shared harness for the SubSync backend test (phase 1 + 2).

Everything talks to the live test server at 127.0.0.1:8096 and reads the plugin's own log.
No mocks: every number in a report comes from here or from a command run in the shell.
"""
import json
import os
import pathlib
import re
import subprocess
import time
import urllib.error
import urllib.request

BASE = "http://127.0.0.1:8096"
TOKEN = pathlib.Path("/opt/data/tmp/jf_token.txt").read_text().strip()
LOG = pathlib.Path("/opt/data/jf12test/data/data/subsync/logs/subsync.log")
CACHE = pathlib.Path("/opt/data/jf12test/cache/subsync")
STATE = pathlib.Path("/opt/data/jf12test/data/data/subsync")
MEDIA = pathlib.Path("/opt/data/jf12test/media")
MEDIA_TV = pathlib.Path("/opt/data/jf12test/media-tv")
WORK = pathlib.Path("/opt/data/tmp/backendtest")
WORK.mkdir(parents=True, exist_ok=True)


def api(method, path, body=None, raw=False, timeout=300, token=None, extra_headers=None):
    """Call the Jellyfin API. Returns (status, parsed-or-text)."""
    url = BASE + path
    data = None
    headers = {
        "Authorization": 'MediaBrowser Token="%s"' % (token or TOKEN),
        "Accept": "application/json",
    }
    if body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"
    if extra_headers:
        headers.update(extra_headers)
    req = urllib.request.Request(url, data=data, headers=headers, method=method)
    t0 = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            payload = r.read()
            status = r.status
    except urllib.error.HTTPError as e:
        payload = e.read()
        status = e.code
    el = (time.time() - t0) * 1000.0
    if raw:
        return status, payload.decode("utf-8", "replace"), el
    try:
        return status, json.loads(payload.decode("utf-8")), el
    except Exception:
        return status, payload.decode("utf-8", "replace"), el


def get(path, **kw):
    return api("GET", path, **kw)


def post(path, body=None, **kw):
    return api("POST", path, body, **kw)


# ---------------------------------------------------------------- items / library

def items(parent_id=None, types=None, recursive=True, limit=None):
    q = ["/Items?Recursive=%s" % str(recursive).lower(), "Fields=Path,MediaSources,MediaStreams"]
    if parent_id:
        q.append("ParentId=" + parent_id)
    if types:
        q.append("IncludeItemTypes=" + ",".join(types))
    if limit:
        q.append("Limit=%d" % limit)
    st, body, _ = get("&".join(q))
    if st != 200:
        raise RuntimeError("items failed %s %s" % (st, body))
    return body["Items"]


def views():
    return items(types=["CollectionFolder", "Folder"], recursive=False)


def find_item(name_contains, types=("Movie", "Episode")):
    for it in items(types=list(types)):
        if name_contains.lower() in (it.get("Name") or "").lower() or \
           name_contains.lower() in (it.get("Path") or "").lower():
            return it
    return None


def item_by_path(path_fragment, types=("Movie", "Episode")):
    return find_item(path_fragment, types)


def library_id(name_contains):
    for v in views():
        if name_contains.lower() in (v.get("Name") or "").lower():
            return v["Id"], v["Name"]
    return None, None


# ---------------------------------------------------------------- log helpers

def log_size():
    return LOG.stat().st_size


def log_since(offset):
    """Return the plugin-log text written after `offset`."""
    with open(LOG, "rb") as f:
        f.seek(offset)
        return f.read().decode("utf-8", "replace")


def log_tail_lines(n=40):
    return LOG.read_text(errors="replace").splitlines()[-n:]


def wait_for(pattern, offset=0, timeout=600, poll=0.5):
    """Wait for a regex in the log after `offset`. Returns the matching line or None."""
    rx = re.compile(pattern)
    deadline = time.time() + timeout
    while time.time() < deadline:
        for line in log_since(offset).splitlines():
            m = rx.search(line)
            if m:
                return line
        time.sleep(poll)
    return None


def wait_job(job_id, timeout=1800, poll=0.5):
    """Block until a job reaches a terminal state. Returns the job dict."""
    deadline = time.time() + timeout
    while time.time() < deadline:
        st, job, _ = get("/SubSync/Jobs/%s" % job_id)
        if st == 200 and isinstance(job, dict) and job.get("Status") in ("Completed", "Failed", "Cancelled"):
            return job
        time.sleep(poll)
    st, job, _ = get("/SubSync/Jobs/%s" % job_id)
    return job


def wait_batch(batch_id, timeout=3600, poll=1.0):
    deadline = time.time() + timeout
    while time.time() < deadline:
        st, b, _ = get("/SubSync/Batch/%s" % batch_id)
        if st == 200 and isinstance(b, dict):
            tasks = b.get("Tasks") or []
            live = [t for t in tasks if t.get("Status") in ("Queued", "Running")]
            if not live:
                return b
        time.sleep(poll)
    st, b, _ = get("/SubSync/Batch/%s" % batch_id)
    return b


# ---------------------------------------------------------------- cache / config

def config():
    st, c, _ = get("/Plugins/%s/Configuration" % PLUGIN_ID)
    return c


PLUGIN_ID = "c7d8e9f0-a1b2-4c3d-e5f6-a7b8c9d0e1f2"


def plugin_config():
    st, c, _ = get("/Plugins/%s/Configuration" % PLUGIN_ID)
    if st != 200:
        raise RuntimeError("config failed %s %s" % (st, c))
    return c


def set_plugin_config(cfg):
    st, body, _ = post("/Plugins/%s/Configuration" % PLUGIN_ID, cfg)
    return st, body


def cache_stats():
    """Counts on disk under the plugin cache root."""
    out = {}
    for root, dirs, files in os.walk(CACHE):
        rel = os.path.relpath(root, CACHE)
        out[rel] = len(files)
    return out


def clear_cache():
    return post("/SubSync/SpeechCache/Clear")[1]


# ---------------------------------------------------------------- misc

def sha256(p):
    import hashlib
    h = hashlib.sha256()
    with open(p, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


def record(name, data):
    """Append a measurement row to the workspace evidence file."""
    p = WORK / "evidence.jsonl"
    with open(p, "a") as f:
        f.write(json.dumps({"t": time.strftime("%Y-%m-%dT%H:%M:%S"), "name": name, "data": data}) + "\n")


def bookmark():
    return log_size()
