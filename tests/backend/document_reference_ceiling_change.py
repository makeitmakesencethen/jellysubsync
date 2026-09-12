#!/usr/bin/env python3
"""Update the check and the documented contract for the reference ceiling.

The ceiling used to be documented as a refusal, and a check pinned that wording. It is now the trigger for the audio
fallback: a subtitle reference that demands a shift past the limit is dropped as a ruler and the subtitle is aligned
against the audio. The ceiling still governs what a subtitle reference is trusted for - it just no longer decides
that the file cannot be synced.
"""
import pathlib
import re

checks = pathlib.Path('tests/run_checks.py')
c = checks.read_text()
old = """    report('a reference-derived shift past the limit is refused, not written',
           'refusing a reference-derived shift' in service
           and 'MaxSubtitleReferenceOffsetSeconds' in service
           and 'worth checking, a shift this size' in service)"""
new = """    report('a reference-derived shift past the limit drops the reference and uses the audio',
           'MaxSubtitleReferenceOffsetSeconds' in service
           and 'it demanded {fromReference.ShiftMs} ms' in service
           and 'ReferenceStore.Discard(videoPath, referenceSpec);' in service
           and 'worth checking, a shift this size' in service
           and 'refusing a reference-derived shift' not in service)"""
assert old in c, 'check not found'
checks.write_text(c.replace(old, new, 1))
print('check updated to the new behaviour')

# AGENTS.md / knowledge docs: the documented contract
for doc in ('AGENTS.md', 'knowledge/FIX_PLAN.md', 'knowledge/backend-test-report-2026-09-11.md'):
    p = pathlib.Path(doc)
    if not p.exists():
        continue
    t = p.read_text()
    hits = [l for l in t.splitlines() if 'MaxSubtitleReferenceOffsetSeconds' in l]
    if not hits:
        continue
    addition = ("\n- `MaxSubtitleReferenceOffsetSeconds` bounds what a *subtitle* reference is trusted for. A shift "
                "past it means that track is not the same cut: the reference is discarded as a ruler and the subtitle "
                "is aligned against the audio instead (one audio analysis per file, cached), rather than the file "
                "being refused. Changed in 2.0.17; before that the ceiling ended the job.\n")
    if 'discarded as a ruler' in t:
        print('%-46s already documents the new contract' % doc)
        continue
    if not t.endswith("\n"):
        t += "\n"
    p.write_text(t + addition)
    print('%-46s documented' % doc)
