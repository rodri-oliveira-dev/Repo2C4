#!/usr/bin/env bash
# Verify local distribution without publishing a tag, release or NuGet package.
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"

packages_dir="${1:-$repo_root/artifacts/distribution}"
version="${2:-1.0.0}"
if [[ ! "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?$ ]]; then
  echo "Invalid SemVer package version." >&2
  exit 2
fi
case "$packages_dir" in
  /*) ;;
  *) packages_dir="$repo_root/$packages_dir" ;;
esac
mkdir -p "$packages_dir"
packages_dir="$(cd "$packages_dir" && pwd)"

resolved="$(dotnet msbuild src/Repo2C4.Cli/Repo2C4.Cli.csproj -nologo -getProperty:Version | tr -d '\r' | tail -n 1)"
if [[ "$resolved" != "$version" ]]; then
  echo "Package version differs from the single MSBuild Version source." >&2
  exit 2
fi

python3 - "$version" <<'PY'
import json
import pathlib
import sys

version = sys.argv[1]
metadata = json.loads(pathlib.Path("server.json").read_text(encoding="utf-8"))

assert metadata["name"] == "io.github.rodri-oliveira-dev/repo2c4-mcp"
assert metadata["version"] == version
packages = metadata["packages"]
assert len(packages) == 1
package = packages[0]
assert package["registryType"] == "nuget"
assert package["registryBaseUrl"] == "https://api.nuget.org/v3/index.json"
assert package["identifier"] == "Repo2C4.Mcp"
assert package["version"] == version
assert package["runtimeHint"] == "dnx"
assert package["transport"]["type"] == "stdio"

arguments = package.get("packageArguments", [])
repository_root = next(
    (item for item in arguments if item.get("type") == "named" and item.get("name") == "--repository-root"),
    None,
)
assert repository_root is not None
assert repository_root.get("format") == "filepath"
assert repository_root.get("isRequired") is True

mcp_readme = pathlib.Path("src/Repo2C4.Mcp/README.md").read_text(encoding="utf-8")
assert "<!-- mcp-name: io.github.rodri-oliveira-dev/repo2c4-mcp -->" in mcp_readme
PY

dotnet pack src/Repo2C4.Cli/Repo2C4.Cli.csproj --configuration Release --no-build --no-restore --output "$packages_dir"
dotnet pack src/Repo2C4.Mcp/Repo2C4.Mcp.csproj --configuration Release --no-build --no-restore --output "$packages_dir"
dotnet pack src/Repo2C4.Agent/Repo2C4.Agent.csproj --configuration Release --no-build --no-restore --output "$packages_dir"

for product in Repo2C4.Cli Repo2C4.Mcp Repo2C4.Agent; do
  archive="$packages_dir/$product.$version.nupkg"
  test -s "$archive"
  unzip -tqq "$archive"
  nuspec="$(unzip -p "$archive" "$product.nuspec")"
  grep -Fq "<id>$product</id>" <<<"$nuspec"
  grep -Fq "<version>$version</version>" <<<"$nuspec"
  grep -Fq "<readme>README.md</readme>" <<<"$nuspec"
  package_readme="$(unzip -p "$archive" README.md)"
  grep -Fq "# Repo2C4" <<<"$package_readme"
  if [[ "$product" == "Repo2C4.Mcp" ]]; then
    grep -Fq "<!-- mcp-name: io.github.rodri-oliveira-dev/repo2c4-mcp -->" <<<"$package_readme"
    grep -Fq "# Repo2C4 MCP" <<<"$package_readme"
  elif [[ "$product" == "Repo2C4.Agent" ]]; then
    grep -Fq "# Repo2C4 Agent" <<<"$package_readme"
  fi
  if grep -Eq '<id>(Template|DotNetLibraryTemplate)([.<]|$)' <<<"$nuspec"; then
    echo "Placeholder package identity detected." >&2
    exit 1
  fi
done
test "$(find "$packages_dir" -maxdepth 1 -type f -name '*.nupkg' | wc -l)" -eq 3

work="$(mktemp -d)"
ollama_pid=""
cleanup() {
  if [ -n "$ollama_pid" ]; then
    kill "$ollama_pid" 2>/dev/null || true
    wait "$ollama_pid" 2>/dev/null || true
  fi
  rm -rf "$work"
}
trap cleanup EXIT
cat > "$work/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="local" value="$packages_dir"/></packageSources></configuration>
EOF

# Use only the three freshly built packages: no live feed, SDK source restore or global tool state.
dotnet tool install --tool-path "$work/cli" Repo2C4.Cli --version "$version" --configfile "$work/NuGet.Config"
dotnet tool install --tool-path "$work/mcp" Repo2C4.Mcp --version "$version" --configfile "$work/NuGet.Config"
dotnet tool install --tool-path "$work/agent" Repo2C4.Agent --version "$version" --configfile "$work/NuGet.Config"
test -x "$work/cli/repo2c4"
test -x "$work/mcp/repo2c4-mcp"
test -x "$work/agent/repo2c4-agent"
"$work/cli/repo2c4" --help | grep -Fq 'Repo2C4 CLI'
"$work/agent/repo2c4-agent" --help >"$work/agent-help.stdout" 2>"$work/agent-help.stderr"
test ! -s "$work/agent-help.stdout"
grep -Fq 'Repo2C4 Agent host' "$work/agent-help.stderr"
"$work/cli/repo2c4" inspect --repository examples/fixtures/library-only --output "$work/snapshot.json"
cmp examples/end-to-end/snapshot.v1.json "$work/snapshot.json"
"$work/cli/repo2c4" generate --model examples/end-to-end/architecture.c1.v1.json --output "$work/likec4" --apply
"$work/cli/repo2c4" validate --output "$work/likec4"
test -s "$work/likec4/evidence-report.md"
test -s "$work/likec4/model.c4"

if ! "$work/mcp/repo2c4-mcp" --help >"$work/mcp.stdout" 2>"$work/mcp.stderr"; then
  echo "Installed MCP executable failed during the help smoke test." >&2
  cat "$work/mcp.stderr" >&2
  exit 1
fi
test ! -s "$work/mcp.stdout"
grep -Fq 'stdout is reserved exclusively for MCP protocol messages' "$work/mcp.stderr"

set +e
"$work/mcp/repo2c4-mcp" --repository-root "$work/missing-repository" >"$work/mcp-bad.stdout" 2>"$work/mcp-bad.stderr"
bad_exit="$?"
set -e
test "$bad_exit" -eq 2
test ! -s "$work/mcp-bad.stdout"
grep -Fq 'invalid or unavailable' "$work/mcp-bad.stderr"

# Exercise the installed MCP executable through real JSON-RPC stdio, not just --help.
python3 - "$work/mcp/repo2c4-mcp" "$(pwd)/examples/fixtures/library-only" <<'PY'
import json
import select
import subprocess
import sys

process = subprocess.Popen(
    [sys.argv[1], "--repository-root", sys.argv[2]],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    text=True, bufsize=1,
)

def request(payload):
    process.stdin.write(json.dumps(payload, separators=(",", ":")) + "\n")
    process.stdin.flush()

def response(expected_id):
    readable, _, _ = select.select([process.stdout], [], [], 20)
    if not readable:
        process.kill()
        raise AssertionError("Installed MCP server did not respond within 20 seconds.")
    line = process.stdout.readline()
    if not line:
        raise AssertionError("Installed MCP server exited without a protocol response.")
    payload = json.loads(line)
    assert payload.get("id") == expected_id, payload
    assert "error" not in payload, payload
    return payload["result"]

try:
    request({"jsonrpc":"2.0","id":1,"method":"initialize","params":{
        "protocolVersion":"2025-11-25","capabilities":{},
        "clientInfo":{"name":"repo2c4-distribution-smoke","version":"1.0.0"}
    }})
    init = response(1)
    assert init["serverInfo"]["name"] == "repo2c4", init
    request({"jsonrpc":"2.0","method":"notifications/initialized","params":{}})
    request({"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}})
    tools = response(2)["tools"]
    names = {tool["name"] for tool in tools}
    assert {"inspect_repository", "get_evidence", "get_snapshot",
            "generate_likec4", "validate_likec4"}.issubset(names), names
    request({"jsonrpc":"2.0","id":3,"method":"tools/call","params":{
        "name":"inspect_repository","arguments":{"repositoryPath":".","maxFiles":50}
    }})
    inspected = response(3)
    assert inspected.get("isError") is not True, inspected
    assert "snapshotId" in json.dumps(inspected), inspected
finally:
    process.stdin.close()
    process.wait(timeout=10)
    assert process.returncode == 0, process.returncode
PY

# Exercise the installed Agent with a controlled loopback Ollama-compatible fake and
# the installed real MCP against a checked-in local fixture. The fake intentionally
# returns no architecture proposal, so a controlled insufficient-evidence result is expected.
port_file="$work/ollama-port"
request_file="$work/ollama-request.json"
python3 - "$port_file" "$request_file" <<'PY' &
import http.server
import json
import socketserver
import sys

port_file, request_file = sys.argv[1:3]

class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, format, *args):
        return

    def do_POST(self):
        if self.path != "/api/chat":
            self.send_response(404)
            self.end_headers()
            return

        length = int(self.headers.get("Content-Length", "0"))
        body = self.rfile.read(length)
        with open(request_file, "wb") as output:
            output.write(body)

        response = {
            "model": "repo2c4-distribution-smoke",
            "created_at": "2026-09-30T00:00:00Z",
            "message": {
                "role": "assistant",
                "content": (
                    "Confirmed facts\n"
                    "The controlled smoke model made no architectural claim.\n\n"
                    "Requires review\n"
                    "Repository evidence requires a real model or human review.\n\n"
                    "Diagnostics/blockers\n"
                    "Distribution smoke intentionally returns no proposal.\n\n"
                    "Proposal\n"
                    "No C1/C2 proposal was produced."
                ),
            },
            "done": True,
            "done_reason": "stop",
            "total_duration": 1,
            "load_duration": 1,
            "prompt_eval_count": 1,
            "prompt_eval_duration": 1,
            "eval_count": 1,
            "eval_duration": 1,
        }
        payload = json.dumps(response).encode("utf-8")
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

class Server(socketserver.TCPServer):
    allow_reuse_address = True

with Server(("127.0.0.1", 0), Handler) as server:
    with open(port_file, "w", encoding="utf-8") as output:
        output.write(str(server.server_address[1]))
    server.serve_forever()
PY
ollama_pid="$!"

for _ in {1..100}; do
  if [ -s "$port_file" ]; then
    break
  fi
  sleep 0.05
done
test -s "$port_file"
ollama_port="$(cat "$port_file")"

set +e
"$work/agent/repo2c4-agent" \
  --provider ollama \
  --model repo2c4-distribution-smoke \
  --endpoint "http://127.0.0.1:$ollama_port/" \
  --repository-root "$repo_root/examples/fixtures/library-only" \
  --mcp-server-path "$work/mcp/repo2c4-mcp" \
  --goal "Distribution smoke: inspect the fixture conservatively." \
  --timeout-seconds 10 \
  --max-duration-seconds 30 \
  >"$work/agent.stdout" 2>"$work/agent.stderr"
agent_exit="$?"
set -e

kill "$ollama_pid" 2>/dev/null || true
wait "$ollama_pid" 2>/dev/null || true
ollama_pid=""

test "$agent_exit" -eq 1
test -s "$request_file"
grep -Fq '"model":"repo2c4-distribution-smoke"' "$request_file" \
  || grep -Fq '"model": "repo2c4-distribution-smoke"' "$request_file"
grep -Fq 'Status: failed' "$work/agent.stdout"
grep -Fq 'Terminal reason: insufficient_evidence' "$work/agent.stdout"
test -z "$(git -C "$repo_root" status --porcelain -- examples/fixtures/library-only)"

printf 'Versioned .NET tools installed and exercised from a clean local feed: CLI, MCP and Agent %s.\n' "$version"
