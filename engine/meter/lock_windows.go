// SPDX-License-Identifier: GPL-3.0-only
package routemeter

import (
	"os"
	"path/filepath"
	"syscall"
	"unsafe"
)

func lockDirectory(directory string) (*os.File, error) {
	f, err := os.OpenFile(filepath.Join(directory, "writer.lock"), os.O_CREATE|os.O_RDWR, 0600)
	if err != nil {
		return nil, err
	}
	var overlapped syscall.Overlapped
	call := syscall.NewLazyDLL("kernel32.dll").NewProc("LockFileEx")
	ok, _, e := call.Call(f.Fd(), 3, 0, 1, 0, uintptr(unsafe.Pointer(&overlapped)))
	if ok == 0 {
		f.Close()
		return nil, e
	}
	return f, nil
}
