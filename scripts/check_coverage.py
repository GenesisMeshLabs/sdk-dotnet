"""Coverage floor: fail when the SDK's coverage drops below the floors CI sets.

CI runs the tests with ``--collect "XPlat Code Coverage"`` (coverlet), which
writes one Cobertura report per target framework, and then:

    python3 scripts/check_coverage.py RESULTS_DIR --project tests/GenesisMesh.Sdk.Tests/GenesisMesh.Sdk.Tests.csproj \\
        --line 90 --branch 85 --file OfflineVerifier.cs=90 --file StrictJson.cs=95

It prints line and branch coverage of every report and of every source file,
and fails unless there is one report per target framework of the test
project and each one meets every floor: ``--line`` and ``--branch`` for the
whole SDK, ``--file NAME=PERCENT`` for the line coverage of one source file
(by file name, e.g. ``Canonical.cs`` or ``Clients/BoundaryClient.cs``).

Floors only go up: when coverage rises, raise the floor in ci.yml to the new
value rounded down to a whole percent.
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

CONDITION = re.compile(r"\((\d+)/(\d+)\)")


def frameworks(project: Path) -> list[str]:
    text = project.read_text(encoding="utf-8")
    found = re.search(r"<TargetFrameworks?>([^<]+)</TargetFrameworks?>", text)
    if not found:
        raise SystemExit(f"{project}: no <TargetFramework(s)>")
    return [f.strip() for f in found.group(1).split(";") if f.strip()]


class Counts:
    def __init__(self) -> None:
        self.lines: dict[int, bool] = {}
        self.branches: dict[int, tuple[int, int]] = {}

    def add(self, number: int, hit: bool, branch: tuple[int, int] | None) -> None:
        # Compiler-generated classes repeat lines of their file: a line is covered when any copy is.
        self.lines[number] = self.lines.get(number, False) or hit
        if branch:
            covered, total = self.branches.get(number, (0, 0))
            self.branches[number] = (max(covered, branch[0]), max(total, branch[1]))

    def line_rate(self) -> float:
        return 100.0 * sum(self.lines.values()) / len(self.lines) if self.lines else 100.0

    def branch_rate(self) -> float:
        covered = sum(c for c, _ in self.branches.values())
        total = sum(t for _, t in self.branches.values())
        return 100.0 * covered / total if total else 100.0


def read(report: Path) -> dict[str, Counts]:
    """Counts per source file, by its path relative to the project (forward slashes)."""
    files: dict[str, Counts] = {}
    root = ET.parse(report).getroot()
    for cls in root.iter("class"):
        name = cls.get("filename", "").replace("\\", "/")
        name = name.split("src/GenesisMesh.Sdk/", 1)[-1]
        counts = files.setdefault(name, Counts())
        for line in cls.iter("line"):
            branch = None
            if line.get("branch") == "True":
                found = CONDITION.search(line.get("condition-coverage", ""))
                if found:
                    branch = (int(found.group(1)), int(found.group(2)))
            counts.add(int(line.get("number", "0")), int(line.get("hits", "0")) > 0, branch)
    return files


def total(files: dict[str, Counts]) -> Counts:
    whole = Counts()
    for index, counts in enumerate(files.values()):
        for number, hit in counts.lines.items():
            whole.lines[(index << 20) + number] = hit
        for number, branch in counts.branches.items():
            whole.branches[(index << 20) + number] = branch
    return whole


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("results", type=Path)
    parser.add_argument("--project", type=Path, required=True)
    parser.add_argument("--line", type=float, required=True, help="line coverage floor of the whole SDK (percent)")
    parser.add_argument("--branch", type=float, required=True, help="branch coverage floor of the whole SDK (percent)")
    parser.add_argument("--file", action="append", default=[], metavar="NAME=PERCENT",
                        help="line coverage floor of one source file")
    args = parser.parse_args()

    floors: dict[str, float] = {}
    for item in args.file:
        name, _, percent = item.partition("=")
        floors[name] = float(percent)

    # The TRX logger also copies each report next to its TRX file (<run>/In/<machine>/): count each run once.
    reports = sorted(r for r in args.results.rglob("coverage.cobertura.xml")
                     if "In" not in r.relative_to(args.results).parts)
    expected = frameworks(args.project)
    problems: list[str] = []
    if len(reports) != len(expected):
        problems.append(f"{len(reports)} coverage reports under {args.results}, expected one per target framework "
                        f"({', '.join(expected)})")

    for report in reports:
        files = read(report)
        whole = total(files)
        print(f"{report}: lines {whole.line_rate():.2f}% (floor {args.line:g}), "
              f"branches {whole.branch_rate():.2f}% (floor {args.branch:g})")
        for name in sorted(files):
            counts = files[name]
            floor = f" (floor {floors[name]:g})" if name in floors else ""
            print(f"  {name}: lines {counts.line_rate():.2f}%{floor}, branches {counts.branch_rate():.2f}%")
        if whole.line_rate() < args.line:
            problems.append(f"{report}: line coverage {whole.line_rate():.2f}% is below the floor {args.line:g}%")
        if whole.branch_rate() < args.branch:
            problems.append(f"{report}: branch coverage {whole.branch_rate():.2f}% is below the floor {args.branch:g}%")
        for name, floor in floors.items():
            if name not in files:
                problems.append(f"{report}: no coverage for {name} (renamed? update the floor in ci.yml)")
            elif files[name].line_rate() < floor:
                problems.append(f"{report}: {name} line coverage {files[name].line_rate():.2f}% is below the floor {floor:g}%")

    for problem in problems:
        print(f"::error::{problem}")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
