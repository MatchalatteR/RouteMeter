#!/usr/bin/env python3
"""Check publishable sources, not local build output or user application data."""
from pathlib import Path
import re
import subprocess

paths = subprocess.check_output(['git','ls-files','-z']).decode().split('\0')
assert any(paths), 'Run this check after staging the public project.'
patterns = [
    rb'/Users/[^/\s]+/',
    rb'-----BEGIN (?:OPENSSH |RSA |EC )?PRIVATE KEY-----',
    rb'gh[pousr]_[A-Za-z0-9]{20,}',
    rb'(?i)(?:sk-|api[_-]?key["\s:=]+)[A-Za-z0-9]{24,}',
    rb'\b[a-f0-9]{64}\b',
]
failures = []
for name in filter(None,paths):
    path = Path(name)
    if not path.is_file(): continue
    # Literal scanner patterns are documentation of checks, not local values.
    if name == 'scripts/check-public.py': continue
    data = path.read_bytes()
    for pattern in patterns:
        if re.search(pattern,data): failures.append(name); break
assert not failures, 'Potential private data in: ' + ', '.join(failures)
print(f'PASS public-source checks ({sum(bool(x) for x in paths)} tracked files)')
