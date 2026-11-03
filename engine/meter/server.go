// SPDX-License-Identifier: GPL-3.0-only
package routemeter

import (
	"crypto/rand"
	"crypto/subtle"
	"encoding/hex"
	"encoding/json"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

type Connection struct {
	Schema   int    `json:"schema"`
	Endpoint string `json:"endpoint"`
	Token    string `json:"token"`
}
type Service struct {
	Meter      *Meter
	Server     *http.Server
	Connection Connection
	stop       chan struct{}
	done       chan struct{}
	once       sync.Once
	lock       *os.File
}

func Start(directory string) (*Service, error) {
	if err := os.MkdirAll(directory, 0700); err != nil {
		return nil, err
	}
	if err := secureDirectory(directory); err != nil {
		return nil, err
	}
	lock, err := lockDirectory(directory)
	if err != nil {
		return nil, err
	}
	keepLock := false
	defer func() {
		if !keepLock {
			_ = lock.Close()
		}
	}()
	meter := New()
	// An unreadable state is explicit in the snapshot, rather than silently treated as restored.
	if err = meter.OpenStorage(directory); err != nil && meter.directory == "" {
		return nil, err
	}
	listener, err := net.Listen("tcp4", "127.0.0.1:0")
	if err != nil {
		return nil, err
	}
	key := make([]byte, 32)
	if _, err = rand.Read(key); err != nil {
		listener.Close()
		return nil, err
	}
	service := &Service{Meter: meter, Connection: Connection{1, "http://" + listener.Addr().String(), hex.EncodeToString(key)}, stop: make(chan struct{}), done: make(chan struct{}), lock: lock}
	mux := http.NewServeMux()
	mux.HandleFunc("/v1/usage", func(w http.ResponseWriter, r *http.Request) {
		if r.Method != http.MethodGet || r.Header.Get("Origin") != "" {
			w.WriteHeader(http.StatusForbidden)
			return
		}
		host, _, e := net.SplitHostPort(r.RemoteAddr)
		if e != nil || !net.ParseIP(host).IsLoopback() {
			w.WriteHeader(http.StatusForbidden)
			return
		}
		supplied := strings.TrimPrefix(r.Header.Get("Authorization"), "Bearer ")
		if subtle.ConstantTimeCompare([]byte(supplied), []byte(service.Connection.Token)) != 1 {
			w.WriteHeader(http.StatusUnauthorized)
			return
		}
		w.Header().Set("Content-Type", "application/json")
		w.Header().Set("Cache-Control", "no-store")
		_ = json.NewEncoder(w).Encode(meter.Snapshot())
	})
	service.Server = &http.Server{Handler: mux, ReadHeaderTimeout: 2 * time.Second, ReadTimeout: 3 * time.Second,
		WriteTimeout: 3 * time.Second, IdleTimeout: 5 * time.Second, MaxHeaderBytes: 4096}
	if err = writePrivate(directory, "connection.json", service.Connection); err != nil {
		listener.Close()
		return nil, err
	}
	go service.Server.Serve(listener)
	go func() {
		defer close(service.done)
		ticker := time.NewTicker(time.Second)
		defer ticker.Stop()
		for {
			select {
			case <-ticker.C:
				_ = meter.Flush()
			case <-service.stop:
				return
			}
		}
	}()
	keepLock = true
	return service, nil
}
func (s *Service) Close() {
	s.once.Do(func() { close(s.stop); <-s.done; _ = s.Server.Close(); _ = s.Meter.Flush(); _ = s.lock.Close() })
}

var defaultOnce sync.Once
var defaultService *Service
var defaultError error

func Initialize(home string) error {
	defaultOnce.Do(func() { defaultService, defaultError = Start(filepath.Join(home, "route-meter")) })
	return defaultError
}
func Default(home string) *Meter {
	if err := Initialize(home); err != nil {
		return nil
	}
	return defaultService.Meter
}
func (m *Meter) BeginSafe(executable, kind string) *Handle {
	if m == nil {
		return nil
	}
	return m.Begin(executable, Classify(kind))
}
func Shutdown() {
	if defaultService != nil {
		defaultService.Close()
	}
}
