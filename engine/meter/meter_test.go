// SPDX-License-Identifier: GPL-3.0-only
package routemeter

import (
	"encoding/json"
	"io"
	"math"
	"net/http"
	"os"
	"path/filepath"
	"sync"
	"testing"
)

func TestCompletedShortFlowsRemainCounted(t *testing.T) {
	m := New()
	for i := 0; i < 1000; i++ {
		h := m.Begin("/Applications/Example.app/Contents/MacOS/Example", Proxy)
		h.Add(12, 7)
		h.Close()
		h.Close()
	}
	s := m.Snapshot()
	if s.Totals.Proxy != (Bytes{12000, 7000}) || s.Apps[0].Active != 0 || s.Apps[0].Connections != 1000 {
		t.Fatal(s)
	}
}
func TestRoutesAndMissingIdentity(t *testing.T) {
	m := New()
	m.Begin("/bin/first", Direct).Add(11, 1)
	m.Begin("/bin/first", Proxy).Add(20, 2)
	m.Begin("", Proxy).Add(30, 3)
	m.Begin("/bin/second", Unknown).Add(40, 4)
	s := m.Snapshot()
	if s.Totals.Direct.Download != 11 || s.Totals.Proxy.Download != 50 || s.Totals.Unknown.Download != 40 {
		t.Fatal(s)
	}
	found := false
	for _, a := range s.Apps {
		if a.ID == "unknown" {
			found = true
			if a.Usage.Proxy.Download != 30 || a.Attribution != "missing" {
				t.Fatal(a)
			}
		}
	}
	if !found {
		t.Fatal("missing unknown bucket")
	}
}
func TestConcurrentTCPAndUDPAccounting(t *testing.T) {
	m := New()
	h := m.Begin("/bin/shared", Proxy)
	var wg sync.WaitGroup
	for g := 0; g < 8; g++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			for i := 0; i < 10000; i++ {
				h.Add(3, 2)
			}
		}()
	}
	wg.Wait()
	h.Close()
	if s := m.Snapshot(); s.Totals.Proxy != (Bytes{240000, 160000}) {
		t.Fatal(s.Totals)
	}
}
func TestIdentityMacAndWindows(t *testing.T) {
	a, name, _, status := identity("/Applications/Example.app/Contents/Frameworks/Helper.app/Contents/MacOS/helper", "darwin")
	b, _, _, _ := identity("/Applications/Example.app/Contents/MacOS/Example", "darwin")
	if a != b || name != "Example" || status != "executable" {
		t.Fatal(a, b, name, status)
	}
	a, _, _, _ = identity(`C:\Program Files\Example\Example.exe`, "windows")
	b, name, _, _ = identity(`c:/program files/example/example.exe`, "windows")
	if a != b || name != "example" {
		t.Fatal(a, b, name)
	}
	a, _, _, status = identity("unknown-name", "windows")
	if a != "unknown" || status != "missing" {
		t.Fatal(a, status)
	}
}
func TestAdapterTypeNotDisplayName(t *testing.T) {
	for _, kind := range []string{"AnyTLS", "Socks5", "Vless", "Http", "WireGuard"} {
		if Classify(kind) != Proxy {
			t.Fatal(kind)
		}
	}
	if Classify("Direct") != Direct || Classify("Selector") != Unknown || Classify("not-supported") != Unknown {
		t.Fatal("route classification")
	}
}
func TestPersistenceAndIndependentWriters(t *testing.T) {
	dir := t.TempDir()
	first, err := Start(dir)
	if err != nil {
		t.Fatal(err)
	}
	first.Meter.Begin("/bin/app", Proxy).Add(80, 20)
	epoch := first.Meter.Snapshot().Epoch
	if duplicate, e := Start(dir); e == nil {
		duplicate.Close()
		t.Fatal("second writer accepted")
	}
	first.Close()
	second, err := Start(dir)
	if err != nil {
		t.Fatal(err)
	}
	defer second.Close()
	state := second.Meter.Snapshot()
	if state.Epoch != epoch || state.Totals.Proxy != (Bytes{80, 20}) || state.Apps[0].Active != 0 {
		t.Fatal(state)
	}
}
func TestCorruptStateIsPreservedAndFlagged(t *testing.T) {
	dir := t.TempDir()
	if err := os.WriteFile(filepath.Join(dir, "usage.json"), []byte("broken"), 0600); err != nil {
		t.Fatal(err)
	}
	service, err := Start(dir)
	if err != nil {
		t.Fatal(err)
	}
	defer service.Close()
	if service.Meter.Snapshot().PersistenceError == "" {
		t.Fatal("corruption hidden")
	}
	matches, _ := filepath.Glob(filepath.Join(dir, "usage-unreadable-*.json"))
	if len(matches) != 1 {
		t.Fatal(matches)
	}
}
func TestReadOnlyHTTPAndCredentials(t *testing.T) {
	s, err := Start(t.TempDir())
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	client := &http.Client{Transport: &http.Transport{Proxy: nil}}
	for _, test := range []struct {
		method, token, origin string
		want                  int
	}{
		{"GET", "", "", 401}, {"POST", s.Connection.Token, "", 403},
		{"GET", s.Connection.Token, "https://example.invalid", 403}, {"GET", s.Connection.Token, "", 200},
	} {
		r, _ := http.NewRequest(test.method, s.Connection.Endpoint+"/v1/usage", nil)
		r.Header.Set("Authorization", "Bearer "+test.token)
		r.Header.Set("Origin", test.origin)
		response, e := client.Do(r)
		if e != nil {
			t.Fatal(e)
		}
		body, _ := io.ReadAll(response.Body)
		response.Body.Close()
		if response.StatusCode != test.want {
			t.Fatalf("%s: %d", test.method, response.StatusCode)
		}
		if test.want == 200 {
			var snap Snapshot
			if json.Unmarshal(body, &snap) != nil || snap.Schema != 2 {
				t.Fatal(string(body))
			}
		}
	}
}
func TestUncertainWritesAndOverflowAreExplicit(t *testing.T) {
	m := New()
	h := m.Begin("/bin/app", Proxy)
	h.UncertainUpload(7)
	m.mu.Lock()
	h.row.Usage.Proxy.Download = math.MaxUint64 - 1
	m.mu.Unlock()
	h.Add(10, -1)
	s := m.Snapshot()
	if s.UncertainUpload != 7 || !s.Overflow || s.Totals.Proxy.Download != math.MaxUint64 || s.Totals.Proxy.Upload != 0 {
		t.Fatal(s)
	}
}

func TestBackendReadinessBeforeFirstTraffic(t *testing.T) {
	s, err := start(t.TempDir(), false)
	if err != nil {
		t.Fatal(err)
	}
	defer s.Close()
	client := &http.Client{Transport: &http.Transport{Proxy: nil}}
	check := func(want int) {
		r, _ := http.NewRequest("GET", s.Connection.Endpoint+"/v1/usage", nil)
		r.Header.Set("Authorization", "Bearer "+s.Connection.Token)
		response, e := client.Do(r)
		if e != nil {
			t.Fatal(e)
		}
		response.Body.Close()
		if response.StatusCode != want {
			t.Fatal(response.StatusCode, want)
		}
	}
	check(503)
	s.ready.Store(true)
	check(200)
}
