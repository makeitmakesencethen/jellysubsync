#!/usr/bin/env python3
"""Fix a falsy-zero trap in the fixture's own verdict (a perfect result scored as a failure).

`(on.get("median_offset_s") or 99) <= 2.5` turns a legitimate 0.0 - which is exactly what a correct rescale
produces - into 99, so the fixture reported CHECK for its best possible outcome while the measurements beside it
were 1.0 and 0.0. None-checks instead of `or` defaults.
"""
import pathlib

p = pathlib.Path('tests/backend/reference_framerate.py')
t = p.read_text()
old = """    # The corrected subtitle's span must match the reference's, the written file must exist, and it must sit on
    # the reference's timeline. (An earlier version of this required the ratio to be strictly greater than 1.0,
    # which reported CHECK for a perfect 1.0 - a wrong assertion, not a wrong result.)
    on_ok = (on.get("rescale") and on.get("status") == "Completed" and bool(on.get("written"))
             and (on.get("span_ratio_vs_reference") or 0) > 0
             and abs((on.get("span_ratio_vs_reference") or 0) - 1.0) <= 0.01
             and (on.get("median_offset_s") or 99) <= 2.5)"""
new = """    # The corrected subtitle's span must match the reference's, the written file must exist, and it must sit on
    # the reference's timeline. None-checks, not `or` defaults: a perfect result is a ratio of 1.0 and an offset of
    # 0.0, and `or 99` reads that 0.0 as "missing" and fails the best possible outcome.
    on_ratio = on.get("span_ratio_vs_reference")
    on_offset = on.get("median_offset_s")
    on_ok = bool(on.get("rescale") and on.get("status") == "Completed" and on.get("written")
                 and on_ratio is not None and abs(on_ratio - 1.0) <= 0.01
                 and on_offset is not None and on_offset <= 2.5)"""
assert old in t, 'verdict block not found'
p.write_text(t.replace(old, new, 1))
print('fixture verdict: None-checks instead of falsy zero')
