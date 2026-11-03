// SPDX-License-Identifier: GPL-3.0-only
//go:build !windows

package routemeter

import "os"

func secureDirectory(path string) error { return os.Chmod(path, 0700) }
