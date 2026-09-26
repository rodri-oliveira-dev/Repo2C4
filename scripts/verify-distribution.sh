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

dotnet pack src/Repo2C4.Cli/Repo2C4.Cli.csproj --configuration Release --no-build --no-restore --output "$packages_dir"
dotnet pack src/Repo2C4.Mcp/Repo2C4.Mcp.csproj --configuration Release --no-build --no-restore --output "$packages_dir"

for product in Repo2C4.Cli Repo2C4.Mcp; do
  archive="$packages_dir/$product.$version.nupkg"
  test -s "$archive"
  unzip -tqq "$archive"
  nuspec="$(unzip -p "$archive" "$product.nuspec")"
  grep -Fq "<id>$product</id>" <<<"$nuspec"
  grep -Fq "<version>$version</version>" <<<"$nuspec"
  if grep -Eq '<id>(Template|DotNetLibraryTemplate)([.<]|$)' <<<"$nuspec"; then
    echo "Placeholder package identity detected." >&2
    exit 1
  fi
done
test "$(find "$packages_dir" -maxdepth 1 -type f -name '*.nupkg' | wc -l)" -eq 2

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
cat > "$work/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="local" value="$packages_dir"/></packageSources></configuration>
EOF

# Use only the two freshly built packages: no live feed, SDK source restore or global tool state.
dotnet tool install --tool-path "$work/cli" Repo2C4.Cli --version "$version" --configfile "$work/NuGet.Config"
dotnet tool install --tool-path "$work/mcp" Repo2C4.Mcp --version "$version" --configfile "$work/NuGet.Config"
test -x "$work/cli/repo2c4"
test -x "$work/mcp/repo2c4-mcp"
"$work/cli/repo2c4" --help | grep -Fq 'Repo2C4 CLI'
"$work/cli/repo2c4" inspect --repository examples/fixtures/library-only --output "$work/snapshot.json"
cmp examples/end-to-end/snapshot.v1.json "$work/snapshot.json"
"$work/cli/repo2c4" generate --model examples/end-to-end/architecture.c1.v1.json --output "$work/likec4" --apply
"$work/cli/repo2c4" validate --output "$work/likec4"
test -s "$work/likec4/evidence-report.md"
test -s "$work/likec4/model.c4"

"$work/mcp/repo2c4-mcp" --help >"$work/mcp.stdout" 2>"$work/mcp.stderr"
test ! -s "$work/mcp.stdout"
grep -Fq 'stdout is reserved exclusively for MCP protocol messages' "$work/mcp.stderr"

set +e
"$work/mcp/repo2c4-mcp" --repository-root "$work/missing-repository" >"$work/mcp-bad.stdout" 2>"$work/mcp-bad.stderr"
bad_exit="$?"
set -e
test "$bad_exit" -eq 2
test ! -s "$work/mcp-bad.stdout"
grep -Fq 'invalid or unavailable' "$work/mcp-bad.stderr"

printf 'Versioned .NET tools installed and exercised from a clean local feed: CLI and MCP %s.\n' "$version"
