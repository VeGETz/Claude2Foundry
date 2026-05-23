#!/usr/bin/env sh
export C2F_WRAPPER=1
BIN="${C2F_BIN:-./Claude2Foundry}"
while :; do
  "$BIN" "$@"
  ec=$?
  if [ "$ec" -ne 75 ]; then
    exit "$ec"
  fi
  echo "[c2f] adapter exited 75 (restart requested); relaunching..." >&2
done
