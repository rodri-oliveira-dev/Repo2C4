#!/usr/bin/env python3
"""Manual, review-first LikeC4 PR automation. No dependency on provider credentials in tests."""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

FILES = frozenset({
    "model.c4", "specification.c4", "views.c4", "c3.views.c4",
    "evidence-report.md", ".repo2c4-manifest.json",
})
ID_PATTERN = re.compile(r"[a-z][a-z0-9-]{0,39}\Z")
MAX_FILE = 4 * 1024 * 1024


class AutomationError(Exception):
    """A safe diagnostic, never raw tool output or a provider error body."""


def command(*args: str, cwd: Path, env: dict[str, str] | None = None) -> str:
    result = subprocess.run(
        args, cwd=cwd, env=env, text=True, capture_output=True, check=False
    )
    if result.returncode != 0:
        name = Path(args[0]).name
        raise AutomationError(f"{name} failed (exit {result.returncode}); inspect the selected inputs and checks.")
    return result.stdout.strip()


def relative_file(root: Path, value: str, *, directory: bool = False) -> Path:
    if not value or value.startswith(("/", "\\")) or "\\" in value or ":" in value:
        raise AutomationError("Input path must be repository-relative and contain no backslash or drive.")
    pieces = value.split("/")
    if any(part in ("", ".", "..") for part in pieces):
        raise AutomationError("Input path contains an empty, current or parent segment.")
    resolved = root
    for part in pieces:
        resolved = resolved / part
        if resolved.is_symlink():
            raise AutomationError("Linked paths are not permitted.")
    if directory and not resolved.is_dir():
        raise AutomationError("The selected repository root does not exist.")
    if not directory and not resolved.is_file():
        raise AutomationError("The selected reviewed architecture model does not exist.")
    if not resolved.resolve().is_relative_to(root.resolve()):
        raise AutomationError("Input path leaves the checked-out repository.")
    return resolved


def validate_options(args: argparse.Namespace, root: Path) -> tuple[Path, Path | None]:
    if args.repository != os.environ.get("GITHUB_REPOSITORY", args.repository):
        raise AutomationError("Requested repository differs from the authorized workflow repository.")
    if args.source_ref != "main":
        raise AutomationError("Only the protected main branch is an authorized automation source.")
    if os.environ.get("GITHUB_ACTIONS") == "true":
        if os.environ.get("GITHUB_REF") != "refs/heads/main":
            raise AutomationError("The manual workflow must run from main, not a fork or feature branch.")
        if os.environ.get("REPO2C4_EVENT_FORK") != "false":
            raise AutomationError("Repository forks cannot run the documentation publishing workflow.")
    if not ID_PATTERN.fullmatch(args.output_id):
        raise AutomationError("Output ID must be a lowercase slug of up to 40 characters.")
    if args.mode not in ("reviewed", "infer"):
        raise AutomationError("Use reviewed or infer mode.")
    if args.mode == "reviewed":
        if args.provider or args.model_id or args.allow_external_ai or not args.model_path:
            raise AutomationError("Reviewed mode requires a model path and forbids provider/consent flags.")
    elif (
        args.model_path or args.provider != "openai" or not args.model_id
        or not args.allow_external_ai
    ):
        raise AutomationError("Hosted inference requires OpenAI, a model ID and explicit external-AI consent.")
    root_path = root if args.repository_root == "." else relative_file(
        root, args.repository_root, directory=True
    )
    model_path = relative_file(root, args.model_path) if args.mode == "reviewed" else None
    if model_path is not None and (model_path.suffix != ".json" or model_path.stat().st_size > MAX_FILE):
        raise AutomationError("The reviewed model must be a bounded JSON file.")
    if os.environ.get("GITHUB_ACTIONS") == "true":
        if command("git", "rev-parse", "HEAD", cwd=root) != args.source_sha:
            raise AutomationError("Checked-out commit differs from the authorized source SHA.")
    return root_path, model_path


