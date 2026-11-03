// SPDX-License-Identifier: GPL-3.0-only
// RouteMeter lifecycle metering for the separately distributed Mihomo-derived core.
package routemeter

import (
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"math"
	"path"
	"runtime"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"
)

type Route string

const (
	Direct  Route = "direct"
	Proxy   Route = "proxy"
	Unknown Route = "unknown"
)

type Bytes struct {
	Download uint64 `json:"download"`
	Upload   uint64 `json:"upload"`
}
type Usage struct {
	Direct  Bytes `json:"direct"`
	Proxy   Bytes `json:"proxy"`
	Unknown Bytes `json:"unknown"`
}
type App struct {
	ID          string `json:"id"`
	Name        string `json:"name"`
	Executable  string `json:"executable,omitempty"`
	Attribution string `json:"attribution"`
	Usage       Usage  `json:"usage"`
	Active      uint64 `json:"active"`
	Connections uint64 `json:"connections"`
}
type Snapshot struct {
	Schema           int    `json:"schema"`
	Program          string `json:"program"`
	Epoch            string `json:"epoch"`
	Instance         string `json:"instance"`
	Since            int64  `json:"since"`
	Timestamp        int64  `json:"timestamp"`
	Sequence         uint64 `json:"sequence"`
	Totals           Usage  `json:"totals"`
	Apps             []App  `json:"apps"`
	UncertainUpload  uint64 `json:"uncertainUpload"`
	Overflow         bool   `json:"overflow"`
	PersistenceError string `json:"persistenceError,omitempty"`
	Checkpoint       int64  `json:"checkpoint"`
	Measurement      string `json:"measurement"`
}
type Meter struct {
	mu               sync.Mutex
	rows             map[string]*App
	since            int64
	epoch            string
	instance         string
	sequence         uint64
	uncertain        uint64
	overflow         bool
	persistenceError string
	checkpoint       int64
	directory        string
	persistMu        sync.Mutex
}
type Handle struct {
	meter  *Meter
	row    *App
	route  Route
	closed atomic.Bool
}

func randomID() string {
	b := make([]byte, 16)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return hex.EncodeToString(b)
}
func New() *Meter {
	return &Meter{rows: make(map[string]*App), since: time.Now().UnixMilli(), epoch: randomID(), instance: randomID()}
}

