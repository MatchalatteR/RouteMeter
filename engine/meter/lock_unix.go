// SPDX-License-Identifier: GPL-3.0-only
//go:build !windows

package routemeter

import (
	"os"
	"path/filepath"
	"syscall"
)

func lockDirectory(directory string) (*os.File, error) {
	f, err := os.OpenFile(filepath.Join(directory, "writer.lock"), os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, err
	}
	if err = syscall.Flock(int(f.Fd()), syscall.LOCK_EX|syscall.LOCK_NB); err != nil {
		f.Close()
		return nil, err
	}
	return f, nil
}
