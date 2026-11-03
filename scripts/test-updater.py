#!/usr/bin/env python3
"""Exercise the real swap/rollback helper in temporary folders with a signed fixture.

Only Launch Services is stubbed. No installed app or user data is changed.
"""
from pathlib import Path
import os
import plistlib
import shutil
import subprocess
import sys
import tempfile

app = Path(sys.argv[1]).resolve()
source = Path("Sources/RouteMeter/Resources/install-update.sh").read_text()


def version(path):
    return plistlib.loads((path / "Contents/Info.plist").read_bytes())["CFBundleShortVersionString"]


with tempfile.TemporaryDirectory(prefix="routemeter updater ") as temporary:
    root = Path(temporary)
    # Reuse one copy-on-write fixture. The original bundle is never edited.
    fixture = root / "Fixture.app"
    shutil.copytree(app, fixture, symlinks=True)
    info = fixture / "Contents/Info.plist"
    values = plistlib.loads(info.read_bytes())
    old_version = values["CFBundleShortVersionString"]
    values["CFBundleShortVersionString"] = "9.0.0"
    info.write_bytes(plistlib.dumps(values))
    subprocess.run(["codesign", "--force", "--sign", "-", str(fixture)], check=True, capture_output=True)
    for case in ["success", "custom-data-folder", "launch-failure", "no-receipt", "invalid-signature", "viewer-still-running", "swap-failure"]:
        parent = root / case
        parent.mkdir()
        target = parent / "Route Meter.app"
        shutil.copytree(app, target, symlinks=True)
        work = parent / ".RouteMeter-update-test"
        work.mkdir(mode=0o700)
        replacement = work / "RouteMeter.app"
        shutil.copytree(fixture, replacement, symlinks=True)
        if case == "invalid-signature":
            binary = replacement / "Contents/MacOS/RouteMeter"
            with binary.open("ab") as output:
                output.write(b"invalid signature")
        launch = parent / "open-fixture"
        launch.write_text("#!/bin/bash\n" + (
            'if [ "$#" = 5 ]; then touch "$5/ready"; fi\n' if case == "success" else
            'if [ "$2" = --env ] && [ "$3" = "ROUTEMETER_DATA_DIR=$ROUTEMETER_DATA_DIR" ]; then touch "$7/ready"; else exit 1; fi\n' if case == "custom-data-folder" else
            "exit 42\n" if case == "launch-failure" else "exit 0\n"))
        launch.chmod(0o700)
        # Keep production timeouts long; shorten only this test copy.
        helper = source.replace("/usr/bin/open", str(launch).replace(" ", "\\ "))
        helper = helper.replace("i<150", "i<3").replace("/bin/sleep 0.2", "/bin/sleep 0.01")
        if case == "swap-failure":
            move = parent / "move-fixture"
            move.write_text('#!/bin/bash\nif [[ "$1" == */.RouteMeter-update-test/RouteMeter.app ]]; then exit 1; fi\nexec /bin/mv "$@"\n')
            move.chmod(0o700)
            helper = helper.replace("/bin/mv", str(move).replace(" ", "\\ "))
        script = work / "install-update.sh"
        script.write_text(helper)
        # A terminated, reaped process supplies a known exited PID.
        child = subprocess.Popen(["/bin/sleep", "10" if case == "viewer-still-running" else "0"])
        if case != "viewer-still-running":
            child.wait()
        try:
            environment = dict(os.environ)
            environment.pop("ROUTEMETER_DATA_DIR", None)
            if case == "custom-data-folder":
                environment["ROUTEMETER_DATA_DIR"] = str(parent / "private data")
            result = subprocess.run(["/bin/bash", str(script), str(work), str(target), str(child.pid), "9.0.0"],
                                    capture_output=True, text=True, timeout=10, env=environment)
            success = case in ["success", "custom-data-folder"]
            assert (result.returncode == 0) == success, (case, result.stdout, result.stderr)
            assert version(target) == ("9.0.0" if success else old_version), case
            if success:
                assert version(work / "Previous.app") == old_version
            else:
                assert not (work / "Previous.app").exists(), case
            print("PASS updater " + case)
        finally:
            if child.poll() is None:
                child.terminate()
                child.wait()
