// SPDX-License-Identifier: GPL-3.0-only
package routemeter

import (
	"context"
	"os/exec"
	"os/user"
	"time"
)

func secureDirectory(path string) error {
	current, err := user.Current()
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	// os/user returns the user's SID on Windows. No shell interpretation or user-name parsing.
	return exec.CommandContext(ctx, "icacls.exe", path, "/inheritance:r", "/grant:r",
		"*"+current.Uid+":(OI)(CI)F", "*S-1-5-18:(OI)(CI)F", "*S-1-5-32-544:(OI)(CI)F").Run()
}
