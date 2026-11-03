#!/bin/bash
# Runs from the private staging folder after the viewer quits. No sudo is used.
set -euo pipefail
trap '' HUP
work="$1"
target="$2"
old_pid="$3"
version="$4"
new="$work/RouteMeter.app"
backup="$work/Previous.app"
swapped=0
installed=0

launch() {
  if [ -n "${ROUTEMETER_DATA_DIR:-}" ]; then
    /usr/bin/open -n --env "ROUTEMETER_DATA_DIR=$ROUTEMETER_DATA_DIR" "$target" --args "$@"
  else
    /usr/bin/open -n "$target" --args "$@"
  fi
}

rollback() {
  result=$?
  trap - EXIT
  if [ "$swapped" = 1 ]; then
    if [ -f "$work/pid" ]; then
      new_pid="$(cat "$work/pid")"
      if [[ "$new_pid" =~ ^[0-9]+$ ]] && [ "$new_pid" -gt 1 ]; then
        kill -TERM "$new_pid" 2>/dev/null || true
        for ((i=0;i<50;i++)); do
          if ! kill -0 "$new_pid" 2>/dev/null; then break; fi
          /bin/sleep 0.1
        done
        if kill -0 "$new_pid" 2>/dev/null; then
          echo 'Replacement is still running; backup retained.' >&2
          exit 1
        fi
      fi
    fi
    if [ "$installed" = 1 ]; then /bin/mv "$target" "$work/Failed.app"; fi
    /bin/mv "$backup" "$target"
    launch --update-failed || true
    echo 'Update failed. Previous application restored.' >&2
  elif ! kill -0 "$old_pid" 2>/dev/null; then
    launch --update-failed || true
  fi
  exit "$result"
}
trap rollback EXIT

# Restrict the transaction to a sibling staging directory and the expected bundle.
[[ "$old_pid" =~ ^[0-9]+$ ]] && [ "$old_pid" -gt 1 ]
[[ "$(basename "$work")" == .RouteMeter-update-* ]]
[ "$(dirname "$work")" = "$(dirname "$target")" ]
[[ "$target" == *.app ]] && [ ! -L "$target" ] && [ ! -L "$new" ]
[ ! -e "$backup" ]
[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIdentifier' "$new/Contents/Info.plist")" = io.github.MatchalatteR.RouteMeter ]
[ "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$new/Contents/Info.plist")" = "$version" ]
/usr/bin/codesign --verify --deep --strict "$new"
for ((i=0;i<150;i++)); do
  if ! kill -0 "$old_pid" 2>/dev/null; then break; fi
  /bin/sleep 0.2
done
if kill -0 "$old_pid" 2>/dev/null; then echo 'Viewer did not quit.' >&2; exit 1; fi
/bin/mv "$target" "$backup"
swapped=1
/bin/mv "$new" "$target"
installed=1
launch --update-receipt "$work"
for ((i=0;i<150;i++)); do
  if [ -f "$work/ready" ]; then
    trap - EXIT
    echo 'Update complete. Previous application retained in Previous.app.'
    exit 0
  fi
  /bin/sleep 0.2
done
echo 'Updated viewer did not start.' >&2
exit 1
