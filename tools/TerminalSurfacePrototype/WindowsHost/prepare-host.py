#!/usr/bin/env python3
"""Extract only the checksum-pinned Uno Win32 host and apply the research patch.
No package cache or upstream checkout is modified. Output is a fresh generated directory.
"""
import hashlib
import pathlib
import subprocess
import sys
import tarfile

COMMIT = 'da217df2312e9e39676880f9cb4b8d519cc297e0'
SHA256 = '7e84fc9f25e70e71736a8715f8da6df2396e70c878ea4a1998462d6d8b6f0cd4'
archive, destination = map(pathlib.Path, sys.argv[1:])
if hashlib.sha256(archive.read_bytes()).hexdigest() != SHA256:
    raise SystemExit('Pinned Uno archive checksum mismatch; no source was written.')
if destination.exists():
    raise SystemExit('Choose a fresh generated source directory; existing sources are never overwritten.')
prefix = f'uno-{COMMIT}/'
with tarfile.open(archive) as source:
    for item in source:
        relative = item.name.removeprefix(prefix)
        if not item.name.startswith(prefix) or not relative.startswith('src/Uno.UI.Runtime.Skia.Win32/'):
            continue
        if not item.isfile() or '..' in pathlib.PurePosixPath(relative).parts:
            continue
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(source.extractfile(item).read())
patch = pathlib.Path(__file__).with_name('uno-webview2-privacy.patch').resolve()
subprocess.run(['git', 'apply', '--no-index', '--check', str(patch)], cwd=destination, check=True)
subprocess.run(['git', 'apply', '--no-index', str(patch)], cwd=destination, check=True)
print(f'Prepared Uno {COMMIT} research host sources at {destination.resolve()}')