def checked_managed_files(root: Path, output: Path) -> tuple[list[str], list[str]]:
    prefix = output.relative_to(root).as_posix() + "/"
    changed: list[str] = []
    removed: list[str] = []
    status = subprocess.run(
        ["git", "status", "--porcelain=v1", "-z", "--untracked-files=all", "--", prefix],
        cwd=root, capture_output=True, check=False,
    )
    if status.returncode != 0:
        raise AutomationError("Could not inspect generated-file diff.")
    entries = status.stdout.split(b"\0")
    for entry in entries:
        if not entry:
            continue
        try:
            kind = entry[:2].decode("ascii")
            path = entry[3:].decode("utf-8")
        except UnicodeError as exception:
            raise AutomationError("Invalid generated-file status encoding.") from exception
        if not path.startswith(prefix) or path[len(prefix):] not in FILES:
            raise AutomationError("Automation detected an unexpected generated-file change.")
        if kind not in ("??", " M", " D", "M ", "D ", "A ", "AM"):
            raise AutomationError("Automation detected an unsupported generated-file status.")
        name = path[len(prefix):]
        if "D" in kind:
            removed.append(name)
        else:
            changed.append(name)
    return sorted(set(changed)), sorted(set(removed))


def safe_output_root(root: Path, output_id: str) -> Path:
    output = root / "docs" / "generated" / output_id
    if any(part.is_symlink() for part in (root / "docs", root / "docs" / "generated", output)):
        raise AutomationError("Generated output path must not contain linked directories.")
    if not output.resolve().is_relative_to(root.resolve()):
        raise AutomationError("Generated output path leaves the authorized repository.")
    return output


def prepare(args: argparse.Namespace, root: Path) -> bool:
    repository_root, model_path = validate_options(args, root)
    output = safe_output_root(root, args.output_id)
    staging = Path(args.artifact_dir).resolve()
    if staging.exists():
        raise AutomationError("Artifact staging directory must be empty and newly created.")
    if staging.is_relative_to(root.resolve()):
        raise AutomationError("Staging directory must be outside the source repository.")
    staging.mkdir(parents=True)
    cli = (root / "src/Repo2C4.Cli/bin/Release/net10.0/Repo2C4.Cli.dll").as_posix()
    temp = staging / "private"
    temp.mkdir()
    snapshot = temp / "snapshot.json"
    try:
        command("dotnet", cli, "inspect", "--repository", str(repository_root),
                "--output", str(snapshot), cwd=root)
        if model_path is None:
            if not os.environ.get("OPENAI_API_KEY"):
                raise AutomationError("OPENAI_API_KEY is required for explicit hosted inference.")
            candidate = temp / "candidate.json"
            command("dotnet", cli, "infer", "--snapshot", str(snapshot),
                    "--provider", "openai", "--model-id", args.model_id,
                    "--allow-external-ai", "--output", str(candidate), cwd=root)
            model_path = candidate
        elif json.loads(model_path.read_text(encoding="utf-8")).get("snapshot") != json.loads(
                snapshot.read_text(encoding="utf-8")):
            raise AutomationError("Reviewed model snapshot differs from the freshly inspected authorized repository.")
        command("dotnet", cli, "generate", "--model", str(model_path),
                "--output", str(output), cwd=root)
        command("dotnet", cli, "generate", "--model", str(model_path),
                "--output", str(output), "--apply", cwd=root)
        command("dotnet", cli, "validate", "--output", str(output), cwd=root)
        changed, removed = checked_managed_files(root, output)
        if not changed and not removed:
            return False
        if not (output / "evidence-report.md").is_file():
            raise AutomationError("Validated output is missing its evidence report.")
        destination = staging / "managed"
        destination.mkdir()
        hashes: dict[str, str] = {}
        for name in sorted(FILES):
            path = output / name
            if not path.exists():
                continue
            if path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_FILE:
                raise AutomationError("Generated output has an invalid managed file.")
            data = path.read_bytes()
            (destination / name).write_bytes(data)
            hashes[name] = hashlib.sha256(data).hexdigest()
        info = {
            "source_sha": args.source_sha,
            "repository": args.repository,
            "output_id": args.output_id,
            "mode": args.mode,
            "model_id": args.model_id if args.mode == "infer" else "",
            "changed": changed,
            "removed": removed,
            "sha256": hashes,
            "validated": True,
        }
        (staging / "metadata.json").write_text(json.dumps(info, sort_keys=True) + "\n", encoding="utf-8")
        return True
    finally:
        # The original snapshot and AI-generated candidate never leave this job.
        shutil.rmtree(staging / "private", ignore_errors=True)


