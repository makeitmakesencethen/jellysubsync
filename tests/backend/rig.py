#!/usr/bin/env python3
"""Drive the local test Jellyfin so a register row can be reproduced without fabji's server.

Why this exists: the register's rows are all about behaviour that only shows up against a real
server - a ceiling chosen, a walk refused, a batch queued slowly. Until this file, every such row
ended with "needs the user to run it", which means it never got run. This box has everything an
equivalent run needs:

  * a real Jellyfin at /opt/data/jf12test (started by tests/backend/start-server.sh),
  * slowread.so, an LD_PRELOAD shim that puts a *real* per-read latency on one directory, so a
    local NVMe can stand in for the share, calibrated against fabji's own log:
        SLOWREAD_MS_PER_CALL=10  SLOWREAD_MS_PER_16K=1.46     -> ~11 MB/s, ~10 ms per round trip
    Raise the per-call cost and the same volume reads as a thrashing share (S41 was one cold read
    of 231 ms); drop it to zero and it is the local disk (0,83 ms in that same field run).
  * media in media/ (fast), media-slow/ (shimmed) and media-fixtures/ (embedded, bitmap, ASS).

Everything here is deliberately plain stdlib: sqlite3 for the caller's token out of the rig's own
Devices table (ItemAccess fails closed without an identity, so an API key would prove nothing),
urllib for the API, and the plugin's own log file as the source of truth for what the engine did.

Usage from a scenario:

    from rig import Rig
    rig = Rig(shim={'MS_PER_CALL': 231})
    rig.start(); rig.wait_ready()
    batch = rig.queue_batch(rig.items(path_contains='media-slow')[:4])
    rig.wait_batch(batch)
    rig.report('S41', [('the shimmed volume is held to one walk', 'ceiling 1' in rig.log_text(), ...)])
"""
from __future__ import annotations

import json
import os
import re
import shutil
import signal
import sqlite3
import subprocess
import sys
import time
import urllib.error
import uuid
import urllib.request
from pathlib import Path

JELLYFIN = Path('/opt/data/jf12test')
DOTNET = Path('/opt/data/.dotnet/dotnet')
SYSTEM_SQL = Path(__file__).resolve().parent          # tests/backend
DB = JELLYFIN / 'data' / 'data' / 'jellyfin.db'
PLUGIN_LOG = JELLYFIN / 'data' / 'data' / 'subsync' / 'logs' / 'subsync.log'
PLUGINS = JELLYFIN / 'data' / 'plugins'
RESULTS = SYSTEM_SQL / 'rig-results.json'
ENV_FILE = SYSTEM_SQL / 'slowread-fabji.env'
REPO = SYSTEM_SQL.parent.parent
BASE_URL = 'http://127.0.0.1:8096'


