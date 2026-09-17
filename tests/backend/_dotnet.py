#!/usr/bin/env python3
"""Where the .NET SDK is, for the probe scripts that shell out to `dotnet`.

The probes used to hard-code one machine's SDK path (`/opt/data/.dotnet/dotnet`) as their fallback, which
made the two suite checks that run them fail on CI with `FileNotFoundError: '/opt/data/.dotnet/dotnet'` -
a red tests workflow on every push, for a reason that has nothing to do with the code under test. An
environment that resolves dotnet the way `run_checks.py` does (DOTNET, then PATH, then the usual roots)
works everywhere the suite works.

`DOTNET_BIN` is still honoured first: it is what the probes' own callers documented.
"""

import os
import pathlib
import shutil

ROOTS = ['DOTNET_ROOT', 'HOME/.dotnet', '/usr/share/dotnet', '/usr/local/share/dotnet', '/opt/dotnet',
         '/opt/data/.dotnet']


def find_dotnet() -> str:
    for name in ('DOTNET_BIN', 'DOTNET'):
        candidate = os.environ.get(name)
        if candidate and os.path.isfile(candidate):
            return candidate
    on_path = shutil.which('dotnet')
    if on_path:
        return on_path
    for root in ROOTS:
        if root == 'HOME/.dotnet':
            resolved = str(pathlib.Path.home() / '.dotnet')
        elif root == 'DOTNET_ROOT':
            resolved = os.environ.get('DOTNET_ROOT') or ''
        else:
            resolved = root
        if resolved and os.path.isfile(os.path.join(resolved, 'dotnet')):
            return os.path.join(resolved, 'dotnet')
    raise SystemExit('dotnet not found: install the .NET SDK, put it on PATH, or set DOTNET=/path/to/dotnet')
