// SPDX-License-Identifier: GPL-3.0-only
package process

import (
    "context"
    "bytes"
    "net"
    "net/netip"
    "os"
    "os/exec"
    "strconv"
    "strings"
    "time"
)

// macOS may restrict a signed third-party process's raw PCB enumeration.
// Use the supported, read-only system utility as the current user, without
// changing signatures, permissions, SIP, entitlements, or security settings.
func findProcessName(network string, ip netip.Addr, port int) (uint32,string,error) {
    if os.Getenv("ROUTEMETER_FORCE_OWNER_FALLBACK")!="1" {
        uid,path,err:=findProcessNameNative(network,ip,port)
        if err==nil && path!="" {return uid,path,nil}
    }
    if port<1 || port>65535 || (network!=TCP && network!=UDP) {return 0,"",ErrInvalidNetwork}
    family:="6";if ip.Unmap().Is4() {family="4"}
    selector:="-i"+family+strings.ToUpper(network)+":"+strconv.Itoa(port)
    ctx,cancel:=context.WithTimeout(context.Background(),2*time.Second);defer cancel()
    cmd:=exec.CommandContext(ctx,"/usr/sbin/lsof","-nP",selector,"-F","pfnT")
    output:=&ownerOutput{}
    cmd.Stdout=output
    // lsof exits 1 for no match; no diagnostics or destination values are logged.
    _=cmd.Run()
    if ctx.Err()!=nil || output.overflow {return 0,"",ErrNotFound}
    pid,ok:=ownerFromLsof(output.Bytes(),network,ip,port)
    if !ok {return 0,"",ErrNotFound}
    path,err:=getExecPathFromPID(pid)
    return 0,path,err
}
type ownerOutput struct {bytes.Buffer;overflow bool}
func (b *ownerOutput) Write(p []byte)(int,error){
    if b.Len()+len(p)>2_000_000 {b.overflow=true;return len(p),nil}
    _,_ = b.Buffer.Write(p);return len(p),nil
}
func ownerFromLsof(data []byte,network string,ip netip.Addr,port int)(uint32,bool){
    var pid,found uint32
    var matched,established,record,hasFound,ambiguous bool
    flush:=func(){
        if record && matched && pid>0 && (network==UDP || established) {
            if hasFound && found!=pid {ambiguous=true};found=pid;hasFound=true
        }
        matched=false;established=false;record=false
    }
    for _,line:=range strings.Split(string(data),"\n"){
        if len(line)==0 {continue}
        switch line[0]{
        case 'p':flush();value,err:=strconv.ParseUint(line[1:],10,32);if err!=nil{pid=0}else{pid=uint32(value)}
        case 'f':flush();record=true
        case 'n':
            local:=strings.SplitN(line[1:],"->",2)[0]
            host,service,err:=net.SplitHostPort(local);if err!=nil{continue}
            p,err:=strconv.Atoi(service);if err!=nil || p!=port{continue}
            if network==UDP && host=="*" {matched=true;continue}
            address,err:=netip.ParseAddr(host)
            if err==nil && address.WithZone("").Unmap()==ip.WithZone("").Unmap(){matched=true}
        case 'T':if line=="TST=ESTABLISHED" {established=true}
        }
    }
    flush();return found,hasFound && !ambiguous
}