class Rig:
    """One run of the test server, with an optional storage shim in front of media-slow."""

    def __init__(self, shim: dict | None = None, use_env_file: bool = False, log=print):
        self.shim = dict(shim or {})
        if use_env_file and ENV_FILE.exists():
            for line in ENV_FILE.read_text().splitlines():
                m = re.match(r'export\s+(\w+)=(.*)', line.strip())
                if m:
                    self.shim[m.group(1)] = m.group(2)
        self.log = log
        self.proc: subprocess.Popen | None = None
        self.token: str | None = None
        self._log_offset = 0

    # ---------------------------------------------------------------- server

    def ensure_active_token(self) -> str:
        """Makes sure the rig has an active admin token, and returns it.

        The rig poses as the server's own admin with a session token out of its `Devices` table, not
        with an API key: since 2.0.34 the plugin's item endpoints resolve who is calling and fail
        closed without an identity (`F3`), so a key would exercise a door no user walks through.

        The container's restarts prune active sessions, so the token is created here if the table has
        none - a write to the rig's own database, which is ours to make, and one taken before the
        server starts so no in-memory session cache can be stale about it.
        """
        con = sqlite3.connect(DB)
        try:
            user = con.execute('select Id from Users where Username = ?', ('admin',)).fetchone()
            if not user:
                raise RuntimeError('the rig has no admin user')
            row = con.execute(
                'select AccessToken from Devices where UserId = ? and IsActive = 1 limit 1',
                (user[0],)).fetchone()
            if row:
                self.token = str(row[0])
                return self.token

            existing = con.execute(
                'select AccessToken from Devices where UserId = ? order by Id limit 1', (user[0],)).fetchone()
            token = str(existing[0]) if existing else uuid.uuid4().hex
            now = time.strftime('%Y-%m-%d %H:%M:%S')
            if existing:
                con.execute('update Devices set IsActive = 1, DateLastActivity = ? where AccessToken = ?',
                            (now, token))
            else:
                con.execute(
                    'insert into Devices (UserId, AccessToken, AppName, AppVersion, DeviceName, DeviceId, '
                    'IsActive, DateCreated, DateModified, DateLastActivity) '
                    'values (?, ?, ?, ?, ?, ?, 1, ?, ?, ?)',
                    (user[0], token, 'subsync-rig', '1.0', 'rig', 'rig', now, now, now))
            con.commit()
        finally:
            con.close()
        self.token = token
        self.log(f'[rig] created an active admin token in the rig ({token[:6]}...)')
        return token

    def start(self) -> 'Rig':
        env = dict(os.environ)
        env.update(
            JELLYFIN_DATA_DIR=str(JELLYFIN / 'data'),
            JELLYFIN_CONFIG_DIR=str(JELLYFIN / 'config'),
            JELLYFIN_CACHE_DIR=str(JELLYFIN / 'cache'),
            JELLYFIN_LOG_DIR=str(JELLYFIN / 'log'),
            JELLYFIN_WEB_DIR=str(JELLYFIN / 'jellyfin' / 'jellyfin-web'),
            DOTNET_ROOT='/opt/data/.dotnet',
            LD_LIBRARY_PATH='/opt/data/local/icu/usr/lib/x86_64-linux-gnu',
        )
        if self.shim:
            so = SYSTEM_SQL / 'slowread.so'
            if not so.exists():
                subprocess.run(['gcc', '-shared', '-fPIC', '-O2', '-o', str(so),
                                str(SYSTEM_SQL / 'slowread.c'), '-ldl'], check=True)
            env['LD_PRELOAD'] = str(so)
            env['SLOWREAD_PREFIX'] = str(JELLYFIN / 'media-slow') + '/'
            for key, value in self.shim.items():
                if key.startswith('SLOWREAD_'):
                    env[key] = str(value)
                else:
                    env['SLOWREAD_' + key] = str(value)
            self.log(f"[rig] shim: {env.get('SLOWREAD_MS_PER_CALL', 0)} ms/call, "
                     f"{env.get('SLOWREAD_MS_PER_16K', 0)} ms/16K on {env['SLOWREAD_PREFIX']}")
        else:
            self.log('[rig] no shim: every volume reads at local speed')
        self.ensure_active_token()
        out = (JELLYFIN / 'log' / 'rig-stdout.log').open('ab')
        self.proc = subprocess.Popen([str(DOTNET), 'jellyfin.dll'], cwd=str(JELLYFIN / 'jellyfin'),
                                     env=env, stdout=out, stderr=subprocess.STDOUT,
                                     preexec_fn=os.setsid)
        self.log(f"[rig] started pid {self.proc.pid}")
        self.refresh_token()
        return self

    def stop(self) -> None:
        if self.proc and self.proc.poll() is None:
            os.killpg(os.getpgid(self.proc.pid), signal.SIGTERM)
            try:
                self.proc.wait(timeout=30)
            except subprocess.TimeoutExpired:
                os.killpg(os.getpgid(self.proc.pid), signal.SIGKILL)
        self.proc = None

    def __enter__(self):
        return self.start()

    def __exit__(self, *exc):
        self.stop()

    def wait_ready(self, timeout=180) -> None:
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                self.get('/System/Info/Public', auth=False)
                self.refresh_token()
                version = self.get('/System/Info/Public', auth=False).get('Version')
                self.log(f'[rig] ready (Jellyfin {version})')
                return
            except Exception:
                time.sleep(3)
        raise TimeoutError('the rig did not come up')

    # ------------------------------------------------------------------- api

    def refresh_token(self) -> str:
        """The admin's access token out of the rig's own Devices table.

        A token is used rather than an API key on purpose: the plugin's item check resolves the
        caller's identity and fails closed without one, so an API key would test a door no user
        walks through.
        """
        con = sqlite3.connect(f'file:{DB}?mode=ro', uri=True)
        try:
            row = con.execute(
                'select AccessToken from Devices where UserId = '
                '(select Id from Users where Username = ?) and IsActive = 1 limit 1',
                ('admin',)).fetchone()
        finally:
            con.close()
        if not row:
            return self.ensure_active_token()
        self.token = str(row[0])
        return self.token

    def get(self, path, auth=True):
        return self._request('GET', path, None, auth)

    def post(self, path, body=None, auth=True):
        return self._request('POST', path, body, auth)

    def _request(self, method, path, body, auth):
        url = (path if path.startswith('http') else BASE_URL + path)
        data = json.dumps(body).encode() if body is not None else None
        req = urllib.request.Request(url, data=data, method=method)
        req.add_header('Content-Type', 'application/json')
        if auth:
            req.add_header('Authorization', f'MediaBrowser Token="{self.token}"')
        with urllib.request.urlopen(req, timeout=60) as response:
            raw = response.read()
        return json.loads(raw) if raw else {}

    # -------------------------------------------------------------- libraries

    def items(self, path_contains=None, kinds='Movie,Episode', limit=2000):
        view = self.get('/Items?Recursive=true&IncludeItemTypes=' + kinds +
                        '&Fields=Path,MediaSources,MediaStreams&Limit=' + str(limit))
        out = [i for i in view.get('Items', [])]
        if path_contains:
            out = [i for i in out if path_contains in (i.get('Path') or '')]
        return out

    def refresh_library(self):
        self.post('/Library/Refresh')

    # ----------------------------------------------------------------- batches

    def queue_batch(self, items, mode=None, label='rig'):
        tasks = []
        for item in items:
            streams = item.get('MediaStreams') or []
            sidx = next((int(s.get('Index', 0)) for s in streams
                         if s.get('Type') == 'Subtitle' and not s.get('IsExternal')), 0)
            tasks.append({'ItemId': item['Id'], 'SubtitleIndex': sidx,
                          'Title': item.get('Name') or item['Id']})
        if not tasks:
            raise RuntimeError('no items to queue')
        body = {'Tasks': tasks, 'Label': label}
        if mode:
            body['Mode'] = mode
        view = self.post('/SubSync/Batch', body)
        batch_id = view.get('BatchId') or view.get('Id')
        self.log(f'[rig] batch {batch_id}: {len(tasks)} task(s)')
        return batch_id

    def batch(self, batch_id):
        return self.get(f'/SubSync/Batch/{batch_id}')

    def wait_batch(self, batch_id, timeout=3600, quiet=0):
        deadline = time.time() + timeout
        while time.time() < deadline:
            view = self.batch(batch_id)
            tasks = view.get('Tasks') or []
            live = [t for t in tasks if (t.get('Status') or '').lower() in ('queued', 'running')]
            if tasks and not live:
                self.log(f"[rig] batch {batch_id} finished: " + ', '.join(
                    sorted({(t.get('Status') or '?') for t in tasks})))
                return view
            time.sleep(10)
        raise TimeoutError(f'batch {batch_id} did not finish in {timeout}s')

    # -------------------------------------------------------------------- log

    def log_lines(self, since=0):
        if not PLUGIN_LOG.exists():
            return []
        with PLUGIN_LOG.open('rb') as handle:
            handle.seek(since)
            data = handle.read()
        self._log_offset = since + len(data)
        return data.decode('utf-8', 'replace').splitlines()

    def log_text(self, since=0):
        return '\n'.join(self.log_lines(since))

    def wait_for_log(self, pattern, since=0, timeout=300, poll=2):
        """Waits until the plugin log gains a line matching `pattern`.

        Returns every line the log gained since `since`, not just the match: a scenario decides on
        the context around its evidence - the probe line, the ceiling line it produced, and what the
        scheduler said between them.
        """
        deadline = time.time() + timeout
        collected = []
        while True:
            collected = self.log_lines(since)
            if any(re.search(pattern, line) for line in collected):
                return collected
            if time.time() >= deadline:
                raise TimeoutError(
                    f'the log never matched {pattern!r} within {timeout}s '
                    f'({len(collected)} line(s) since offset {since})')
            time.sleep(poll)

    # ---------------------------------------------------------------- results

    def report(self, scenario, assertions, notes=''):
        """Append one scenario's outcome to tests/backend/rig-results.json."""
        record = {
            'scenario': scenario, 'when': time.strftime('%Y-%m-%d %H:%M:%S'),
            'shim': self.shim, 'notes': notes,
            'assertions': [{'check': c, 'passed': bool(p), 'detail': d} for c, p, d in assertions],
        }
        record['passed'] = all(a['passed'] for a in record['assertions'])
        history = json.loads(RESULTS.read_text()) if RESULTS.exists() else []
        history.append(record)
        RESULTS.write_text(json.dumps(history, indent=2) + '\n')
        for item in record['assertions']:
            self.log(('  PASS  ' if item['passed'] else '  FAIL  ') + item['check'] +
                     (f" - {item['detail']}" if item['detail'] else ''))
        self.log(f"[rig] {scenario}: {'PASSED' if record['passed'] else 'FAILED'} "
                 f"({RESULTS})")
        return record


