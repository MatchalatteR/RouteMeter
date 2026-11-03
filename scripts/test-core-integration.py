#!/usr/bin/env python3
"""Real core, real socket owners and closed-flow counters. All destinations are loopback."""
import argparse
import contextlib
import json
import os
from pathlib import Path
import secrets
import select
import shutil
import socket
import socketserver
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('core')
parser.add_argument('--viewer')
args = parser.parse_args()

def exact(sock, size):
    data = b''
    while len(data) < size:
        chunk = sock.recv(size-len(data))
        if not chunk: raise ConnectionError('closed socket')
        data += chunk
    return data

def decode_address(sock):
    kind = exact(sock, 1)[0]
    if kind == 1: host = socket.inet_ntoa(exact(sock, 4))
    elif kind == 3: host = exact(sock, exact(sock, 1)[0]).decode('ascii')
    elif kind == 4: host = socket.inet_ntop(socket.AF_INET6, exact(sock, 16))
    else: raise ValueError('address type')
    return host, struct.unpack('!H', exact(sock, 2))[0]

class TCPServer(socketserver.ThreadingTCPServer):
    daemon_threads = True
    allow_reuse_address = True

class UDPServer(socketserver.ThreadingUDPServer):
    daemon_threads = True

class Echo(socketserver.BaseRequestHandler):
    def handle(self):
        try:
            while True:
                data = self.request.recv(65536)
                if not data: return
                self.request.sendall(data)
        except OSError: pass

class EchoUDP(socketserver.BaseRequestHandler):
    def handle(self):
        data, sock = self.request
        sock.sendto(data, self.client_address)

class HTTPOrigin(socketserver.BaseRequestHandler):
    request_size = 0
    response = b'HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\nTestBody'
    def handle(self):
        data = b''
        while b'\r\n\r\n' not in data:
            block = self.request.recv(4096)
            if not block: return
            data += block
        HTTPOrigin.request_size = len(data)
        self.request.sendall(self.response)

allowed_ports = set()
class Proxy(socketserver.BaseRequestHandler):
    tcp_connections = 0
    udp_packets = 0
    def handle(self):
        remote = None
        try:
            self.request.settimeout(10)
            version, count = exact(self.request, 2)
            assert version == 5 and 0 in exact(self.request, count)
            self.request.sendall(b'\x05\x00')
            version, command, _ = exact(self.request, 3)
            host, port = decode_address(self.request)
            if command == 3:
                with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as udp:
                    udp.bind(('127.0.0.1', 0));udp.settimeout(0.2)
                    response = b'\x05\x00\x00\x01\x7f\x00\x00\x01'+struct.pack('!H', udp.getsockname()[1])
                    self.request.sendall(response)
                    while True:
                        ready,_,_ = select.select([udp,self.request],[],[],1)
                        if self.request in ready:
                            if not self.request.recv(1): return
                        if udp not in ready: continue
                        data, peer = udp.recvfrom(65536)
                        from io import BytesIO
                        stream = BytesIO(data)
                        assert exact(stream,3)==b'\x00\x00\x00'
                        target, target_port=decode_address(stream)
                        assert target in ('127.0.0.1','proxy-route.invalid','udp-proxy.invalid') and target_port in allowed_ports
                        payload=stream.read()
                        with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as relay:
                            relay.settimeout(3);relay.sendto(payload,('127.0.0.1',target_port));reply,_=relay.recvfrom(65536)
                        udp.sendto(b'\x00\x00\x00\x01\x7f\x00\x00\x01'+struct.pack('!H',target_port)+reply,peer)
                        Proxy.udp_packets+=1
                return
            assert command==1 and host in ('127.0.0.1','proxy-route.invalid') and port in allowed_ports
            remote=socket.create_connection(('127.0.0.1',port),timeout=3)
            self.request.sendall(b'\x05\x00\x00\x01\x7f\x00\x00\x01'+struct.pack('!H',port))
            Proxy.tcp_connections+=1
            while True:
                ready,_,_=select.select([self.request,remote],[],[],3)
                for source in ready:
                    data=source.recv(65536)
                    if not data:return
                    (remote if source is self.request else self.request).sendall(data)
        except (OSError,AssertionError,ConnectionError): pass
        finally:
            if remote:remote.close()

# BytesIO and sockets intentionally share a small read interface.
def read_source(source,n):
    return source.recv(n) if hasattr(source,'recv') else source.read(n)
