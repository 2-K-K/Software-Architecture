#!/usr/bin/env bash
# Зупиняє процеси, запущені run-all.sh
cd "$(dirname "$0")"
for f in pids/*.pid; do
  [ -f "$f" ] || continue
  name=$(basename "$f" .pid)
  kill "$(cat "$f")" 2>/dev/null && echo "зупинено $name" || echo "$name уже не працює"
  rm -f "$f"
done
