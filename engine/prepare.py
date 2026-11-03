#!/usr/bin/env python3
"""Fetch pinned corresponding source and apply the auditable RouteMeter overlay."""
from pathlib import Path
import hashlib
import json
import os
import shutil
import subprocess
import tarfile
import urllib.request

root = Path(__file__).resolve().parent.parent
source = root / '.build/engine/mihomo'
metadata = json.loads((root/'engine/upstream.json').read_text())
patch = root/'engine/patches/lifecycle-meter.patch'
stamp = source/'.routemeter-patch'
if not source.exists():
    source.parent.mkdir(parents=True,exist_ok=True)
    archive = source.parent/'source.tar.gz'
    url = 'https://api.github.com/repos/MetaCubeX/mihomo/tarball/'+metadata['commit']
    request = urllib.request.Request(url,headers={'User-Agent':'RouteMeter-source-builder'})
    with urllib.request.urlopen(request,timeout=90) as response, archive.open('wb') as output:
        shutil.copyfileobj(response,output)
    with tarfile.open(archive) as tar:
        prefix=tar.getnames()[0].split('/')[0]
        for member in tar.getmembers():
            if member.name.startswith('/') or '..' in Path(member.name).parts:
                raise RuntimeError('Unsafe source archive path')
            if member.issym() or member.islnk():
                target=(source.parent/Path(member.name).parent/member.linkname).resolve()
                if not str(target).startswith(str(source.parent.resolve())+os.sep):
                    raise RuntimeError('Unsafe source archive link')
        tar.extractall(source.parent)
    (source.parent/prefix).rename(source)
normalized = source.parent/'lifecycle-meter.normalized.patch'
normalized.write_bytes(patch.read_bytes().replace(b'\r\n',b'\n'))
digest = hashlib.sha256(normalized.read_bytes()).hexdigest()
if not stamp.exists():
    environment = dict(os.environ,GIT_CEILING_DIRECTORIES=str(source.parent))
    environment.pop('GIT_DIR',None);environment.pop('GIT_WORK_TREE',None)
    subprocess.run(['git','-c','core.autocrlf=false','apply','--check',str(normalized)],cwd=source,env=environment,check=True)
    subprocess.run(['git','-c','core.autocrlf=false','apply',str(normalized)],cwd=source,env=environment,check=True)
    stamp.write_text(digest)
elif stamp.read_text()!=digest:
    raise RuntimeError('Patch changed: remove only .build/engine/mihomo and rebuild from the pinned source')
destination = source/'component/routemeter'
destination.mkdir(parents=True,exist_ok=True)
for file in (root/'engine/meter').glob('*.go'):
    shutil.copy2(file,destination/file.name)
print('Prepared Mihomo '+metadata['commit']+' with RouteMeter lifecycle counters')
