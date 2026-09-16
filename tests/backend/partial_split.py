#!/usr/bin/env python3
"""Move one cluster of `SubSyncService` into its own partial-class file.

Phase 2 of `knowledge/SUBSYNCSERVICE_MAP.md` section 7: the tangled clusters (C1, C2, C3, C5, C8) share the
run-state, so they cannot become real classes yet - but they can stop sharing a file. A partial class is one
class across files: accessibility unchanged, DI unchanged, no call site moves, and nothing about the state
changes, so the move is a text move and can be proved to be one.

The cluster's member list is read from the map (section 1 is the inventory of record), the regions are computed
from the current file, and each moved region is compared byte-for-byte against what the file held before:
`moved byte-identical` counts the blocks that came through untouched, and anything else is printed instead of
being quietly accepted.

Usage:
    python3 tests/backend/partial_split.py C8 [--dry-run]

The header of a new part copies the main file's `using` list rather than guessing which ones the moved code
needs: an unused directive costs one line, a missing one costs a build.
"""

import argparse
import pathlib
import re
import sys

REPO = pathlib.Path(__file__).resolve().parents[2]
SERVICE = REPO / 'Jellyfin.Plugin.SubSync' / 'Services' / 'SubSyncService.cs'
MAP = REPO / 'knowledge' / 'SUBSYNCSERVICE_MAP.md'
SERVICE_DIR = SERVICE.parent

# cluster -> (file name, what the file holds, the map heading its member table sits under)
CLUSTERS = {
    'C1': ('SubSyncService.Queue.cs', 'the queue surface: enqueue, batch, cancel, kill, job lookup',
           '### C1 queue/dispatch'),
    'C2': ('SubSyncService.Scheduler.cs', 'the pump, the wave policy and the walk/read-cost policy',
           '### C2 scheduler'),
    'C3': ('SubSyncService.Extraction.cs', 'the extraction lanes, the extraction chain and the extracted-text caches',
           '### C3 extraction'),
    'C5': ('SubSyncService.JobPipeline.cs', 'the sync job pipeline: the engine attempt, the reference, the write and the outcome',
           '### C5 job pipeline'),
    'C8': ('SubSyncService.SweepHistory.cs', 'the read models, the access surface, the sweep and the batch history',
           '### C8 API read models'),
}

# Nested types that belong to a cluster: the data shapes only that cluster builds and reads.
EXTRAS = {
    'C1': ['BatchCreation', 'SyncQueueConflictException'],
    'C2': ['WavePolicy'],
    'C3': [],
    'C5': ['SyncWriteOutcome', 'ReferenceResolution'],
    'C8': ['SyncTarget'],
}


def cluster_member_names(cluster):
    """The member names of a cluster, from the map's inventory table (section 1)."""
    text = MAP.read_text(encoding='utf-8')
    start = text.index(CLUSTERS[cluster][2])
    nxt = text.find('\n### ', start + 10)
    section = text[start:nxt if nxt > 0 else len(text)]
    return re.findall(r'\|\s*`([A-Za-z0-9_]+)`\s*\|\s*\d+\s*\|\s*\d+\s*\|', section)


def parse_service(text):
    """Every top-level member of the class, as (declaration line, last line) plus its name.

    Depth-based rather than regex-per-member: the file contains string literals with braces in them (the ffmpeg
    filter graphs, the SRT templates), so brace counting alone would drift and a drifted parser cuts a member in
    half. Multi-line string literals are tracked so their lines are never mistaken for a declaration.
    """
    lines = text.split('\n')
    depths = [0] * (len(lines) + 1)
    i = 0
    depth = 0
    while i < len(text):
        c = text[i]
        if c == '/' and i + 1 < len(text) and text[i + 1] == '/':
            j = text.find('\n', i)
            i = len(text) if j < 0 else j
            continue
        if c == '/' and i + 1 < len(text) and text[i + 1] == '*':
            j = text.find('*/', i + 2)
            i = len(text) if j < 0 else j + 2
            continue
        if c in '"\'':
            j = i + 1
            while j < len(text):
                if text[j] == '\\':
                    j += 2
                    continue
                if text[j] == c:
                    break
                j += 1
            i = j + 1
            continue
        if c == '{':
            depth += 1
        elif c == '}':
            depth -= 1
        elif c == '\n':
            depths[text.count('\n', 0, i + 1)] = depth
        i += 1

    class_line = next(k for k, l in enumerate(lines, 1) if re.match(r'^public (?:partial )?class SubSyncService', l))
    members = []
    k = class_line
    while k <= len(lines):
        if re.match(r'^    [A-Za-z/\[]', lines[k - 1]) and depths[k] == 1:
            if re.match(r'^    (///|//|\[)', lines[k - 1]):
                k += 1
                continue
            j = k
            while j <= len(lines):
                if j > k and depths[j] == 1 and re.match(r'^    [A-Za-z/\[]', lines[j - 1]):
                    break
                j += 1
            last = min(j - 1, len(lines))
            members.append((k, last, member_name(lines[k - 1])))
            k = j
        else:
            k += 1
    return lines, members, class_line


def member_name(declaration):
    """The declared name, robust to tuple return types: `private (a, b) Name(...)`."""
    head = re.split(r'\s=\s|\s=>\s', declaration.strip())[0]
    if '(' in head:
        before = head[:head.rindex('(')].strip()
        tail = re.findall(r'[A-Za-z_][A-Za-z0-9_]*$', before)
        return tail[0] if tail else before[-20:]
    tail = re.findall(r'[A-Za-z_][A-Za-z0-9_]*', head)
    return tail[-1] if tail else head[:20]


def doc_start(lines, first):
    """The first line of the doc comment that belongs to a member (so the docs move with it)."""
    j = first - 1
    while j >= 1 and lines[j - 1].strip().startswith('//'):
        j -= 1
    return j + 1


