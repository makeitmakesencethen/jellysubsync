#!/usr/bin/env python3
"""Run the characterization harness against one mutation and print what actually failed - a decisive replacement
for guessing why a mutation was reported as MISSED."""
import importlib.util
import pathlib
import sys

spec = importlib.util.spec_from_file_location('mj', 'tests/backend/mutation_job_checks.py')
mj = importlib.util.module_from_spec(spec)
sys.modules['mj'] = mj
spec.loader.exec_module(mj)

name = sys.argv[1]
old, new = mj.MUTATIONS[name]
source = mj.SERVICE.read_text(encoding='utf-8')
assert old in source, 'anchor not found'
mutated = source.replace(old, new)
print('anchor occurrences replaced:', source.count(old))
mj.SERVICE.write_text(mutated, encoding='utf-8')
try:
    env = mj.environment()
    out, err = mj.run_harness(env)
    if out is None:
        print('BUILD FAILED:', err[-600:])
    else:
        fails = [line for line in out.splitlines() if line.startswith('FAIL')]
        passes = [line for line in out.splitlines() if line.startswith('PASS')]
        print('PASS lines: %d | FAIL lines: %d' % (len(passes), len(fails)))
        for line in fails[:12]:
            print('  ', line.strip()[:150])
        p3 = [line for line in out.splitlines() if 'P3' in line]
        for line in p3[:6]:
            print('  P3:', line.strip()[:150])
finally:
    mj.SERVICE.write_text(source, encoding='utf-8')
    print('source put back:', mj.SERVICE.read_text(encoding='utf-8') == source)