// The adapter type is captured at the actual leaf, before policy groups append names.
// Node names, hostnames, interfaces, and the selected group are never routing evidence.
func Classify(kind string) Route {
	switch kind {
	case "Direct":
		return Direct
	case "Shadowsocks", "ShadowsocksR", "Snell", "Socks5", "Http", "Vmess", "Vless", "Trojan", "Hysteria", "Hysteria2", "WireGuard", "Tuic", "Ssh", "Mieru", "AnyTLS", "Sudoku", "Masque", "TrustTunnel", "ShadowQuic", "OpenVPN", "Tailscale", "ZeroTier", "EasyTier", "GostRelay":
		return Proxy
	default:
		return Unknown
	}
}
func identity(executable, osName string) (id, name, display, attribution string) {
	if len(executable) > 2048 {
		return "unknown", "Unattributed", "", "missing"
	}
	normalized := strings.ReplaceAll(strings.TrimSpace(executable), "\\", "/")
	absolute := strings.HasPrefix(normalized, "/") || (len(normalized) > 2 && normalized[1] == ':' && normalized[2] == '/')
	if !absolute {
		return "unknown", "Unattributed", "", "missing"
	}
	normalized = path.Clean(normalized)
	if osName == "darwin" {
		if i := strings.Index(normalized, ".app/"); i >= 0 {
			normalized = normalized[:i+4]
		}
	}
	display = normalized
	name = path.Base(normalized)
	if osName == "windows" {
		normalized = strings.ToLower(normalized)
		if strings.HasSuffix(strings.ToLower(name), ".exe") {
			name = name[:len(name)-4]
		}
	}
	if osName == "darwin" {
		name = strings.TrimSuffix(name, ".app")
	}
	sum := sha256.Sum256([]byte(normalized))
	return hex.EncodeToString(sum[:]), name, display, "executable"
}
func (m *Meter) Begin(executable string, route Route) *Handle {
	id, name, display, attribution := identity(executable, runtime.GOOS)
	if route != Direct && route != Proxy {
		route = Unknown
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	if _, ok := m.rows[id]; !ok && len(m.rows) >= 4096 {
		id, name, display, attribution = "unknown", "Unattributed", "", "capacity"
	}
	row := m.rows[id]
	if row == nil {
		row = &App{ID: id, Name: name, Executable: display, Attribution: attribution}
		m.rows[id] = row
	}
	row.Active = m.add(row.Active, 1)
	row.Connections = m.add(row.Connections, 1)
	m.sequence++
	return &Handle{meter: m, row: row, route: route}
}
func (m *Meter) add(a, b uint64) uint64 {
	if math.MaxUint64-a < b {
		m.overflow = true
		return math.MaxUint64
	}
	return a + b
}
func (h *Handle) Add(download, upload int64) {
	if h == nil || (download <= 0 && upload <= 0) {
		return
	}
	m := h.meter
	m.mu.Lock()
	defer m.mu.Unlock()
	target := &h.row.Usage.Unknown
	if h.route == Direct {
		target = &h.row.Usage.Direct
	} else if h.route == Proxy {
		target = &h.row.Usage.Proxy
	}
	if download > 0 {
		target.Download = m.add(target.Download, uint64(download))
	}
	if upload > 0 {
		target.Upload = m.add(target.Upload, uint64(upload))
	}
	m.sequence++
}
func (h *Handle) UncertainUpload(amount int64) {
	if h == nil || amount <= 0 {
		return
	}
	m := h.meter
	m.mu.Lock()
	defer m.mu.Unlock()
	m.uncertain = m.add(m.uncertain, uint64(amount))
	m.sequence++
}
func (h *Handle) Close() {
	if h == nil || h.closed.Swap(true) {
		return
	}
	m := h.meter
	m.mu.Lock()
	defer m.mu.Unlock()
	if h.row.Active > 0 {
		h.row.Active--
	}
	m.sequence++
}
func (m *Meter) Snapshot() Snapshot {
	m.mu.Lock()
	defer m.mu.Unlock()
	s := Snapshot{Schema: 2, Program: "RouteMeterCore", Epoch: m.epoch, Instance: m.instance, Since: m.since,
		Timestamp: time.Now().UnixMilli(), Sequence: m.sequence, Apps: make([]App, 0, len(m.rows)),
		UncertainUpload: m.uncertain, Overflow: m.overflow, PersistenceError: m.persistenceError,
		Checkpoint: m.checkpoint, Measurement: "routing-core-payload-lifecycle"}
	for _, row := range m.rows {
		s.Apps = append(s.Apps, *row)
		s.Totals.Direct.Download = m.add(s.Totals.Direct.Download, row.Usage.Direct.Download)
		s.Totals.Direct.Upload = m.add(s.Totals.Direct.Upload, row.Usage.Direct.Upload)
		s.Totals.Proxy.Download = m.add(s.Totals.Proxy.Download, row.Usage.Proxy.Download)
		s.Totals.Proxy.Upload = m.add(s.Totals.Proxy.Upload, row.Usage.Proxy.Upload)
		s.Totals.Unknown.Download = m.add(s.Totals.Unknown.Download, row.Usage.Unknown.Download)
		s.Totals.Unknown.Upload = m.add(s.Totals.Unknown.Upload, row.Usage.Unknown.Upload)
	}
	sort.Slice(s.Apps, func(i, j int) bool { return s.Apps[i].ID < s.Apps[j].ID })
	s.Overflow = m.overflow
	return s
}
