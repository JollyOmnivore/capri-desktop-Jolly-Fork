#!/bin/sh
set -eu
repo=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
node -e 'const [major, minor] = process.versions.node.split(".").map(Number); if (major < 24 || (major === 24 && minor < 9)) throw new Error("DSH needs Node 24.9 or newer")'
target="$repo/out/dsh-runtime"
mkdir -p "$target"
cp "$repo/integrations/dsh/package.json" "$target/package.json"
cp "$repo/integrations/dsh/package-lock.json" "$target/package-lock.json"
npm ci --prefix "$target" --ignore-scripts --no-fund
printf '\nDSH runtime folder: %s\nSelect More options > Experimental DSH backend in the experimental Capri build.\n' "$target"
