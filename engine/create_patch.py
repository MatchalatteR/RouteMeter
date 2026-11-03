#!/usr/bin/env python3
"""Maintainer tool: generate the small, pinned Mihomo integration patch."""
from pathlib import Path
import difflib
import tarfile

root = Path(__file__).resolve().parent.parent
source = root / '.build/engine/mihomo'
archive = root / '.build/engine/mihomo-source.tar.gz'
if not archive.exists(): archive = root / '.build/engine/source.tar.gz'
files = ['constant/adapters.go', 'adapter/outbound/base.go', 'tunnel/statistic/tracker.go', 'main.go', 'tunnel/tunnel.go', 'component/process/process_darwin.go', 'component/process/process_windows.go']
with tarfile.open(archive) as tar:
    prefix = tar.getnames()[0].split('/')[0]
    original = {name: tar.extractfile(prefix + '/' + name).read().decode() for name in files}
edited = dict(original)

s = edited['constant/adapters.go']
s = s.replace('type Connection interface {', 'type Connection interface {\n\t// RouteMeter captures the actual leaf adapter type, not its user-chosen name.\n\tRouteMeterOutboundType() AdapterType')
edited['constant/adapters.go'] = s
s = edited['adapter/outbound/base.go']
for name in ['conn', 'packetConn']:
    start=s.index('type '+name+' struct {')
    end=s.index('\n}',start)
    # Preserve offsets of the upstream embedded transport and existing fields.
    s=s[:end]+'\n\trouteMeterLeafType C.AdapterType\n\trouteMeterLeafSet bool'+s[end:]
    signature = 'func (c *'+name+') AppendToChains(a C.ProxyAdapter) {'
    s = s.replace(signature, signature+'\n\tif !c.routeMeterLeafSet { c.routeMeterLeafType = a.Type(); c.routeMeterLeafSet = true }')
    s += '\nfunc (c *'+name+') RouteMeterOutboundType() C.AdapterType { if !c.routeMeterLeafSet { return C.AdapterType(-1) }; return c.routeMeterLeafType }\n'
s = s.replace('&conn{N.NewExtendedConn(c), nil, nil, a.Addr()}', '&conn{ExtendedConn: N.NewExtendedConn(c), adapterAddr: a.Addr()}')
s = s.replace('&packetConn{epc, nil, nil, a.Name(), utils.NewUUIDV4().String(), a.Addr(), a.ResolveUDP}', '&packetConn{EnhancePacketConn: epc, adapterName: a.Name(), connID: utils.NewUUIDV4().String(), adapterAddr: a.Addr(), resolveUDP: a.ResolveUDP}')
edited['adapter/outbound/base.go'] = s

s = edited['tunnel/statistic/tracker.go']
s = s.replace('"time"', '"time"\n\t"github.com/metacubex/mihomo/component/routemeter"')
s = s.replace('pushToManager bool `json:"-"`', 'pushToManager bool `json:"-"`\n\tusage *routemeter.Handle `json:"-"`')
for receiver in ['tt', 'ut']:
    s = s.replace(receiver+'.DownloadTotal.Add(download)', receiver+'.DownloadTotal.Add(download)\n\t'+receiver+'.usage.Add(download, 0)')
    s = s.replace(receiver+'.UploadTotal.Add(upload)', receiver+'.UploadTotal.Add(upload)\n\t'+receiver+'.usage.Add(0, upload)')
    s = s.replace(receiver+'.manager.Leave('+receiver+')', receiver+'.manager.Leave('+receiver+')\n\t'+receiver+'.usage.Close()')
# A buffered writer cannot report a partial byte count on error. Keep it explicit.
s = s.replace('tt.UploadTotal.Add(upload)\n\ttt.usage.Add(0, upload)\n\treturn\n}',
              'tt.UploadTotal.Add(upload)\n\tif err == nil { tt.usage.Add(0, upload) } else { tt.usage.UncertainUpload(upload) }\n\treturn\n}')
