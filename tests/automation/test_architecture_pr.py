"""Deterministic, credential-free checks for the manual review PR automation."""
from __future__ import annotations

import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

MODULE_PATH = Path(__file__).resolve().parents[2] / "scripts" / "architecture-pr.py"
SPEC = importlib.util.spec_from_file_location("repo2c4_architecture_pr", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
automation = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(automation)
REPOSITORY = "rodri-oliveira-dev/Repo2C4"
SOURCE_SHA = "a" * 40
SNAPSHOT = {"schemaVersion": "1.0", "repositoryId": "repo_fixture", "files": [], "evidence": [], "diagnostics": []}


def args_for(root: Path, *, mode: str = "reviewed", model_path: str = "models/reviewed.json",
             consent: bool = False) -> argparse.Namespace:
    return argparse.Namespace(
        repository=REPOSITORY, source_ref="main", source_sha=SOURCE_SHA,
        repository_root="fixtures/local", mode=mode,
        model_path=model_path if mode == "reviewed" else "",
        provider="openai" if mode == "infer" else "",
        model_id="test-model" if mode == "infer" else "",
        allow_external_ai=consent, output_id="fixture",
        artifact_dir=str(root.parent / "stage"),
    )


class ArchitecturePrTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory(prefix="repo2c4-automation-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name) / "checkout"
        self.root.mkdir()
        (self.root / "fixtures" / "local").mkdir(parents=True)
        (self.root / "models").mkdir()
        (self.root / "models" / "reviewed.json").write_text(
            json.dumps({"snapshot": SNAPSHOT}), encoding="utf-8"
        )
        for argv in (
            ("git", "init", "-q", "-b", "main"),
            ("git", "config", "user.email", "ci@example.invalid"),
            ("git", "config", "user.name", "CI Test"),
            ("git", "add", "."),
            ("git", "commit", "-q", "-m", "fixture"),
        ):
            subprocess.run(argv, cwd=self.root, check=True, capture_output=True)
        self.environment = patch.dict(os.environ, {"GITHUB_REPOSITORY": REPOSITORY, "GITHUB_ACTIONS": "false"}, clear=False)
        self.environment.start()
        self.addCleanup(self.environment.stop)
        self.original_command = automation.command

    def fake_cli(self, *, invalid_schema: bool = False, invalid_likec4: bool = False,
                 no_diff: bool = False) -> tuple[object, list[tuple[str, ...]]]:
        calls: list[tuple[str, ...]] = []

        def execute(*values: str, cwd: Path, env=None) -> str:
            calls.append(values)
            if values[0] != "dotnet":
                return self.original_command(*values, cwd=cwd, env=env)
            verb = values[2]
            if verb == "inspect":
                Path(values[values.index("--output") + 1]).write_text(
                    json.dumps(SNAPSHOT), encoding="utf-8"
                )
            elif verb == "infer":
                Path(values[values.index("--output") + 1]).write_text(
                    json.dumps({"snapshot": SNAPSHOT}), encoding="utf-8"
                )
            elif verb == "generate":
                if invalid_schema:
                    raise automation.AutomationError("dotnet failed (exit 3).")
                if "--apply" in values:
                    destination = Path(values[values.index("--output") + 1])
                    destination.mkdir(parents=True, exist_ok=True)
                    (destination / "model.c4").write_text("system candidate\n", encoding="utf-8")
                    (destination / "evidence-report.md").write_text(
                        "Hypotheses requiring review\n", encoding="utf-8"
                    )
                    (destination / ".repo2c4-manifest.json").write_text("{}\n", encoding="utf-8")
            elif verb == "validate" and invalid_likec4:
                raise automation.AutomationError("dotnet failed (exit 4).")
            return ""

        return execute, calls

    def test_reviewed_fixture_with_diff_prepares_only_approved_files(self) -> None:
        fake, calls = self.fake_cli()
        with patch.object(automation, "command", fake):
            self.assertTrue(automation.prepare(args_for(self.root), self.root))
        info = json.loads((self.root.parent / "stage" / "metadata.json").read_text())
        self.assertEqual([".repo2c4-manifest.json", "evidence-report.md", "model.c4"], info["changed"])
        self.assertTrue(info["validated"])
        self.assertEqual([], info["removed"])
        self.assertEqual(1, sum(1 for call in calls if "validate" in call))
        self.assertFalse((self.root.parent / "stage" / "private").exists())
        self.assertFalse((self.root.parent / "stage" / "managed" / "snapshot.json").exists())

    def test_no_diff_never_produces_publishable_artifact(self) -> None:
        output = self.root / "docs" / "generated" / "fixture"
        output.mkdir(parents=True)
        (output / "model.c4").write_text("system candidate\n", encoding="utf-8")
        (output / "evidence-report.md").write_text("Hypotheses requiring review\n", encoding="utf-8")
        (output / ".repo2c4-manifest.json").write_text("{}\n", encoding="utf-8")
        subprocess.run(["git", "add", "."], cwd=self.root, check=True, capture_output=True)
        subprocess.run(["git", "commit", "-q", "-m", "existing output"],
                       cwd=self.root, check=True, capture_output=True)
        fake, _ = self.fake_cli(no_diff=True)
        with patch.object(automation, "command", fake):
            self.assertFalse(automation.prepare(args_for(self.root), self.root))
        self.assertFalse((self.root.parent / "stage" / "metadata.json").exists())

    def test_schema_failure_blocks_artifact_and_pr(self) -> None:
        fake, _ = self.fake_cli(invalid_schema=True)
        with patch.object(automation, "command", fake):
            with self.assertRaisesRegex(automation.AutomationError, "exit 3"):
                automation.prepare(args_for(self.root), self.root)
        self.assertFalse((self.root.parent / "stage" / "metadata.json").exists())

    def test_likec4_failure_blocks_artifact_and_pr(self) -> None:
        fake, _ = self.fake_cli(invalid_likec4=True)
        with patch.object(automation, "command", fake):
            with self.assertRaisesRegex(automation.AutomationError, "exit 4"):
                automation.prepare(args_for(self.root), self.root)
        self.assertFalse((self.root.parent / "stage" / "metadata.json").exists())

    def test_missing_credential_never_calls_inference(self) -> None:
        fake, calls = self.fake_cli()
        with patch.dict(os.environ, {"OPENAI_API_KEY": ""}), patch.object(automation, "command", fake):
            with self.assertRaisesRegex(automation.AutomationError, "OPENAI_API_KEY"):
                automation.prepare(args_for(self.root, mode="infer", consent=True), self.root)
        self.assertFalse(any("infer" in call for call in calls))

    def test_cloud_consent_and_repository_guards_run_before_network(self) -> None:
        with self.assertRaisesRegex(automation.AutomationError, "consent"):
            automation.validate_options(args_for(self.root, mode="infer"), self.root)
        untrusted = args_for(self.root)
        untrusted.source_ref = "feature"
        with self.assertRaisesRegex(automation.AutomationError, "protected main"):
            automation.validate_options(untrusted, self.root)
        untrusted.source_ref = "main"
        untrusted.repository = "foreign/fork"
        with self.assertRaisesRegex(automation.AutomationError, "repository differs"):
            automation.validate_options(untrusted, self.root)

    def test_unmatched_reviewed_snapshot_blocks_generation(self) -> None:
        (self.root / "models" / "reviewed.json").write_text(
            json.dumps({"snapshot": {**SNAPSHOT, "repositoryId": "other"}}), encoding="utf-8"
        )
        fake, calls = self.fake_cli()
        with patch.object(automation, "command", fake):
            with self.assertRaisesRegex(automation.AutomationError, "snapshot differs"):
                automation.prepare(args_for(self.root), self.root)
        self.assertFalse(any("generate" in call for call in calls))

    def test_linked_input_rejected(self) -> None:
        (self.root / "linked.json").symlink_to(self.root / "models" / "reviewed.json")
        with self.assertRaisesRegex(automation.AutomationError, "Linked paths"):
            automation.relative_file(self.root, "linked.json")

    def test_write_denied_prevents_publish(self) -> None:
        with patch.dict(os.environ, {"GITHUB_REPOSITORY": REPOSITORY,
                                     "GITHUB_REF": "refs/heads/main", "GH_TOKEN": ""}):
            with self.assertRaisesRegex(automation.AutomationError, "write permissions"):
                automation.publish(args_for(self.root), self.root)

    def test_validated_diff_creates_exactly_one_review_pr_with_safe_description(self) -> None:
        info = args_for(self.root)
        stage = Path(info.artifact_dir)
        managed = stage / "managed"
        managed.mkdir(parents=True)
        contents = {
            "model.c4": b"system candidate\n",
            "evidence-report.md": b"Hypotheses requiring review\n",
            ".repo2c4-manifest.json": b"{}\n",
        }
        for name, data in contents.items():
            (managed / name).write_bytes(data)
        (stage / "metadata.json").write_text(json.dumps({
            "source_sha": SOURCE_SHA, "repository": REPOSITORY, "output_id": "fixture",
            "mode": "reviewed", "changed": sorted(contents), "removed": [],
            "sha256": {name: hashlib.sha256(data).hexdigest() for name, data in contents.items()},
            "validated": True,
        }), encoding="utf-8")
        called: list[tuple[str, ...]] = []

        def fake_command(*values: str, cwd: Path, env=None) -> str:
            called.append(values)
            if values[:3] == ("git", "rev-parse", "HEAD"):
                return SOURCE_SHA
            if values[:3] == ("git", "ls-remote", "origin"):
                return SOURCE_SHA + "\trefs/heads/main"
            if values[:3] == ("gh", "pr", "list"):
                return ""
            if values[:4] == ("git", "diff", "--cached", "--name-only"):
                return "\n".join("docs/generated/fixture/" + name for name in sorted(contents))
            if values[:3] == ("gh", "pr", "create"):
                return "https://github.com/rodri-oliveira-dev/Repo2C4/pull/123"
            return ""

        real_run = subprocess.run

        def fake_run(values, *a, **kw):
            if values[:3] == ["git", "ls-remote", "--exit-code"]:
                return subprocess.CompletedProcess(values, 2, b"", b"")
            return real_run(values, *a, **kw)

        with patch.dict(os.environ, {"GITHUB_REPOSITORY": REPOSITORY,
                                     "GITHUB_REF": "refs/heads/main", "GH_TOKEN": "temporary-ci-token"}):
            with patch.object(automation, "command", fake_command), patch.object(
                    automation.subprocess, "run", side_effect=fake_run):
                output = automation.publish(info, self.root)
        self.assertIn("/pull/123", output)
        creations = [call for call in called if call[:3] == ("gh", "pr", "create")]
        self.assertEqual(1, len(creations))
        body = creations[0][creations[0].index("--body") + 1]
        self.assertIn("Human review is required", body)
        self.assertIn("evidence-report.md", body)
        self.assertNotIn("temporary-ci-token", body)
        self.assertFalse(any("merge" in call for call in called))


if __name__ == "__main__":
    unittest.main()
