#!/usr/bin/env python3
"""Read-only analysis of the SubSync plugin log for the 2026-09-18 run window.

    python3 tests/backend/analyse_log_2026_09_18.py \
        --log /subsync-logs/subsync.log \
        --json tests/backend/log-analysis-2026-09-18.json \
        --dumpdir /tmp/logwork

Everything is counted programmatically from the raw bytes. Note two log quirks:
decimal separators appear BOTH as '.' and ',' (Swedish culture on some messages),
and a negative number is written with U+2212 MINUS SIGN, not ASCII '-'.
Nothing is written outside --json and --dumpdir.
"""
import argparse
import json
import os
import re
import statistics
import collections

RE_TS = re.compile(r'^(?P<ts>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}\.\d{3})Z '
                   r'(?P<lvl>INFO|WARN|ERROR)\s+(?P<msg>.*)$')
RE_JOB = re.compile(r'\b([0-9a-f]{32})\b')
NUM = r'[-−]?\d+(?:[.,]\d+)?'


def num(s):
    """Parse a log number: U+2212 minus and comma decimal separator both allowed."""
    if s is None:
        return None
    t = s.strip().replace('\u2212', '-').replace(',', '.')
    try:
        return float(t)
    except ValueError:
        return None


class Log:
    def __init__(self, path):
        with open(path, 'rb') as fh:
            raw = fh.read()
        self.path = path
        self.raw_bytes = len(raw)
        self.lines = raw.decode('utf-8').split('\n')
        if self.lines and self.lines[-1] == '':
            self.lines.pop()
        self.records = []            # (lineno, ts|None, level|None, msg)
        for i, l in enumerate(self.lines, 1):
            m = RE_TS.match(l)
            self.records.append((i, m.group('ts'), m.group('lvl'), m.group('msg')) if m
                                else (i, None, None, l))

    def grep(self, pattern, flags=0):
        rx = re.compile(pattern, flags)
        return [r for r in self.records if rx.search(r[3])]


