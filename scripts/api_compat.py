"""Public-API compatibility: the checks around NuGet package validation.

``dotnet pack`` validates the package against the last release
(``EnablePackageValidation``, ``PackageValidationBaselineVersion`` in
src/GenesisMesh.Sdk/GenesisMesh.Sdk.csproj) and fails on a removed or changed
public member (CP0001, CP0002, ...). A break made on purpose is recorded in
``src/GenesisMesh.Sdk/CompatibilitySuppressions.xml``:

    dotnet pack src/GenesisMesh.Sdk -c Release -p:ApiCompatGenerateSuppressionFile=true

This script checks the two things package validation cannot:

    python3 scripts/api_compat.py baseline [--strict]
    python3 scripts/api_compat.py gate --base BASE_SHA --head HEAD_SHA --labels '["breaking"]'

``baseline`` reports a release newer than the baseline that is on NuGet: the
baseline must be the latest release, or a later break would be compared with
an older API. It warns, so a release does not turn every open pull request
red; with ``--strict`` (CI on a push to main) it fails. After a release, set
the baseline to it and delete the suppression file in one pull request (the
release steps in AGENT.md). A tag whose package is not on NuGet yet, while
publish.yml runs, only warns.

``gate`` fails a pull request that adds or changes the suppression file (it
accepts a break) unless the pull request carries the label ``breaking`` and
adds or changes a changelog fragment with an ``### Upgrading`` section.
"""

from __future__ import annotations

import argparse
import json
import re
import subprocess
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "src" / "GenesisMesh.Sdk" / "GenesisMesh.Sdk.csproj"
SUPPRESSIONS = "src/GenesisMesh.Sdk/CompatibilitySuppressions.xml"
LABEL = "breaking"
STABLE = re.compile(r"^v(\d+)\.(\d+)\.(\d+)$")


def _property(name: str) -> str:
    found = re.search(rf"<{name}>([^<]+)</{name}>", PROJECT.read_text(encoding="utf-8"))
    if not found:
        raise SystemExit(f"{PROJECT.relative_to(ROOT)}: no <{name}>")
    return found.group(1).strip()


def _version(text: str) -> tuple[int, int, int]:
    major, minor, patch = (int(p) for p in text.split("."))
    return major, minor, patch


def _git(*args: str) -> str:
    return subprocess.run(["git", *args], cwd=ROOT, check=True, capture_output=True, text=True).stdout


def _release_tags(remote: str) -> list[tuple[int, int, int]]:
    out = _git("ls-remote", "--tags", "--refs", remote, "refs/tags/v*")
    tags = []
    for line in out.splitlines():
        name = line.split("refs/tags/", 1)[-1]
        found = STABLE.match(name)
        if found:
            tags.append((int(found[1]), int(found[2]), int(found[3])))
    return sorted(tags)


def _published(package: str) -> set[str]:
    url = f"https://api.nuget.org/v3-flatcontainer/{package.lower()}/index.json"
    with urllib.request.urlopen(url, timeout=30) as response:  # noqa: S310 (a fixed https URL)
        return set(json.load(response)["versions"])


def baseline(remote: str, strict: bool) -> int:
    current = _property("PackageValidationBaselineVersion")
    tags = _release_tags(remote)
    if not tags:
        print(f"::error::no release tag v<major>.<minor>.<patch> on {remote}")
        return 1
    latest = tags[-1]
    latest_text = ".".join(map(str, latest))
    if latest <= _version(current):
        print(f"Package validation baseline {current}; latest release {latest_text}.")
        return 0
    if latest_text not in _published(_property("PackageId")):
        print(f"::warning::Release {latest_text} is tagged but not on NuGet yet; package validation still compares "
              f"with {current}. Once it is published, set PackageValidationBaselineVersion to {latest_text}.")
        return 0
    level = "error" if strict else "warning"
    print(f"::{level}::Package validation compares with {current}, but {latest_text} is released. Set "
          f"<PackageValidationBaselineVersion> in {PROJECT.relative_to(ROOT).as_posix()} to {latest_text} and delete "
          f"{SUPPRESSIONS} (its breaks are in the release now).")
    return 1 if strict else 0


def _changed(base: str, head: str, *paths: str) -> list[str]:
    out = _git("diff", "--name-only", "--no-renames", "--diff-filter=AM", f"{base}...{head}", "--", *paths)
    return [line for line in out.splitlines() if line]


def gate(base: str, head: str, labels: list[str]) -> int:
    if not _changed(base, head, SUPPRESSIONS):
        print(f"{SUPPRESSIONS} unchanged: no break accepted by this pull request.")
        return 0
    problems = []
    if LABEL not in labels:
        problems.append(f"the label '{LABEL}' (the Maintainer adds it)")
    fragments = [f for f in _changed(base, head, "changelog.d") if f.endswith(".md")]
    upgrading = [f for f in fragments
                 if any(line.strip() == "### Upgrading"
                        for line in _git("show", f"{head}:{f}").splitlines())]
    if not upgrading:
        problems.append("a changelog fragment (changelog.d/<version>/<name>.md) with an '### Upgrading' section")
    if problems:
        print(f"::error::This pull request changes {SUPPRESSIONS}, so it accepts a breaking change of the public API. "
              f"It also needs: {'; and '.join(problems)}.")
        return 1
    print(f"Breaking change accepted: label '{LABEL}', Upgrading note in {', '.join(upgrading)}.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    commands = parser.add_subparsers(dest="command", required=True)
    base_cmd = commands.add_parser("baseline", help="the baseline is the latest release on NuGet")
    base_cmd.add_argument("--remote", default="https://github.com/GenesisMeshLabs/sdk-dotnet.git")
    base_cmd.add_argument("--strict", action="store_true", help="fail, rather than warn, on a stale baseline")
    gate_cmd = commands.add_parser("gate", help="an accepted break has the label and an Upgrading note")
    gate_cmd.add_argument("--base", required=True)
    gate_cmd.add_argument("--head", required=True)
    gate_cmd.add_argument("--labels", default="[]", help="the pull request's labels as a JSON list")
    args = parser.parse_args()
    if args.command == "baseline":
        return baseline(args.remote, args.strict)
    return gate(args.base, args.head, json.loads(args.labels))


if __name__ == "__main__":
    sys.exit(main())
