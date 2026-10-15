#!/usr/bin/env python3
"""Prepare only pinned xterm research sources in a fresh generated directory."""
import hashlib
import json
import pathlib
import subprocess
import sys
import tarfile

HERE = pathlib.Path(__file__).resolve().parent
manifest = json.loads((HERE / 'checksums.json').read_text())
archive, destination = map(pathlib.Path, sys.argv[1:])
for path, expected in [(archive, manifest['archive']), (HERE / 'combined-cell-limit.patch', manifest['patch'])]:
    if hashlib.sha256(path.read_bytes()).hexdigest() != expected:
        raise SystemExit('Pinned input checksum mismatch; no sources written.')
if destination.exists():
    raise SystemExit('Use a fresh generated directory.')
with tarfile.open(archive) as source:
    members = [m for m in source.getmembers() if m.name.startswith('package/src/') or m.name == 'package/LICENSE']
    if any(not (m.isfile() or m.isdir()) or '..' in pathlib.PurePosixPath(m.name).parts for m in members):
        raise SystemExit('Unexpected archive entry.')
    destination.mkdir(parents=True)
    source.extractall(destination, members=members)
subprocess.run(['git', 'apply', '--check', str(HERE / 'combined-cell-limit.patch')], cwd=destination / 'package', check=True)
subprocess.run(['git', 'apply', str(HERE / 'combined-cell-limit.patch')], cwd=destination / 'package', check=True)
print('Pinned research sources prepared.')