def publish(args: argparse.Namespace, root: Path) -> str:
    if os.environ.get("GITHUB_REPOSITORY") != args.repository:
        raise AutomationError("Publishing repository mismatch.")
    if os.environ.get("GITHUB_REF") != "refs/heads/main":
        raise AutomationError("Only a main-branch dispatch can publish a documentation PR.")
    if not os.environ.get("GH_TOKEN"):
        raise AutomationError("GITHUB_TOKEN unavailable; check Actions workflow write permissions.")
    staging = Path(args.artifact_dir).resolve()
    info = json.loads((staging / "metadata.json").read_text(encoding="utf-8"))
    if (
        info.get("repository") != args.repository or info.get("source_sha") != args.source_sha
        or info.get("output_id") != args.output_id or info.get("validated") is not True
        or not ID_PATTERN.fullmatch(args.output_id)
        or command("git", "rev-parse", "HEAD", cwd=root) != args.source_sha
    ):
        raise AutomationError("Artifact/source identity does not match the validated dispatch.")
    remote_main = command("git", "ls-remote", "origin", "refs/heads/main", cwd=root).split()
    if not remote_main or remote_main[0] != args.source_sha:
        raise AutomationError("main moved or could not be resolved; restart the dispatch from current main.")
    hashes = info["sha256"]
    changed, removed = info["changed"], info["removed"]
    if (not isinstance(hashes, dict) or not isinstance(changed, list)
            or not isinstance(removed, list)
            or not changed and not removed
            or any(name not in FILES for name in (*hashes, *changed, *removed))
            or not set(changed).issubset(hashes)
            or set(changed) & set(removed)
            or "evidence-report.md" not in hashes):
        raise AutomationError("Artifact contains unsupported managed-file metadata.")
    origin = safe_output_root(root, args.output_id)
    for name, digest in hashes.items():
        file = staging / "managed" / name
        if file.is_symlink() or not file.is_file() or file.stat().st_size > MAX_FILE:
            raise AutomationError("Artifact contains an invalid managed file.")
        if hashlib.sha256(file.read_bytes()).hexdigest() != digest:
            raise AutomationError("Artifact checksum verification failed.")
    branch = "bot/repo2c4-" + args.output_id
    existing = command("gh", "pr", "list", "--repo", args.repository,
                       "--head", branch, "--base", "main", "--state", "open",
                       "--json", "url", "--jq", ".[0].url // empty", cwd=root)
    if existing:
        return "Existing review PR: " + existing
    branch_check = subprocess.run(
        ["git", "ls-remote", "--exit-code", "origin", "refs/heads/" + branch],
        cwd=root, capture_output=True, check=False
    )
    if branch_check.returncode == 0:
        raise AutomationError("A documentation branch already exists without an open PR; review it manually.")
    if branch_check.returncode != 2:
        raise AutomationError("Could not check the dedicated documentation branch; verify repository write access.")

    command("git", "checkout", "-b", branch, cwd=root)
    origin.mkdir(parents=True, exist_ok=True)
    for name in removed:
        target = origin / name
        if target.is_symlink():
            raise AutomationError("Refusing to delete a linked managed file.")
        if target.exists():
            target.unlink()
    for name in hashes:
        target = origin / name
        if target.is_symlink():
            raise AutomationError("Refusing to replace a linked managed file.")
        shutil.copyfile(staging / "managed" / name, target)
    pathspec = "docs/generated/" + args.output_id
    command("git", "add", "-A", "--", pathspec, cwd=root)
    staged = command("git", "diff", "--cached", "--name-only", cwd=root).splitlines()
    if not staged or any(
        not name.startswith(pathspec + "/") or name[len(pathspec) + 1:] not in FILES
        for name in staged
    ):
        raise AutomationError("No allowed managed-file diff is available for a review PR.")
    command("git", "config", "user.name", "github-actions[bot]", cwd=root)
    command("git", "config", "user.email", "41898282+github-actions[bot]@users.noreply.github.com", cwd=root)
    command("git", "commit", "-m", "docs(likec4): propose reviewed documentation for " + args.output_id, cwd=root)
    # Auth is scoped to the publishing job, after all checks. Do not persist credentials in checkout.
    command("gh", "auth", "setup-git", cwd=root)
    try:
        command("git", "push", "origin", "HEAD:refs/heads/" + branch, cwd=root)
    except AutomationError as exception:
        raise AutomationError(
            "Could not push the dedicated documentation branch. Check the workflow token "
            "contents:write permission, branch rulesets and repository Actions write policy."
        ) from exception
    body = (
        "## Review-required LikeC4 documentation\n\n"
        "Source: protected main at `" + args.source_sha + "` (mode: `" + info["mode"] + "`).\n\n"
        "Generated files: " + ", ".join(" `" + p + "`" for p in staged) + ".\n\n"
        "Validation: locked restore, Release build and tests in the dispatch; "
        "official LikeC4 CLI validation passed before publishing.\n\n"
        "Evidence and outstanding architectural hypotheses: "
        "[evidence-report.md](../blob/" + branch + "/" + pathspec + "/evidence-report.md).\n\n"
        "**Human review is required.** Evidence references do not independently prove runtime "
        "relationships or system boundaries. No automatic merge or architecture approval is performed.\n"
    )
    try:
        url = command("gh", "pr", "create", "--repo", args.repository, "--base", "main",
                      "--head", branch, "--title", "docs: review LikeC4 for " + args.output_id,
                      "--body", body, cwd=root)
    except AutomationError as exception:
        raise AutomationError(
            "The validated branch was pushed, but creating the PR failed. "
            "Check Settings > Actions > General > workflow PR permissions, "
            "repository rulesets and the dedicated branch before retrying."
        ) from exception
    return "Review PR created: " + url


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("operation", choices=("prepare", "publish"))
    parser.add_argument("--repository", required=True)
    parser.add_argument("--source-ref", default="main")
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--repository-root", default=".")
    parser.add_argument("--mode", choices=("reviewed", "infer"), default="reviewed")
    parser.add_argument("--model-path", default="")
    parser.add_argument("--provider", default="")
    parser.add_argument("--model-id", default="")
    parser.add_argument("--allow-external-ai", action="store_true")
    parser.add_argument("--output-id", required=True)
    parser.add_argument("--artifact-dir", required=True)
    args = parser.parse_args()
    try:
        root = Path.cwd().resolve()
        if args.operation == "prepare":
            changed = prepare(args, root)
            with open(os.environ.get("GITHUB_OUTPUT", os.devnull), "a", encoding="utf-8") as target:
                target.write("has_changes=" + ("true" if changed else "false") + "\n")
            print("Validated managed-file changes are available for PR review." if changed
                  else "No managed-file changes; no PR is necessary.")
        else:
            print(publish(args, root))
    except (AutomationError, OSError, ValueError, KeyError, TypeError, json.JSONDecodeError) as exception:
        # No third-party body, model output, input JSON, path or credential is printed.
        message = str(exception) if isinstance(exception, AutomationError) else "Invalid automation input or artifact."
        print("Repo2C4 automation: " + message, file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
