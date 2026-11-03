#!/usr/bin/env python3
"""Publish the exact, patched corresponding core source next to binary releases."""
from pathlib import Path
import hashlib
import tarfile

root=Path(__file__).resolve().parent.parent
version=(root/'VERSION').read_text().strip()
output=root/'dist'/('RouteMeter-Core-'+version+'-source.tar.gz')
source=root/'.build/engine/mihomo'
assert (source/'component/routemeter/meter.go').exists()
with tarfile.open(output,'w:gz') as tar:
    for path in sorted(source.rglob('*')):
        relative=path.relative_to(source)
        if '.git' in relative.parts:continue
        tar.add(path,arcname='RouteMeter-Core-source/'+str(relative),recursive=False)
    tar.add(root/'engine/upstream.json',arcname='RouteMeter-Core-source/ROUTEMETER-UPSTREAM.json')
    tar.add(root/'docs/CORE-SOURCE.md',arcname='RouteMeter-Core-source/ROUTEMETER-BUILD.md')
digest=hashlib.sha256(output.read_bytes()).hexdigest()
Path(str(output)+'.sha256').write_text(digest+'  '+output.name+'\n')
print('Created complete patched core source archive')