def tukey(values):
    """(q1, median, q3, fences, outliers) by the 1.5*IQR fence rule."""
    vs = sorted(v for v in values if v is not None)
    if len(vs) < 4:
        return None
    q1 = statistics.quantiles(vs, n=4, method='inclusive')[0]
    med = statistics.median(vs)
    q3 = statistics.quantiles(vs, n=4, method='inclusive')[2]
    iqr = q3 - q1
    lo, hi = q1 - 1.5 * iqr, q3 + 1.5 * iqr
    return {'n': len(vs), 'min': min(vs), 'q1': q1, 'median': med, 'q3': q3,
            'max': max(vs), 'iqr': iqr, 'lower_fence': lo, 'upper_fence': hi,
            'outliers': [v for v in vs if v < lo or v > hi]}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--log', default='/subsync-logs/subsync.log')
    ap.add_argument('--json', default='tests/backend/log-analysis-2026-09-18.json')
    ap.add_argument('--dumpdir', default='/tmp/logwork')
    a = ap.parse_args()

    L = Log(a.log)
    recs = L.records
    out = collections.OrderedDict()
    dumps = {}

    # ------------------------------------------------------------- 1. what was read
    ts_lines = [r for r in recs if r[1]]
    no_ts = [r for r in recs if not r[1]]
    out['log'] = {
        'path': L.path,
        'raw_bytes': L.raw_bytes,
        'records': len(recs),
        'lines_with_timestamp': len(ts_lines),
        'lines_without_timestamp': len(no_ts),
        'lines_without_timestamp_linenos': [r[0] for r in no_ts],
        'first_timestamp': ts_lines[0][1],
        'last_timestamp': ts_lines[-1][1],
        'first_line': L.lines[0],
        'last_line': L.lines[-1],
        'lines_by_day': dict(collections.Counter(r[1][:10] for r in ts_lines)),
        'timestamps_monotonic': [r[1] for r in ts_lines] == sorted(r[1] for r in ts_lines),
        'lines_after_2026_09_19T00_19_59Z': len([r for r in ts_lines if r[1] > '2026-09-19 00:19:59.999']),
    }

    # ------------------------------------------------------------- 2. per-job model
    jobs = collections.OrderedDict()

    def job(jid):
        return jobs.setdefault(jid, {'id': jid})

    re_queued = re.compile(
        r'queued: job=(?P<job>[0-9a-f]{32}) item=(?P<item>\S+) stream=(?P<stream>[−\-\d]+) '
        r'ordinal=(?P<ordinal>[−\-\d]+) mode=(?P<mode>\S+) batch=(?P<batch>\S+) '
        r'language=(?P<lang>\S+) external=(?P<ext>\S+) forced=(?P<forced>\S+) '
        r'codec=(?P<codec>\S+) video=(?P<video>.*)$')
    re_jobref = re.compile(r'\[(?P<job>[0-9a-f]{32})\] reference: method=(?P<method>\S+)(?P<rest>.*)$')
    re_start = re.compile(r'\[(?P<job>[0-9a-f]{32})\] ffsubsync start: ')
    re_exit = re.compile(r'\[(?P<job>[0-9a-f]{32})\] ffsubsync exit=(?P<exit>[−\-\d]+) after (?P<ms>\d+) ms$')
    re_align = re.compile(r'\[(?P<job>[0-9a-f]{32})\] ffsubsync alignment: score=(?P<score>\S+) '
                          r'offset=(?P<off>' + NUM + r') s (?P<against>.*)$')
    re_fr = re.compile(r'\[(?P<job>[0-9a-f]{32})\] framerate: (?P<rest>.*)$')
    re_walk = re.compile(r'\[(?P<job>[0-9a-f]{32})\] this walk moved (?P<mb>' + NUM + r') MB of (?P<file>.*?) '
                         r'in (?P<s>' + NUM + r') s = (?P<mbs>' + NUM + r') MB/s(?P<rest>.*)$')
    # output= must be non-greedy .*? : output paths contain spaces.
    re_done = re.compile(r'job (?P<job>[0-9a-f]{32}) completed: mode=(?P<mode>\S+) output=(?P<output>.*?) '
                         r'bytes=(?P<bytes>\S+) change=(?P<change>.*?) extraction=(?P<extraction>.*)$')
    re_reffile = re.compile(r'\[(?P<job>[0-9a-f]{32})\] reference: method=\S+.*? file=(?P<file>.*)$')
    re_verdict = re.compile(r'job (?P<job>[0-9a-f]{32}) (?P<verdict>UNVERIFIED|REFUSED|failed):?(?P<rest>.*)$')

    verify_lines = []
    for ln, ts, lvl, msg in recs:
        m = re_queued.search(msg)
        if m:
            j = job(m.group('job'))
            j.update(queued_lineno=ln, queued_ts=ts, item=m.group('item'),
                     stream=int(m.group('stream').replace('\u2212', '-')),
                     ordinal=int(m.group('ordinal').replace('\u2212', '-')),
                     mode=m.group('mode'), batch=m.group('batch'), language=m.group('lang'),
                     external=m.group('ext'), codec=m.group('codec'), file=m.group('video'))
            continue
        m = re_jobref.search(msg)
        if m:
            j = job(m.group('job'))
            j.update(reference_line=ln, reference_ts=ts, reference_method=m.group('method'),
                     reference_raw=msg)
            mf = re_reffile.search(msg)
            if mf:
                j['file_from_reference'] = mf.group('file')
            continue
        m = re_start.search(msg)
        if m:
            job(m.group('job')).update(start_line=ln, start_ts=ts)
            continue
        m = re_exit.search(msg)
        if m:
            job(m.group('job')).update(exit=int(m.group('exit').replace('\u2212', '-')),
                                       exit_ms=int(m.group('ms')), exit_line=ln)
            continue
        m = re_align.search(msg)
        if m:
            j = job(m.group('job'))
            j.update(align_line=ln, align_ts=ts, score_raw=m.group('score'),
                     score=num(m.group('score')), offset_s=num(m.group('off')),
                     align_against=m.group('against'))
            continue
        m = re_fr.search(msg)
        if m:
            j = job(m.group('job'))
            j.setdefault('framerate_lines', []).append(ln)
            j.setdefault('framerate_msgs', []).append(msg)
            continue
        m = re_walk.search(msg)
        if m:
            j = job(m.group('job'))
            j.update(walk_mb=num(m.group('mb')), walk_s=num(m.group('s')),
                     walk_mbs=num(m.group('mbs')), walk_line=ln)
            j.setdefault('walk_msgs', []).append(msg)
            continue
        m = re_done.search(msg)
        if m:
            j = job(m.group('job'))
            j.update(completed_line=ln, completed_ts=ts, completed_mode=m.group('mode'),
                     output=m.group('output'), bytes_raw=m.group('bytes'),
                     change=m.group('change'), extraction=m.group('extraction'))
            continue
        m = re_verdict.search(msg)
        if m:
            j = job(m.group('job'))
            j.update(verdict=m.group('verdict'), verdict_line=ln, verdict_ts=ts,
                     verdict_reason=m.group('rest'))
            verify_lines.append((ln, ts, lvl, msg))
            continue

    # ------------------------------------------------ file fallback + change telemetry
    for j in jobs.values():
        if not j.get('file'):
            j['file'] = j.get('file_from_reference')
        if not j.get('file') and j.get('walk_msgs'):
            m = re.search(r' MB of (.*?) in ', j['walk_msgs'][0])
            j['file'] = m.group(1) if m else None
        if not j.get('file') and j.get('output') and j['output'] != '(none)':
            j['file'] = j['output']
        ch = j.get('change')
        if ch is not None:
            mo = re.search(r'([-+−])\s*(\d+) ms offset', ch)
            j['change_offset_ms'] = (-1 if mo.group(1) in '-−' else 1) * int(mo.group(2)) if mo else None
            ms = re.search(r'framerate ratio (' + NUM + r')', ch)
            j['change_ratio'] = num(ms.group(1)) if ms else None
            mm = re.search(r'stretched to (' + NUM + r')x', ch)
            j['change_stretch'] = num(mm.group(1)) if mm else None
        j['wrote_file'] = bool(j.get('output') and j['output'] != '(none)')

    # ------------------------------------------------------ reference / extract methods
    out['reference_methods'] = {
        'jobs_by_job_reference_line': dict(collections.Counter(
            j['reference_method'] for j in jobs.values() if j.get('reference_method'))),
        'file_level_reference_lines': dict(collections.Counter(
            m.group(1) for m in (re.match(r'^reference: method=(\S+)', r[3]) for r in recs) if m)),
        'file_level_extract_lines': dict(collections.Counter(
            m.group(1) for m in (re.match(r'^extract: method=(\S+)', r[3]) for r in recs) if m)),
        'jobs_with_queued_line': len([j for j in jobs.values() if 'queued_lineno' in j]),
        'jobs_total_seen': len(jobs),
        'jobs_with_engine_run': len([j for j in jobs.values() if 'start_line' in j]),
    }

    # ------------------------------------------------------------- 3. extraction= split
    ext, ext_class = collections.Counter(), collections.Counter()
    for j in jobs.values():
        if 'extraction' in j:
            e = j['extraction']
            ext[e] += 1
            if e.startswith('served from the extracted-subtitle cache'):
                ext_class['cache'] += 1
            elif e.startswith('read through'):
                ext_class['real read'] += 1
            else:
                ext_class['n/a'] += 1
    out['extraction'] = {'by_full_value': dict(ext), 'by_class': dict(ext_class)}

    # ------------------------------------------------------------- 4. sync outcomes
    done_jobs = [j for j in jobs.values() if 'completed_line' in j]
    out['sync_outcomes'] = {
        'queued_lines': len([r for r in recs if r[3].startswith('queued: job=')]),
        'completed': len(done_jobs),
        'completed_mode': dict(collections.Counter(j['completed_mode'] for j in done_jobs)),
        'failed': len([j for j in jobs.values() if j.get('verdict') == 'failed']),
        'unverified': len([j for j in jobs.values() if j.get('verdict') == 'UNVERIFIED']),
        'refused': len([j for j in jobs.values() if j.get('verdict') == 'REFUSED']),
        'cancelled_lines': len(L.grep(r'\bcancel', re.I)),
        'already_in_sync': len([j for j in done_jobs if 'already in sync' in j['change']]),
        'bytes_written_jobs': len([j for j in done_jobs if 'already in sync' not in j['change']]),
        'ffsubsync_start_lines': len(L.grep(r'\] ffsubsync start: ')),
        'ffsubsync_exit_lines': len(L.grep(r'\] ffsubsync exit=')),
        'done_paren_lines': len(L.grep(r'Done \(')),
    }
    out['sync_outcomes']['refusal_reasons'] = [
        {'line': ln, 'job': RE_JOB.search(msg).group(1),
         'reason': jobs[RE_JOB.search(msg).group(1)].get('verdict_reason'), 'text': msg}
        for ln, ts, lvl, msg in verify_lines if 'REFUSED' in msg]
    out['sync_outcomes']['unverified_lines'] = [
        {'line': ln, 'job': RE_JOB.search(msg).group(1), 'text': msg}
        for ln, ts, lvl, msg in verify_lines if 'UNVERIFIED' in msg]
    out['sync_outcomes']['failed_lines'] = [
        {'line': ln, 'job': RE_JOB.search(msg).group(1), 'text': msg}
        for ln, ts, lvl, msg in verify_lines if 'failed' in msg]

    # ------------------------------------------------------------- 5. WARN / ERROR
    warn = [(ln, ts, msg) for ln, ts, lvl, msg in recs if lvl == 'WARN']
    err = [(ln, ts, msg) for ln, ts, lvl, msg in recs if lvl == 'ERROR']
    fam = collections.Counter()
    for ln, ts, msg in warn:
        if 'past the fetch' in msg:
            fam['extract: <file> read X MB past the fetch ...'] += 1
        elif msg.startswith('reference: method=index-none'):
            fam['reference: method=index-none ... reason=<r> file=<f>'] += 1
        elif msg.startswith('extract fallback to ffmpeg:'):
            fam['extract fallback to ffmpeg: file=<f> ... reasons=<r>'] += 1
        elif msg.startswith('extract plan:'):
            fam['extract plan: <file> ... - this pass <why>'] += 1
        elif msg.startswith('extract: rejected'):
            fam['extract: rejected file=<f> stream=<s> exit=<n> reason=<r>'] += 1
        else:
            fam['OTHER: ' + msg[:90]] += 1
    out['warn_error'] = {
        'warn_lines': len(warn), 'error_lines': len(err),
        'warn_distinct_messages': len(set(w[2] for w in warn)),
        'error_distinct_messages': len(set(e[2] for e in err)),
        'warn_line_numbers': [w[0] for w in warn],
        'error_line_numbers': [e[0] for e in err],
        'warn_families': dict(fam),
        'error_families': dict(collections.Counter(e[2] for e in err)),
    }
    dumps['warn'] = warn
    dumps['error'] = err

    # ------------------------------------------------------------- 6. [bound, not verified]
    bound = L.grep(re.escape('[bound, not verified]'))
    out['bound_not_verified'] = {
        'count': len(bound),
        'line_numbers': [b[0] for b in bound],
        'lines': [{'line': b[0], 'text': b[3]} for b in bound],
        'distinct_files': sorted(set(re.match(r'^extract plan: (.*?) (cue-indexed|shared-pass|cluster-walk)', b[3]).group(1)
                                    for b in bound)),
        'has_job_id_in_line': any(RE_JOB.search(b[3]) for b in bound),
        'grep_proof_command': "$ python3 -c \"import re;print('[bound, not verified]', sum(1 for l in open('/tmp/logwork/snapshot.log',encoding='utf-8') if '[bound, not verified]' in l))\"",
    }

    # ------------------------------------------------------------- 7. bytes / read counts
    bytes_vals, bytes_unknown = [], 0
    for j in done_jobs:
        b = j['bytes_raw']
        if b == 'unknown':
            bytes_unknown += 1
        else:
            bytes_vals.append((j['completed_line'], j['id'], int(b)))
    bvals = [v[2] for v in bytes_vals]
    walks = [(j['walk_line'], j['id'], j['walk_mb'], j['walk_s'], j['walk_mbs'])
             for j in jobs.values() if 'walk_mb' in j]
    walk_mb = [w[2] for w in walks]
    walk_mbs = [w[4] for w in walks]
    out['read_byte_counts'] = {
        'completions_with_bytes_field': len(done_jobs),
        'bytes_unknown': bytes_unknown,
        'bytes_numeric_count': len(bvals),
        'bytes_min': min(bvals), 'bytes_max': max(bvals), 'bytes_tukey': tukey(bvals),
        'bytes_values': [{'line': v[0], 'job': v[1], 'bytes': v[2]} for v in bytes_vals],
        'walk_moved_lines': len(walks),
        'walk_moved_values': [{'line': w[0], 'job': w[1], 'mb': w[2], 's': w[3], 'mb_s': w[4]} for w in walks],
        'walk_mb_min': min(walk_mb), 'walk_mb_max': max(walk_mb), 'walk_mb_tukey': tukey(walk_mb),
        'walk_mbs_min': min(walk_mbs), 'walk_mbs_max': max(walk_mbs), 'walk_mbs_tukey': tukey(walk_mbs),
    }
    plans = []
    for ln, ts, lvl, msg in recs:
        m = re.match(r'^extract plan: (?P<f>.*?) (?P<kind>cue-indexed|shared-pass|cluster-walk)'
                     r'(?: \[bound, not verified\])? expected (?P<emb>' + NUM + r') MB/(?P<er>\d+) read\(s\) '
                     r'\((?P<ems>' + NUM + r') ms\), actual (?P<amb>' + NUM + r') MB/(?P<ar>\d+) read\(s\) '
                     r'\((?P<ams>' + NUM + r') ms\) - bytes (?P<bx>' + NUM + r')x, reads (?P<rx>' + NUM + r')x', msg)
        if m:
            plans.append({'line': ln, 'file': m.group('f'), 'kind': m.group('kind'),
                          'expected_mb': num(m.group('emb')), 'expected_reads': int(m.group('er')),
                          'actual_mb': num(m.group('amb')), 'actual_reads': int(m.group('ar')),
                          'bytes_x': num(m.group('bx')), 'reads_x': num(m.group('rx'))})
    out['read_byte_counts']['extract_plan_lines'] = len(plans)
    out['read_byte_counts']['extract_plan_expected_mb_tukey'] = tukey([p['expected_mb'] for p in plans])
    out['read_byte_counts']['extract_plan_actual_mb_tukey'] = tukey([p['actual_mb'] for p in plans])
    out['read_byte_counts']['extract_plan_bytes_ratio_tukey'] = tukey([p['bytes_x'] for p in plans])
    dumps['plans'] = plans

    # ------------------------------------------------------------- 8. offsets / scores
    offs = [j for j in jobs.values() if j.get('offset_s') is not None and j.get('score') is not None]
    off_vals = [j['offset_s'] for j in offs]
    scores = [j['score'] for j in offs]
    st = tukey(scores)
    out['offsets'] = {
        'alignment_lines': len(L.grep(r'\] ffsubsync alignment: ')),
        'offsets_parsed': len(off_vals),
        'offset_min': min(off_vals), 'offset_max': max(off_vals),
        'offset_zero': len([o for o in off_vals if o == 0.0]),
        'offset_abs_gt_5s': [{'job': j['id'], 'file': j.get('file'), 'offset_s': j['offset_s'],
                              'score': j.get('score'), 'score_raw': j.get('score_raw'),
                              'line': j['align_line'], 'verdict': j.get('verdict', 'completed'),
                              'wrote_file': j.get('wrote_file', False),
                              'written_ms': j.get('change_offset_ms'), 'against': j['align_against']}
                             for j in offs if abs(j['offset_s']) > 5],
        'offset_negative': [{'job': j['id'], 'file': j.get('file'), 'offset_s': j['offset_s'],
                             'score': j.get('score'), 'line': j['align_line'],
                             'verdict': j.get('verdict', 'completed'),
                             'wrote_file': j.get('wrote_file', False),
                             'written_ms': j.get('change_offset_ms')} for j in offs if j['offset_s'] < 0],
        'against_breakdown': dict(collections.Counter(j['align_against'] for j in offs)),
        'score_min': min(scores), 'score_max': max(scores), 'score_tukey': st,
        'score_outliers': [{'job': j['id'], 'file': j.get('file'), 'score': j['score'],
                            'score_raw': j.get('score_raw'), 'offset_s': j['offset_s'],
                            'line': j['align_line'], 'verdict': j.get('verdict', 'completed')}
                           for j in offs if not (st['lower_fence'] <= j['score'] <= st['upper_fence'])],
        'negative_scores': [{'job': j['id'], 'file': j.get('file'), 'score': j['score'],
                             'offset_s': j['offset_s'], 'line': j['align_line'],
                             'note': j['align_against'], 'verdict': j.get('verdict', 'completed')}
                            for j in offs if j['score'] < 0],
        'non_integer_scores': [{'job': j['id'], 'score': j['score'], 'score_raw': j.get('score_raw'),
                                'line': j['align_line']} for j in offs
                               if j.get('score_raw') and ('.' in j['score_raw'] or ',' in j['score_raw'])],
    }
    dumps['offsets_all'] = [{'job': j['id'], 'file': j.get('file'), 'score': j.get('score'),
                             'score_raw': j.get('score_raw'), 'offset_s': j['offset_s'],
                             'against': j['align_against'], 'verdict': j.get('verdict', 'completed'),
                             'written': 'completed_line' in j, 'line': j['align_line']} for j in offs]

    # ------------------------------------------------------------- 9. framerate / rescale
    fr_jobs = [j for j in jobs.values() if 'framerate_msgs' in j]
    fr_kinds = collections.Counter()
    for j in fr_jobs:
        for m in j['framerate_msgs']:
            if 'a framerate pair' in m:
                fr_kinds['rescale: a framerate pair'] += 1
            elif 'no rescale' in m:
                fr_kinds['no rescale: reference is the odd one out'] += 1
            elif 'does NOT hold against the audio' in m:
                fr_kinds['stretch does NOT hold against the audio'] += 1
            elif 'the stretch is tested against' in m:
                fr_kinds['stretch tested against the audio'] += 1
            else:
                fr_kinds['OTHER: ' + m[:90]] += 1
    out['framerate'] = {
        'jobs_with_framerate_line': len(fr_jobs),
        'framerate_lines_total': len(L.grep(r'\] framerate: ')),
        'kinds': dict(fr_kinds),
        'rescale_jobs': [{'job': j['id'], 'file': j.get('file'), 'offset_s': j.get('offset_s'),
                          'score': j.get('score'), 'verdict': j.get('verdict', 'completed'),
                          'line': j['framerate_lines'][0]}
                         for j in fr_jobs if any('a framerate pair' in m for m in j['framerate_msgs'])],
        'stretch_failed_jobs': [{'job': j['id'], 'file': j.get('file'), 'offset_s': j.get('offset_s'),
                                 'score': j.get('score'), 'verdict': j.get('verdict', 'completed'),
                                 'line': j['framerate_lines'][0]}
                                for j in fr_jobs if any('does NOT hold against the audio' in m for m in j['framerate_msgs'])],
    }
    dumps['framerate_msgs'] = sorted(set(m for j in fr_jobs for m in j['framerate_msgs']))

    # ------------------------------------------------------------- 10. process / queue
    out['queue_and_process'] = {
        'queue_status_lines': len(L.grep(r'^queue: \d+ queued, \d+ running')),
        'dispatch_lines': len(L.grep(r'^dispatch: ')),
        'dispatch_lines_with_detail': len(L.grep(r'^dispatch: .* — starting ')),
        'queue_lock_slow_lines': len(L.grep(r'^queue lock slow: ')),
        'queue_lock_slow_max_ms': max(int(re.search(r'ms=(\d+)', r[3]).group(1))
                                     for r in L.grep(r'^queue lock slow: ')),
        'startup_lines': len(L.grep(r'^startup: version=')),
        'startup_versions': dict(collections.Counter(re.search(r'version=(\S+)', r[3]).group(1)
                                                     for r in L.grep(r'^startup: version='))),
        'teardown_lines': len(L.grep(r'^teardown: ')),
        'batch_history_lines': len(L.grep(r'^batch history: restored ')),
        'batch_queued_lines': len(L.grep(r'^batch [0-9a-f]{32} queued: tasks=')),
        'walk_ceiling_lines': len(L.grep(r'^walk ceiling: ')),
    }
    for name, pat in (('startup_events', r'^startup: version='), ('teardown_events', r'^teardown: '),
                      ('batch_history_events', r'^batch history: restored '),
                      ('batch_queued_events', r'^batch [0-9a-f]{32} queued: tasks=')):
        out['queue_and_process'][name] = [{'line': r[0], 'ts': r[1], 'text': r[3]} for r in L.grep(pat)]

    # ------------------------------------------------------------- 11. exceptions etc
    exc = L.grep(r'^\s+(at |System\.)')
    out['exception_lines'] = {
        'count': len(exc), 'line_numbers': [e[0] for e in exc],
        'exception_heads': sorted(set(e[3].split(' :')[0][:160] for e in exc
                                      if not e[3].lstrip().startswith('at '))),
    }
    out['rejected_extractions'] = [{'line': r[0], 'ts': r[1], 'text': r[3]}
                                   for r in L.grep(r'^extract: rejected ')]
    out['ffmpeg_fallbacks'] = [{'line': r[0], 'ts': r[1], 'text': r[3]}
                               for r in L.grep(r'^extract fallback to ffmpeg: ')]

    # ------------------------------------------------------------- 12. per-batch tallies
    batch_done = collections.defaultdict(collections.Counter)
    for j in jobs.values():
        if j.get('batch'):
            batch_done[j['batch']]['completed' if 'completed_line' in j else j.get('verdict', 'no verdict')] += 1
    out['per_batch'] = {b: dict(c) for b, c in batch_done.items()}
    disp = []
    for ln, ts, lvl, msg in recs:
        m = re.match(r'^dispatch: starting (?P<st>\d+), running (?P<run>\d+), limit (?P<lim>\d+), '
                     r'queued (?P<q>\d+), batch (?P<b>[0-9a-f]{32})', msg)
        if m:
            disp.append({'line': ln, 'ts': ts, 'starting': int(m.group('st')), 'running': int(m.group('run')),
                         'limit': int(m.group('lim')), 'queued': int(m.group('q')), 'batch': m.group('b')})
    out['dispatch_summary'] = {
        'lines': len(disp), 'distinct_batches': len(set(d['batch'] for d in disp)),
        'max_queued_seen': max(d['queued'] for d in disp), 'min_queued_seen': min(d['queued'] for d in disp),
        'first': disp[0], 'last': disp[-1],
    }
    dumps['dispatch'] = disp

    # ------------------------------------------------------------- 13. run accounting
    queued_ids = set(j['id'] for j in jobs.values() if 'queued_lineno' in j)
    terminal_ids = set(j['id'] for j in jobs.values() if 'completed_line' in j or j.get('verdict'))
    engine_ids = set(j['id'] for j in jobs.values() if 'start_line' in j)
    pending = [j for j in jobs.values() if 'queued_lineno' in j and j['id'] not in terminal_ids]
    orphan = [j for j in jobs.values() if 'queued_lineno' not in j and j['id'] in terminal_ids]
    eng_nostop = [j for j in jobs.values() if 'start_line' in j and 'exit_line' not in j]
    eng_noend = [j for j in jobs.values() if 'start_line' in j and j['id'] not in terminal_ids]
    out['run_accounting'] = {
        'queued_job_ids': len(queued_ids),
        'terminal_job_ids': len(terminal_ids),
        'engine_run_job_ids': len(engine_ids),
        'queued_without_terminal': len(pending),
        'queued_without_terminal_first_ts': min((j['queued_ts'] for j in pending), default=None),
        'queued_without_terminal_last_ts': max((j['queued_ts'] for j in pending), default=None),
        'queued_without_terminal_by_batch': dict(collections.Counter(j.get('batch') for j in pending)),
        'terminal_without_queued_line_here': len(orphan),
        'terminal_without_queued_line_first_ts': min((j.get('completed_ts') or j.get('verdict_ts') for j in orphan), default=None),
        'terminal_without_queued_line_last_ts': max((j.get('completed_ts') or j.get('verdict_ts') for j in orphan), default=None),
        'engine_start_without_exit': len(eng_nostop),
        'engine_start_without_terminal': len(eng_noend),
        'engine_start_without_terminal_detail': [
            {'job': j['id'], 'start_ts': j['start_ts'], 'file': j.get('file')} for j in eng_noend],
        'duplicate_queued_job_ids': len([1 for j in jobs.values() if 'queued_lineno' in j]) - len(queued_ids),
        'duplicate_engine_runs_per_job': len([j for j in jobs.values() if 'start_line' in j]) - len(engine_ids),
        'snapshot_last_ts': max(r[1] for r in recs if r[1]),
    }
    dumps['pending_jobs'] = [{'job': j['id'], 'ts': j['queued_ts'], 'batch': j.get('batch'),
                              'language': j.get('language'), 'file': j.get('file'),
                              'stream': j.get('stream')} for j in pending]

    # -------------------------------------------------- 14. written offsets (what landed)
    written = [j for j in jobs.values() if j.get('wrote_file')]
    written_off = [j for j in written if j.get('change_offset_ms') is not None]
    out['written_offsets'] = {
        'jobs_that_wrote_a_file': len(written),
        'jobs_with_ms_offset_in_change': len(written_off),
        'max_abs_written_ms': max((abs(j['change_offset_ms']) for j in written_off), default=None),
        'written_ms_gt_5000': [{'job': j['id'], 'file': j.get('file'), 'change_ms': j['change_offset_ms'],
                                'align_offset_s': j.get('offset_s'), 'score': j.get('score'),
                                'line': j['completed_line']} for j in written_off if abs(j['change_offset_ms']) > 5000],
        'written_ms_negative': [{'job': j['id'], 'file': j.get('file'), 'change_ms': j['change_offset_ms'],
                                 'align_offset_s': j.get('offset_s'), 'score': j.get('score'),
                                 'line': j['completed_line']} for j in written_off if j['change_offset_ms'] < 0],
        'written_offsets_ms_tukey': tukey([abs(j['change_offset_ms']) for j in written_off]),
        'change_kinds': dict(collections.Counter(
            re.sub(r'[-+−]?\d+', '<N>', j['change']) for j in jobs.values() if j.get('change'))),
    }

    out['offsets']['flagged_union_count'] = len(set(r['job'] for r in out['offsets']['offset_abs_gt_5s'])
                                                  | set(r['job'] for r in out['offsets']['offset_negative']))
    out['offsets']['score_outlier_count'] = len(out['offsets']['score_outliers'])
    _w = set(r['job'] for r in out['written_offsets']['written_ms_gt_5000']) | set(r['job'] for r in out['written_offsets']['written_ms_negative'])
    out['written_offsets']['flagged_union_count'] = len(_w)
    out['written_offsets']['flagged_union_jobs'] = sorted(_w)
    # -------------------------------------------------- 15. queue lock / walk ceiling
    qls = L.grep(r'^queue lock slow: ')
    out['queue_lock_slow'] = {
        'lines': len(qls),
        'max_ms': max(int(re.search(r'ms=(\d+)', r[3]).group(1)) for r in qls),
        'holders': dict(collections.Counter(re.search(r'holder=(\S+)', r[3]).group(1) for r in qls)),
        'over_1000ms': len([r for r in qls if int(re.search(r'ms=(\d+)', r[3]).group(1)) > 1000]),
    }
    wc = L.grep(r'^walk ceiling: ')
    out['walk_ceiling'] = {
        'lines': len(wc),
        'distinct_volumes': sorted(set(re.search(r'holding (\S+) at', r[3]).group(1) for r in wc)),
        'thrashing_lines': len([r for r in wc if 'thrashing' in r[3]]),
    }
    exc2 = L.grep(r'^(System\.|\s+at )')
    out['exception_lines'] = {
        'count': len(exc2), 'line_numbers': [e[0] for e in exc2],
        'exception_heads': sorted(set(e[3].split(' at ')[0][:170] for e in exc2
                                      if not e[3].lstrip().startswith('at '))),
    }
    out['read_byte_counts']['window_edge'] = {
        'lines_ts_after_2026_09_19T00_19_59_999Z': len([r for r in recs if r[1] and r[1] > '2026-09-19 00:19:59.999']),
        'first_ts_after_window': next((r[1] for r in recs if r[1] and r[1] > '2026-09-19 00:19:59.999'), None),
    }

    # ------------------------------------------------------------- 16. derived extras
    ob = collections.OrderedDict()
    for j in jobs.values():
        if not j.get('batch'):
            continue
        b = ob.setdefault(j['batch'], {'items': set(), 'files': set(), 'first_ts': None, 'last_ts': None,
                                       'tasks': 0})
        b['tasks'] += 1
        if j.get('item'):
            b['items'].add(j['item'])
        if j.get('file'):
            b['files'].add(j['file'])
        for t in (j.get('queued_ts'), j.get('completed_ts'), j.get('verdict_ts'), j.get('align_ts')):
            if t:
                b['first_ts'] = t if b['first_ts'] is None or t < b['first_ts'] else b['first_ts']
                b['last_ts'] = t if b['last_ts'] is None or t > b['last_ts'] else b['last_ts']
    out['per_batch_detail'] = {k: {'tasks': v['tasks'], 'items': len(v['items']), 'files': len(v['files']),
                                   'first_ts': v['first_ts'], 'last_ts': v['last_ts']} for k, v in ob.items()}

    # union of everything flagged as a suspicious offset
    flag = {}
    for row in out['offsets']['offset_abs_gt_5s']:
        flag.setdefault(row['job'], {'job': row['job'], 'file': row['file'], 'align_offset_s': row['offset_s'],
                                     'score': row['score'], 'written_ms': row.get('written_ms'),
                                     'wrote_file': row.get('wrote_file'), 'flags': []})
        flag[row['job']]['flags'].append('align |offset| > 5 s')
    for row in out['written_offsets']['written_ms_gt_5000']:
        flag.setdefault(row['job'], {'job': row['job'], 'file': row['file'], 'align_offset_s': row.get('align_offset_s'),
                                     'score': row.get('score'), 'written_ms': row['change_ms'],
                                     'wrote_file': True, 'flags': []})
        flag[row['job']]['flags'].append('written |offset| > 5000 ms')
    for row in out['written_offsets']['written_ms_negative']:
        flag.setdefault(row['job'], {'job': row['job'], 'file': row['file'], 'align_offset_s': row.get('align_offset_s'),
                                     'score': row.get('score'), 'written_ms': row['change_ms'],
                                     'wrote_file': True, 'flags': []})
        flag[row['job']]['flags'].append('written offset negative')
    for row in out['offsets']['offset_negative']:
        flag.setdefault(row['job'], {'job': row['job'], 'file': row['file'], 'align_offset_s': row['offset_s'],
                                     'score': row['score'], 'written_ms': None,
                                     'wrote_file': row.get('wrote_file'), 'flags': []})
        flag[row['job']]['flags'].append('align offset negative')
    for row in out['offsets']['score_outliers']:
        flag.setdefault(row['job'], {'job': row['job'], 'file': row['file'], 'align_offset_s': row.get('offset_s'),
                                     'score': row['score'], 'written_ms': None, 'wrote_file': None, 'flags': []})
        flag[row['job']]['flags'].append('engine score outlier')
    for row in out['offsets']['negative_scores']:
        flag.setdefault(row['job'], {'job': row['job'], 'file': row['file'], 'align_offset_s': row.get('offset_s'),
                                     'score': row['score'], 'written_ms': None, 'wrote_file': None, 'flags': []})
        flag[row['job']]['flags'].append('engine score negative')
    out['flagged_jobs'] = sorted(flag.values(), key=lambda r: r['job'])
    out['flagged_jobs_count'] = len(flag)

    # framerate: jobs per kind (not lines)
    fk = collections.Counter()
    for j in fr_jobs:
        if any('a framerate pair' in m for m in j['framerate_msgs']):
            fk['rescale: a framerate pair'] += 1
        elif any('no rescale' in m for m in j['framerate_msgs']):
            fk['no rescale: reference is the odd one out'] += 1
        else:
            fk['other'] += 1
    out['framerate']['jobs_per_kind'] = dict(fk)
    out['framerate']['all_jobs'] = [{'job': j['id'], 'file': j.get('file'),
                                     'ratios': [num(m) for m in re.findall(r'(' + NUM + r')x the reference', ' '.join(j['framerate_msgs']))],
                                     'kind': ('rescale' if any('a framerate pair' in m for m in j['framerate_msgs'])
                                              else 'no-rescale' if any('no rescale' in m for m in j['framerate_msgs'])
                                              else 'other'),
                                     'verdict': j.get('verdict', 'completed'),
                                     'line': j['framerate_lines'][0]} for j in fr_jobs]
    out['framerate']['extreme_ratio_jobs'] = [
        {'job': j['job'], 'file': j['file'], 'ratios': j['ratios'], 'verdict': j['verdict'], 'line': j['line']}
        for j in out['framerate']['all_jobs']
        if any((x is not None and (x > 1.5 or x < 0.9)) for x in j['ratios'])]
    out['framerate']['extreme_ratio_count'] = len(out['framerate']['extreme_ratio_jobs'])
    out['stretched_to_jobs'] = [
        {'job': j['id'], 'file': j.get('file'), 'change': j.get('change'), 'bytes': j.get('bytes_raw'),
         'line': j['completed_line']}
        for j in done_jobs if 'stretched to' in (j.get('change') or '')]
    out['stretched_to_count'] = len(out['stretched_to_jobs'])
    out['engine_identity_lines'] = len(L.grep(r'^engine identity: '))
    out['lane_stopped_lines'] = len(L.grep(r'^extract lane: stopped'))
    ex_ms = [int(m.group(2)) for m in (re.search(r'ffsubsync exit=(\S+) after (\d+) ms', r[3]) for r in recs) if m]
    out['engine_exit_codes'] = dict(collections.Counter(
        m.group(1) for m in (re.search(r'ffsubsync exit=(\S+) after', r[3]) for r in recs) if m))
    out['engine_run_ms'] = {'n': len(ex_ms), 'min': min(ex_ms), 'median': sorted(ex_ms)[len(ex_ms) // 2],
                            'max': max(ex_ms), 'sum_seconds': round(sum(ex_ms) / 1000.0, 1)}
    out['engine_longest_run'] = {'job': best[0], 'line': best[1], 'ms': best[2]} if (best := max(
        ((m.group(1), r[0], int(m.group(2))) for r in recs
         for m in [re.search(r'\[(?P<j>[0-9a-f]{32})\] ffsubsync exit=\S+ after (?P<ms>\d+) ms', r[3])] if m),
        key=lambda x: x[2], default=None)) else None
    out['recovery_ruler_discarded_lines'] = len(L.grep(r'discarding that track as a ruler'))
    out['speech_cache_reference_lines'] = len(L.grep(r'reference: method=speech-cache'))
    out['lane_summary_lines'] = len(L.grep(r'^extract lane: \S.* -> \d+/\d+ subtitle'))
    out['lane_summary_ok_true'] = len(L.grep(r'^extract lane: \S.* -> \d+/\d+ subtitle.*ok=True'))
    out['lane_summary_ok_false'] = len(L.grep(r'^extract lane: \S.* -> \d+/\d+ subtitle.*ok=False'))
    out['lane_ok_false_lines'] = [{'line': r[0], 'ts': r[1], 'text': r[3]} for r in L.grep(r'^extract lane: \S.* ok=False')]
    out['lane_started_lines'] = len(L.grep(r'^extract lane: started$'))
    out['idle_before_startup'] = [
        {'startup_line': r[0], 'startup_ts': r[1],
         'previous_log_ts': max([x[1] for x in recs if x[1] and x[0] < r[0]], default=None)}
        for r in L.grep(r'^startup: version=')]
    out['commands'] = {
        'snapshot': "cp /subsync-logs/subsync.log /tmp/logwork/snapshot.log && md5sum /tmp/logwork/snapshot.log",
        'run_parser': "python3 tests/backend/analyse_log_2026_09_18.py --log /tmp/logwork/snapshot.log "
                      "--json tests/backend/log-analysis-2026-09-18.json --dumpdir /tmp/logwork",
        'line_count': "wc -l /tmp/logwork/snapshot.log",
        'warn_error': "grep -nE ' (WARN|ERROR) ' /tmp/logwork/snapshot.log",
        'bound': "grep -n '\\[bound, not verified\\]' /tmp/logwork/snapshot.log",
        'done_paren': "grep -c 'Done (' /tmp/logwork/snapshot.log",
        'walk_moved': "grep -n 'this walk moved' /tmp/logwork/snapshot.log",
        'bytes': "grep -o 'bytes=[^ ]*' /tmp/logwork/snapshot.log | sort | uniq -c",
        'outcomes': "grep -oE 'job [0-9a-f]{32} (completed|UNVERIFIED|REFUSED|failed)' /tmp/logwork/snapshot.log | awk '{print $3}' | sort | uniq -c",
        'startups': "grep -n 'startup: version=' /tmp/logwork/snapshot.log",
    }

    # ------------------------------------------- 17. last-alignment-negative analysis
    MINUS = '\u2212'
    re_al = re.compile(r'\[(?P<job>[0-9a-f]{32})\] ffsubsync alignment: score=(?P<score>' + NUM + r') '
                       r'offset=(?P<off>' + NUM + r') s (?P<against>.*)$')
    last_align = {}
    for ln, ts, lvl, msg in recs:
        m = re_al.search(msg)
        if m:
            last_align[m.group('job')] = {'line': ln, 'score': num(m.group('score')),
                                          'offset_s': num(m.group('off')), 'against': m.group('against')}
    neg = []
    for jid, al in last_align.items():
        if al['score'] is None or al['score'] >= 0:
            continue
        j = jobs.get(jid, {})
        neg.append({'job': jid, 'align_line': al['line'], 'score': al['score'], 'offset_s': al['offset_s'],
                    'against': al['against'], 'file': j.get('file'),
                    'verdict': j.get('verdict', 'completed' if 'completed_line' in j else '<no terminal line>'),
                    'wrote_file': bool(j.get('wrote_file')), 'output': j.get('output'),
                    'bytes': j.get('bytes_raw'), 'change': j.get('change'),
                    'completed_line': j.get('completed_line')})
    out['negative_score_final'] = {
        'jobs_whose_last_alignment_score_is_negative': len(neg),
        'of_those_that_wrote_a_file': len([r for r in neg if r['wrote_file']]),
        'of_those_that_wrote_nothing': len([r for r in neg if not r['wrote_file']]),
        'written_with_change_unknown': len([r for r in neg if r['wrote_file'] and r['change'] == 'unknown']),
        'written_with_forced_signs_change': len([r for r in neg if r['wrote_file'] and 'forced/signs' in (r['change'] or '')]),
        'max_abs_offset_written': max([abs(r['offset_s']) for r in neg if r['wrote_file']], default=None),
        'min_abs_offset_written': min([abs(r['offset_s']) for r in neg if r['wrote_file']], default=None),
        'rows': sorted(neg, key=lambda r: r['align_line']),
    }
    out['completed_before_2150z'] = sum(D2.get('completed', 0) for k, D2 in
                                        ((k, v) for k, v in out['per_batch'].items()
                                         if k in ('b76b77e3edb5450999bc5d8e207cb679',
                                                  'cd29cd12a01e4be89018d3b9ae6751b8', '(standalone)')))
    forced = [{'job': j['id'], 'file': j.get('file'), 'output': j.get('output'),
               'bytes': j.get('bytes_raw'), 'change': j.get('change'), 'line': j['completed_line']}
              for j in done_jobs if 'forced/signs track' in (j.get('change') or '')]
    out['forced_signs_written'] = {'count': len(forced), 'rows': forced}
    out['change_unknown_written'] = len([j for j in done_jobs if j.get('change') == 'unknown'])

    # ------------------------------------------------------------- write out
    os.makedirs(a.dumpdir, exist_ok=True)
    for name, val in dumps.items():
        with open(os.path.join(a.dumpdir, name + '.json'), 'w', encoding='utf-8') as fh:
            json.dump(val, fh, ensure_ascii=False, indent=1, default=str)
    with open(os.path.join(a.dumpdir, 'jobs.json'), 'w', encoding='utf-8') as fh:
        json.dump(list(jobs.values()), fh, ensure_ascii=False, indent=1, default=str)
    with open(a.json, 'w', encoding='utf-8') as fh:
        json.dump(out, fh, ensure_ascii=False, indent=2, default=str)
    print('wrote', a.json)
    print(json.dumps(out['log'], ensure_ascii=False, indent=1))


if __name__ == '__main__':
    main()
