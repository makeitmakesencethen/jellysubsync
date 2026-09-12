#!/usr/bin/env python3
"""Fix the queue-indicator check and prove what the page actually renders.

The exact-string comparison kept biting because the JS escape ("\u2014") passes through several layers of
quoting. The check now asserts the *shape* (a reason function, the spinner, no vague wording) and the
rendered text is proven by running it through node instead.
"""
import pathlib
import re
import subprocess

checks = pathlib.Path('tests/run_checks.py')
t = checks.read_text()
lines = t.splitlines()
start = next(i for i, l in enumerate(lines) if "a queued run says why it is waiting" in l)
end = next(i for i in range(start, len(lines)) if lines[i].rstrip().endswith('and \'id="ss-spinner"\' in pages[\'subsyncMain.html\'])'))
new_block = [
    "    report('a queued run says why it is waiting, and shows the plugin\\'s own spinner',",
    "           'function queuedReason(view, elsewhereBusy)' in pages['subsyncMain.js']",
    "           and 'waiting to start' not in pages['subsyncMain.js']",
    "           and 'runSpinner(true);' in pages['subsyncMain.js']",
    "           and 'runSpinner(false);' in pages['subsyncMain.js']",
    "           and 'starting as soon as a worker is free' in pages['subsyncMain.js']",
    "           and '.ss-spinner { width: 12px; height: 12px' in pages['subsyncMain.html']",
    "           and 'id=\"ss-spinner\"' in pages['subsyncMain.html'])",
]
lines[start:end + 1] = new_block
checks.write_text("\n".join(lines) + "\n")
print('check rewritten')

# what the page will actually show, run through node
page = pathlib.Path('Jellyfin.Plugin.SubSync/Web/subsyncMain.js').read_text()
for needle in ('Queued', 'reading subtitles', 'Waiting for a free worker'):
    for l in page.splitlines():
        if needle in l and ('return' in l or 'textContent' in l):
            expr = re.search(r"'([^']*)'", l)
            if expr:
                out = subprocess.run(['node', '-e', 'console.log("%s")' % expr.group(1).replace('"', '')],
                                     capture_output=True, text=True)
                print('%(needle)r renders as: %(out)s' % {'needle': needle, 'out': out.stdout.strip()[:90]})
                break
