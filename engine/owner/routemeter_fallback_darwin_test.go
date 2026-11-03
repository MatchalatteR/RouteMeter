// SPDX-License-Identifier: GPL-3.0-only
package process
import ("net/netip";"testing")
func TestRouteMeterFallbackTCPSourceNotServerPeer(t *testing.T){
    data:=[]byte("p123\nf3\nn127.0.0.1:12345->127.0.0.1:9000\nTST=ESTABLISHED\np456\nf4\nn127.0.0.1:9000->127.0.0.1:12345\nTST=ESTABLISHED\n")
    pid,ok:=ownerFromLsof(data,TCP,netip.MustParseAddr("127.0.0.1"),12345)
    if !ok || pid!=123{t.Fatal(pid,ok)}
}
func TestRouteMeterFallbackUDPAndAmbiguity(t *testing.T){
    data:=[]byte("p123\nf3\nn*:12345\np456\nf4\nn127.0.0.1:12345\n")
    if _,ok:=ownerFromLsof(data,UDP,netip.MustParseAddr("127.0.0.1"),12345);ok{t.Fatal("ambiguous owner accepted")}
    pid,ok:=ownerFromLsof([]byte("p123\nf3\nn*:12345\n"),UDP,netip.MustParseAddr("127.0.0.1"),12345)
    if !ok || pid!=123{t.Fatal(pid,ok)}
}
func TestRouteMeterFallbackIgnoresClosedTCPAndNormalizesIPv6(t *testing.T){
    if _,ok:=ownerFromLsof([]byte("p123\nf3\nn127.0.0.1:12345->127.0.0.1:9000\nTST=TIME_WAIT\n"),TCP,netip.MustParseAddr("127.0.0.1"),12345);ok{t.Fatal("closed TCP accepted")}
    pid,ok:=ownerFromLsof([]byte("p123\nf3\nn[2001:db8::1]:12345->[2001:db8::2]:443\nTST=ESTABLISHED\n"),TCP,netip.MustParseAddr("2001:db8::1"),12345)
    if !ok || pid!=123{t.Fatal(pid,ok)}
}
