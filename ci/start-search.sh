#!/bin/sh
set -eu
repo_root=$(CDPATH= cd -- "$(dirname -- "$0")/.." && pwd)
port=${1:-17888}
case "$port" in ''|*[!0-9]*) echo "Choose a numeric localhost port." >&2; exit 1 ;; esac
if [ "$port" -lt 1 ] || [ "$port" -gt 65535 ]; then
    echo "Choose a port from 1 to 65535." >&2
    exit 1
fi
config_dir="$repo_root/.local/searxng"
mkdir -p "$config_dir"
chmod 700 "$repo_root/.local" "$config_dir"
python3 - "$config_dir/settings.yml" <<'PY'
from pathlib import Path
import secrets, sys
path = Path(sys.argv[1])
if not path.exists():
    path.write_text("use_default_settings: true\nserver:\n  secret_key: '" + secrets.token_hex(32) + "'\n  limiter: false\n  image_proxy: false\nsearch:\n  formats:\n    - html\n    - json\n")
    path.chmod(0o600)
PY
docker run -d --name capri-searxng --label io.capri.search=true \
    --restart unless-stopped --publish "127.0.0.1:$port:8080" \
    --volume "$config_dir:/etc/searxng:rw" \
    docker.io/searxng/searxng@sha256:9f59681775ba8d99744796e161d46dfecc771b972572edddb31d92dcfafc12b7
python3 - "$repo_root/.local/search.json" "$port" <<'PY'
from pathlib import Path
import json, sys
path = Path(sys.argv[1])
if not path.exists():
    path.write_text(json.dumps({"BaseUrl": "http://127.0.0.1:" + sys.argv[2]}, indent=2) + "\n")
    path.chmod(0o600)
PY
echo "SearXNG is starting at http://127.0.0.1:$port."
echo "Copy .local/search.json to Capri's application-data folder, then restart Capri."
