#!/usr/bin/env sh
set -e
cd "$(dirname "$0")/.."

command -v dotnet >/dev/null || { echo "dotnet not on PATH"; exit 1; }
command -v pnpm   >/dev/null || { echo "pnpm not on PATH"; exit 1; }

rm -rf src/Claude2Foundry/bin src/Claude2Foundry/obj publish

for rid in win-x64 linux-x64; do
  echo "==> publish $rid"
  dotnet publish src/Claude2Foundry/Claude2Foundry.csproj \
    -c Release -r "$rid" \
    --self-contained true \
    /p:PublishSingleFile=true \
    -o "publish/$rid"
done

echo "==> done:"
ls -lh publish/*/Claude2Foundry* 2>/dev/null