def only_noise(lines, first, last):
    """True when every line in the range is blank or a comment (so a region may span it)."""
    return all(not lines[k - 1].strip() or lines[k - 1].strip().startswith('//')
               for k in range(first, last + 1))


def regions_for(cluster, lines, members):
    """The cluster's members as maximal contiguous regions, extra nested types merged in."""
    wanted = set(cluster_member_names(cluster))
    picked = []
    used = set()
    for name in cluster_member_names(cluster):
        for index, (first, last, member) in enumerate(members):
            if member == name and index not in used:
                used.add(index)
                picked.append(index)
                break
    for extra in EXTRAS[cluster]:
        # Match the declaration, not the extracted name: `public sealed class X : InvalidOperationException`
        # extracts as `InvalidOperationException`, which silently left that type behind the first time.
        for index, (first, last, member) in enumerate(members):
            if member == extra or re.search(rf'\bclass {re.escape(extra)}\b', lines[first - 1]):
                used.add(index)
                picked.append(index)
                break
    picked.sort()

    regions = []
    current = None
    for index in picked:
        first = doc_start(lines, members[index][0])
        last = members[index][1]
        if current and only_noise(lines, current[1] + 1, first - 1):
            current = (current[0], max(current[1], last), current[2] + [index])
        else:
            if current:
                regions.append(current)
            current = (first, last, [index])
    if current:
        regions.append(current)
    return regions, wanted


def move(cluster, dry_run):
    file_name, blurb, _ = CLUSTERS[cluster]
    text = SERVICE.read_text(encoding='utf-8')
    lines, members, class_line = parse_service(text)
    regions, wanted = regions_for(cluster, lines, members)
    if not regions:
        raise SystemExit(f'{cluster}: no members found - did the map or the file change?')

    found = {members[i][2] for _, _, idx in regions for i in idx}
    missing = sorted(wanted - found)
    usings = [l for l in lines[:class_line - 1] if l.startswith('using ')]
    namespace = next(l for l in lines[:class_line - 1] if l.startswith('namespace '))

    # The new part: the same usings, the same namespace, then the regions in file order.
    body = []
    for index, (first, last, idx) in enumerate(regions):
        body.extend(lines[first - 1:last])
        if index < len(regions) - 1:
            body.append('')
    new_part = '\n'.join(usings + ['', namespace, '',
                                   '/// <summary>',
                                   f'/// {blurb[0].upper()}{blurb[1:]}.',
                                   '/// </summary>',
                                   '/// <remarks>',
                                   f'/// Part of <see cref="SubSyncService"/>, split out of the single file as the {cluster} cluster of',
                                   '/// <c>knowledge/SUBSYNCSERVICE_MAP.md</c>. A partial class is one class across files: the fields,',
                                   '/// the constructor and the call sites are unchanged, so nothing here is a new seam - the state this',
                                   '/// code shares with the rest of the service is still the service\'s own fields, declared in the main',
                                   '/// file.',
                                   '/// </remarks>',
                                   'public partial class SubSyncService',
                                   '{'] + body + ['}', ''])

    # Remove the regions from the main file, last first, leaving one blank line where a block was.
    kept = list(lines)
    for first, last, _ in sorted(regions, key=lambda r: -r[0]):
        del kept[first - 1:last]
        while first - 2 >= 0 and first - 2 < len(kept) and not kept[first - 2].strip() \
                and first - 1 < len(kept) and not kept[first - 1].strip():
            del kept[first - 1]
    declaration = next(k for k, l in enumerate(kept) if re.match(r'^public (?:partial )?class SubSyncService', l))
    kept[declaration] = kept[declaration].replace('public class SubSyncService', 'public partial class SubSyncService')
    new_main = '\n'.join(kept)

    # Purity: every region must still be in the new part, verbatim, and gone from the main file. Then the
    # stronger form of the same claim: the main file must be EXACTLY the original minus the moved lines, with
    # one word added to the class declaration - nothing re-indented, re-wrapped or dropped on the way out.
    intact = sum(1 for first, last, _ in regions if '\n'.join(lines[first - 1:last]) in new_part)
    leftovers = [first for first, last, _ in regions if '\n'.join(lines[first - 1:last]) in new_main]
    moved_lines = sum(last - first + 1 for first, last, _ in regions)
    moved = set()
    for first, last, _ in regions:
        moved.update(range(first, last + 1))
    expected_main = '\n'.join(l for k, l in enumerate(lines, 1) if k not in moved)
    expected_main = expected_main.replace('public class SubSyncService', 'public partial class SubSyncService')
    subtraction_exact = expected_main == new_main

    print(f'{cluster} -> {file_name}')
    print(f'  map names present: {len(found - set(EXTRAS[cluster]))} of {len(wanted)}'
          f'  (+{len(EXTRAS[cluster])} nested types)'
          + (f'  NOT IN THE FILE: {missing}' if missing else ''))
    print(f'  regions: {len(regions)}  lines moved: {moved_lines}')
    print(f'  purity : {intact}/{len(regions)} regions byte-identical in the new file'
          + ('  ! LEFTOVER COPY IN THE MAIN FILE' if leftovers else ''))
    print(f'  subtraction: {"exact" if subtraction_exact else "NOT EXACT - the main file is not original minus moved"}')
    print(f'  service: {len(lines)} -> {len(kept)} lines')
    print(f'  part   : {len(new_part.splitlines())} lines')
    if dry_run:
        print('  dry run: nothing written')
        return
    (SERVICE_DIR / file_name).write_text(new_part, encoding='utf-8')
    SERVICE.write_text(new_main, encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('cluster', choices=sorted(CLUSTERS))
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    move(args.cluster, args.dry_run)
