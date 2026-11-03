// SPDX-License-Identifier: GPL-3.0-only
package routemeter

import (
	"encoding/json"
	"errors"
	"io"
	"os"
	"path/filepath"
	"runtime"
	"time"
)

func (m *Meter) OpenStorage(directory string) error {
	if err := os.MkdirAll(directory, 0700); err != nil {
		return err
	}
	if err := secureDirectory(directory); err != nil {
		return err
	}
	m.directory = directory
	file, err := os.Open(filepath.Join(directory, "usage.json"))
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		m.persistenceError = "Saved counters are unreadable; persistence is degraded"
		return err
	}
	defer file.Close()
	var saved Snapshot
	err = json.NewDecoder(io.LimitReader(file, 32_000_000)).Decode(&saved)
	if err != nil || saved.Schema != 2 || saved.Program != "RouteMeterCore" || saved.Epoch == "" || len(saved.Apps) > 4097 {
		m.persistenceError = "Saved counters are invalid; a new epoch was created"
		_ = file.Close()
		_ = os.Rename(filepath.Join(directory, "usage.json"), filepath.Join(directory, "usage-unreadable-"+time.Now().Format("20060102T150405.000000000")+".json"))
		return errors.New("invalid metering state")
	}
	rows := make(map[string]*App)
	for _, row := range saved.Apps {
		if row.ID == "" || rows[row.ID] != nil {
			m.persistenceError = "Duplicate saved App identity; counters not restored"
			_ = file.Close()
			_ = os.Rename(filepath.Join(directory, "usage.json"), filepath.Join(directory, "usage-unreadable-"+time.Now().Format("20060102T150405.000000000")+".json"))
			return errors.New("invalid App identity")
		}
		clone := row
		clone.Active = 0
		rows[row.ID] = &clone
	}
	m.rows = rows
	m.epoch = saved.Epoch
	m.since = saved.Since
	m.sequence = saved.Sequence
	m.uncertain = saved.UncertainUpload
	m.overflow = saved.Overflow
	m.checkpoint = saved.Checkpoint
	return nil
}
func writePrivate(directory, name string, value any) error {
	data, err := json.Marshal(value)
	if err != nil {
		return err
	}
	temp, err := os.CreateTemp(directory, ".routemeter-*")
	if err != nil {
		return err
	}
	temporary := temp.Name()
	defer os.Remove(temporary)
	if err = temp.Chmod(0600); err == nil {
		_, err = temp.Write(data)
	}
	if err == nil {
		err = temp.Sync()
	}
	closeErr := temp.Close()
	if err == nil {
		err = closeErr
	}
	if err == nil {
		err = os.Rename(temporary, filepath.Join(directory, name))
	}
	if err == nil && runtime.GOOS != "windows" {
		if dir, e := os.Open(directory); e == nil {
			err = dir.Sync()
			_ = dir.Close()
		}
	}
	return err
}
func (m *Meter) Flush() error {
	m.persistMu.Lock()
	defer m.persistMu.Unlock()
	if m.directory == "" {
		return nil
	}
	state := m.Snapshot()
	state.Checkpoint = time.Now().UnixMilli()
	err := writePrivate(m.directory, "usage.json", state)
	m.mu.Lock()
	defer m.mu.Unlock()
	if err != nil {
		m.persistenceError = "Could not checkpoint counters; recent data may not survive a restart"
	}
	if err == nil {
		m.checkpoint = state.Checkpoint
	}
	return err
}