def install_plugin(version: str | None = None, source: Path | None = None) -> Path:
    """Put the freshly built plugin into the rig, the way a release zip would."""
    source = source or (REPO / 'Jellyfin.Plugin.SubSync' / 'bin' / 'Release' / 'net10.0')
    meta = json.loads((REPO / 'Jellyfin.Plugin.SubSync' / 'meta.json').read_text())
    version = version or meta['version']
    target = PLUGINS / f'SubSync_{version}'
    target.mkdir(parents=True, exist_ok=True)
    for name in ('Jellyfin.Plugin.SubSync.dll', 'Jellyfin.Plugin.SubSync.deps.json',
                 'Jellyfin.Plugin.SubSync.pdb', 'Jellyfin.Plugin.SubSync.xml'):
        if (source / name).exists():
            shutil.copy2(source / name, target / name)
    shutil.copy2(REPO / 'Jellyfin.Plugin.SubSync' / 'meta.json', target / 'meta.json')
    return target


if __name__ == '__main__':
    # A smoke test of the harness itself: start, list, stop.
    rig = Rig(shim={'MS_PER_CALL': 10, 'MS_PER_16K': 1.46})
    try:
        rig.start()
        rig.wait_ready()
        items = rig.items()
        print(f'{len(items)} items visible')
        for item in items[:10]:
            print('  ', item.get('Path'))
        print('media-slow items:', len(rig.items('media-slow')))
        print('plugin log:', PLUGIN_LOG, PLUGIN_LOG.exists())
    finally:
        rig.stop()