def exact(source,n):
    data=b''
    while len(data)<n:
        block=read_source(source,n-len(data))
        if not block:raise ConnectionError('closed source')
        data+=block
    return data

def port():
    with socket.socket() as sock:sock.bind(('127.0.0.1',0));return sock.getsockname()[1]

with tempfile.TemporaryDirectory(prefix='routemeter-lifecycle-') as directory:
    root=Path(directory);root.chmod(0o700)
    echo=TCPServer(('127.0.0.1',0),Echo)
    origin=TCPServer(('127.0.0.1',0),HTTPOrigin)
    udp_origin=UDPServer(('127.0.0.1',0),EchoUDP)
    proxy=TCPServer(('127.0.0.1',0),Proxy)
    servers=[echo,origin,udp_origin,proxy]
    for server in servers:
        allowed_ports.add(server.server_address[1])
        threading.Thread(target=server.serve_forever,daemon=True).start()
    mixed,api=port(),port();secret=secrets.token_hex(32)
    config=root/'test.yaml'
    config.write_text(f'''
mixed-port: {mixed}
allow-lan: false
bind-address: 127.0.0.1
external-controller: 127.0.0.1:{api}
secret: "{secret}"
mode: rule
log-level: warning
find-process-mode: off
geo-auto-update: false
dns:
  enable: false
hosts:
  proxy-route.invalid: 127.0.0.1
  direct-route.invalid: 127.0.0.1
  udp-proxy.invalid: 127.0.0.1
  udp-direct.invalid: 127.0.0.1
proxies:
  - name: LocalProxy
    type: socks5
    server: 127.0.0.1
    port: {proxy.server_address[1]}
    udp: true
  - name: ProxyLookingNameButDirect
    type: direct
proxy-groups:
  - name: DirectLookingNameButProxy
    type: select
    proxies: [LocalProxy]
rules:
  - DOMAIN,proxy-route.invalid,DirectLookingNameButProxy
  - DOMAIN,udp-proxy.invalid,DirectLookingNameButProxy
  - MATCH,ProxyLookingNameButDirect
''')
    config.chmod(0o600)
    opener=urllib.request.build_opener(urllib.request.ProxyHandler({}))
    core=None
    def start_core():
        log=(root/'core.log').open('a')
        child=subprocess.Popen([str(Path(args.core).resolve()),'-d',str(root),'-f',str(config)],stdout=log,stderr=log)
        log.close()
        for _ in range(200):
            if child.poll() is not None:raise RuntimeError((root/'core.log').read_text())
            try:
                sample=usage()
                controller('GET')
                return child,sample
            except (OSError,ValueError,json.JSONDecodeError):time.sleep(0.05)
        raise RuntimeError('Core did not become ready')
    def usage():
        connection=json.loads((root/'route-meter/connection.json').read_text())
        request=urllib.request.Request(connection['endpoint']+'/v1/usage',headers={'Authorization':'Bearer '+connection['token']})
        with opener.open(request,timeout=3) as response:return json.load(response)
    def controller(method):
        request=urllib.request.Request(f'http://127.0.0.1:{api}/connections',method=method,headers={'Authorization':'Bearer '+secret})
        with opener.open(request,timeout=3) as response:
            return json.load(response) if method=='GET' else None
    def settle():
        controller('DELETE')
        for _ in range(200):
            snap=usage()
            if sum(row['active'] for row in snap['apps'])==0:return snap
            time.sleep(0.02)
        raise AssertionError('Connections did not close')
    def difference(before,after,route):
        return {key:after['totals'][route][key]-before['totals'][route][key] for key in ('download','upload')}
    def socks_connect(host,target,command=1):
        sock=socket.create_connection(('127.0.0.1',mixed),timeout=5)
        sock.sendall(b'\x05\x01\x00');assert exact(sock,2)==b'\x05\x00'
        encoded=host.encode('ascii')
        sock.sendall(b'\x05'+bytes([command])+b'\x00\x03'+bytes([len(encoded)])+encoded+struct.pack('!H',target))
        assert exact(sock,3)==b'\x05\x00\x00'
        bound=decode_address(sock)
        return sock,bound
    try:
        core,initial=start_core()
        assert initial['program']=='RouteMeterCore' and initial['schema']==2
        for route,host in [('proxy','proxy-route.invalid'),('direct','direct-route.invalid')]:
            before=settle()
            for index in range(25):
                payload=(b'payload-'+bytes([65+index]))*113
                sock,_=socks_connect(host,echo.server_address[1])
                sock.sendall(payload);assert exact(sock,len(payload))==payload;sock.close()
            after=settle()
            count=25*len(payload)
            assert difference(before,after,route)=={'download':count,'upload':count},(route,difference(before,after,route),count)
            other='direct' if route=='proxy' else 'proxy'
            assert difference(before,after,other)=={'download':0,'upload':0}
            print('PASS exact closed short-TCP payload totals:',route,flush=True)
        for route,host in [('proxy','udp-proxy.invalid'),('direct','udp-direct.invalid')]:
            before=settle()
            control,bound=socks_connect('127.0.0.1',0,command=3)
            with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as udp:
                udp.bind(('127.0.0.1',0));udp.settimeout(5)
                payload=b'udp-exact-payload'*17
                encoded=host.encode('ascii')
                udp.sendto(b'\x00\x00\x00\x03'+bytes([len(encoded)])+encoded+struct.pack('!H',udp_origin.server_address[1])+payload,
                           ('127.0.0.1',bound[1]))
                reply,_=udp.recvfrom(65536)
                from io import BytesIO
                stream=BytesIO(reply);assert exact(stream,3)==b'\x00\x00\x00';decode_address(stream);assert stream.read()==payload
            control.close();after=settle()
            assert difference(before,after,route)=={'download':len(payload),'upload':len(payload)},(route,difference(before,after,route))
            print('PASS exact UDP payload totals:',route,flush=True)
        before=settle()
        curl=shutil.which('curl.exe' if os.name=='nt' else 'curl')
        assert curl,'curl required for a second real client executable'
        subprocess.run([curl,'--proxy',f'http://127.0.0.1:{mixed}','--noproxy','','-sS','--max-time','10',
                        f'http://proxy-route.invalid:{origin.server_address[1]}/','-o',os.devnull],check=True)
        after=settle()
        assert difference(before,after,'proxy')=={'download':len(HTTPOrigin.response),'upload':HTTPOrigin.request_size}
        owners=[row for row in after['apps'] if row['attribution']=='executable' and row['usage']['proxy']['download']>0]
        assert len(owners)>=2,[(r['name'],r['attribution']) for r in after['apps']]
        assert any('curl' in row['name'].lower() for row in owners)
        assert any('python' in row['name'].lower() for row in owners)
        for route in ['direct','proxy','unknown']:
            for key in ['download','upload']:
                assert sum(row['usage'][route][key] for row in after['apps'])==after['totals'][route][key]
        assert not after['overflow'] and after['uncertainUpload']==0
        print('PASS real executable ownership, final adapter type and per-App/global conservation',flush=True)
        # Test the shipped native frontend's actual JSON/HTTP path without interactive GUI.
        if args.viewer:
            report=root/'viewer-result.json'
            subprocess.run([str(Path(args.viewer).resolve()),'--core-self-test',str(root/'route-meter/connection.json'),str(report)],check=True,timeout=15)
            assert json.loads(report.read_text())['valid'] is True
            print('PASS native viewer protocol against the real core',flush=True)
        old_epoch,old_totals=after['epoch'],after['totals']
        # Wait for a durable checkpoint; Windows TerminateProcess has no graceful SIGTERM.
        for _ in range(100):
            saved=root/'route-meter/usage.json'
            if saved.exists() and json.loads(saved.read_text())['sequence']>=after['sequence']:break
            time.sleep(0.05)
        else:raise AssertionError('Final closed-flow counters were not checkpointed')
        core.terminate();core.wait(timeout=10);core=None
        core,restored=start_core()
        assert restored['epoch']==old_epoch and restored['totals']==old_totals,(restored['totals'],old_totals)
        print('PASS cumulative state across core restart; zero closed-flow loss',flush=True)
    except BaseException:
        log_file=root/'core.log'
        if log_file.exists():print(log_file.read_text(errors='replace')[-10000:],file=sys.stderr,flush=True)
        raise
    finally:
        if core and core.poll() is None:
            core.terminate()
            try:core.wait(timeout=10)
            except subprocess.TimeoutExpired:core.kill();core.wait()
        for server in servers:server.shutdown();server.server_close()