for receiver, function in [('t','NewTCPTracker'), ('ut','NewUDPTracker')]:
    start = s.index('func '+function+'(')
    position = s.index('\n\tif pushToManager {', start)
    setup = '\n\tif pushToManager {\n\t\t'+receiver+'.usage = routemeter.Default(C.Path.HomeDir()).BeginSafe(metadata.ProcessPath, conn.RouteMeterOutboundType().String())\n\t\t'+receiver+'.usage.Add(downloadTotal, uploadTotal)\n\t}\n'
    s = s[:position]+setup+s[position:]
edited['tunnel/statistic/tracker.go'] = s
s = edited['main.go'].replace('"context"', '"context"\n\t"github.com/metacubex/mihomo/component/routemeter"')
needle = '\tif err := hub.Parse(configBytes, options...); err != nil {'
s = s.replace(needle, '\tif err := routemeter.Initialize(C.Path.HomeDir()); err != nil {\n\t\tlog.Fatalln("RouteMeter metering initialization failed: %s", err.Error())\n\t}\n\tdefer routemeter.Shutdown()\n\n'+needle, 1)
edited['main.go'] = s
s = edited['tunnel/tunnel.go'].replace('findProcessMode.Store(mode)', '// RouteMeter Core always attempts source ownership; failures stay Unattributed.\n\tfindProcessMode.Store(process.FindProcessAlways)')
edited['tunnel/tunnel.go'] = s

# Ambiguous source-port ownership must not silently pick the first process.
s = edited['component/process/process_darwin.go']
s = s.replace('var fallbackUDPProcess string', 'var fallbackPID, matchedPID uint32\n\tvar fallbackFound, matchedFound bool\n\tvar fallbackAmbiguous, matchedAmbiguous bool')
s = s.replace('pp, err := getExecPathFromPID(pid)\n\t\t\treturn 0, pp, err', 'if matchedFound && matchedPID != pid { matchedAmbiguous = true }; matchedPID = pid; matchedFound = true\n\t\t\tcontinue')
s = s.replace('fallbackUDPProcess, _ = getExecPathFromPID(readNativeUint32(buf[so+68 : so+72]))', 'pid := readNativeUint32(buf[so+68 : so+72])\n\t\t\tif fallbackFound && fallbackPID != pid { fallbackAmbiguous = true }; fallbackPID = pid; fallbackFound = true')
s = s.replace('if network == UDP && fallbackUDPProcess != "" {\n\t\treturn 0, fallbackUDPProcess, nil\n\t}', 'if matchedFound { if matchedAmbiguous { return 0, "", ErrNotFound }; pp, err := getExecPathFromPID(matchedPID); return 0, pp, err }\n\tif network == UDP && fallbackFound && !fallbackAmbiguous { pp, err := getExecPathFromPID(fallbackPID); return 0, pp, err }')
edited['component/process/process_darwin.go'] = s
s = edited['component/process/process_darwin.go'].replace('func findProcessName(network string, ip netip.Addr, port int)', 'func findProcessNameNative(network string, ip netip.Addr, port int)')
edited['component/process/process_darwin.go'] = s
s = edited['component/process/process_windows.go']
s = s.replace('itemSize := s.itemSize\n\tfor i := 0;', 'itemSize := s.itemSize\n\tvar match uint32\n\tvar found bool\n\tfor i := 0;')
s = s.replace('pid := readNativeUint32(row[s.pid : s.pid+4])\n\t\treturn pid, nil', 'pid := readNativeUint32(row[s.pid : s.pid+4])\n\t\tif found && match != pid { return 0, ErrNotFound }; match = pid; found = true')
s = s.replace('\treturn 0, ErrNotFound\n}\n\nfunc newSearcher', '\tif found { return match, nil }; return 0, ErrNotFound\n}\n\nfunc newSearcher')
edited['component/process/process_windows.go'] = s

patch = []
for name in files:
    assert edited[name] != original[name], name
    patch.extend(difflib.unified_diff(original[name].splitlines(True), edited[name].splitlines(True), fromfile='a/'+name, tofile='b/'+name))
    (source/name).write_text(edited[name])
(root/'engine/patches/lifecycle-meter.patch').write_text(''.join(patch))
print('Generated pinned lifecycle and ownership patch')
