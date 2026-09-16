#!/usr/bin/env python3
"""Point the mutation drivers at the file each mutation's line now lives in.

A mutation is a text anchor: when a cluster moves to another file, the anchor follows the code. Doing that by
hand is how an anchor is silently left behind - and a mutation whose anchor is not found reports "the source
moved" instead of verifying anything, which reads as a pass at a glance. So the retarget is computed from the
anchors themselves: for every mutation, find the files that contain its anchor text, and when the declared file
no longer does and exactly one candidate does, rewrite the driver's declaration in place.

Anything else - an anchor that no longer resolves anywhere, or one that resolves in two files at once - is
printed and left alone, never guessed.

Usage:
    python3 tests/backend/retarget_mutations.py [--dry-run]
"""

import argparse
import pathlib
import re

REPO = pathlib.Path(__file__).resolve().parents[2]
SERVICES = REPO / 'Jellyfin.Plugin.SubSync' / 'Services'
DRIVERS = [REPO / 'tests' / 'backend' / 'mutation_phase0_checks.py',
           REPO / 'tests' / 'backend' / 'mutation_job_checks.py']


def candidate_files():
    """Every .cs file a mutation could plausibly be anchored in."""
    return sorted(SERVICES.glob('*.cs')) + [REPO / 'Jellyfin.Plugin.SubSync' / 'Api' / 'SubSyncController.cs']


def load_driver(path):
    """The driver's mutation table and file constants, without importing its main."""
    source = path.read_text(encoding='utf-8')
    head = source.split('def environment(')[0]
    head = head.replace('REPO = pathlib.Path(__file__).resolve().parents[2]',
                        f"import pathlib as _p; REPO = _p.Path({str(REPO)!r})")
    name_space = {}
    exec(head, name_space)  # noqa: S102 - our own tooling, and the tables are the point
    return source, name_space


def as_path(value):
    """The declared file as an absolute path: the Phase 0 driver names it relative to the repo."""
    return value if isinstance(value, pathlib.Path) else REPO / value


def ensure_constant(source, target, constants):
    """The name of the file constant for `target`, adding one next to the others when there is none.

    A moved cluster needs its own constant: the alternative is a relative path spelled out inside the mutation
    table, which is how one of these ends up quietly pointing at the wrong file.
    """
    existing = next((c for c, v in constants.items() if v == target), None)
    if existing:
        return source, existing
    relative = target.relative_to(REPO).as_posix()
    stem = target.stem.replace('SubSyncService.', '')
    name = re.sub(r'(?<!^)(?=[A-Z])', '_', stem).upper()
    lines = source.split('\n')
    last = max(i for i, l in enumerate(lines) if re.match(r'^[A-Z_]+ = .*\.cs\'$', l))
    lines.insert(last + 1, f"{name} = '{relative}'")
    return '\n'.join(lines), name


def main(dry_run):
    files = {path: path.read_text(encoding='utf-8') for path in candidate_files()}
    for driver in DRIVERS:
        source, ns = load_driver(driver)
        mutations = ns['MUTATIONS']
        # phase 0 declares the file inside the tuple; the job-checks driver keeps a separate FILES map.
        three = all(isinstance(v, tuple) and len(v) == 3 for v in mutations.values())
        constants = {}
        for name, value in ns.items():
            if name.isupper() and name != 'REPO' and isinstance(value, (str, pathlib.Path)):
                candidate = as_path(value)
                if candidate in files:
                    constants[name] = candidate
        changed, unresolved, ambiguous = [], [], []
        for name, entry in mutations.items():
            declared = as_path(entry[0]) if three else as_path(
                ns.get('FILES', {}).get(name, ns['SERVICE']))
            old = entry[1] if three else entry[0]
            holders = [path for path, text in files.items() if old in text]
            if declared in holders:
                continue
            if not holders:
                unresolved.append(name)
                continue
            if len(holders) > 1:
                ambiguous.append((name, [p.name for p in holders]))
                continue
            target = holders[0]
            if three:
                source, constant = ensure_constant(source, target, constants)
                constants[constant] = target
                replaced = re.sub(rf"('{re.escape(name)}':\s*\()[A-Z_]+(\s*,)", rf'\g<1>{constant}\g<2>', source)
            else:
                constant = next((c for c, v in constants.items() if v == target), None)
                if constant is None:
                    unresolved.append(f'{name} (no file constant for {target.name})')
                    continue
                # the job-checks driver names the file in a FILES map; add or replace the entry there
                if re.search(rf"'{re.escape(name)}':\s*METRICS", source):
                    replaced = re.sub(rf"('{re.escape(name)}':\s*)METRICS", rf'\g<1>{constant}', source)
                else:
                    replaced = source.replace('FILES = {', f"FILES = {{\n    '{name}': {constant},", 1)
            if replaced == source:
                unresolved.append(f'{name} (declaration not found in the driver text)')
                continue
            source = replaced
            changed.append((name, declared.name, target.name))
        print(f'{driver.name}: {len(changed)} retargeted, {len(unresolved)} unresolved, {len(ambiguous)} ambiguous')
        for name, before, after in changed:
            print(f'   {name}: {before} -> {after}')
        for name in unresolved:
            print(f'   UNRESOLVED: {name}')
        for name, holders in ambiguous:
            print(f'   AMBIGUOUS: {name} in {holders}')
        if changed and not dry_run:
            driver.write_text(source, encoding='utf-8')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--dry-run', action='store_true')
    args = parser.parse_args()
    main(args.dry_run)
