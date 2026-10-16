"""Skipped is not green: check that a ``dotnet test`` run really ran its tests.

``dotnet test`` exits 0 when a filter matches no test, and xUnit reports a
skipped test without failing. CI writes TRX files (``--logger trx``) and this
script reads them:

    python3 scripts/check_trx.py RESULTS_DIR --project tests/GenesisMesh.Sdk.Tests/GenesisMesh.Sdk.Tests.csproj --min-tests 100
    python3 scripts/check_trx.py RESULTS_DIR --project ... --only-test EveryCallReturnsWhatTheNASent

It fails unless there is one TRX file per target framework of the test
project, each with at least ``--min-tests`` tests, every one of them run and
passed (none failed, skipped or not executed). ``--only-test NAME`` also
requires that NAME is the only test in each run.
"""

from __future__ import annotations

import argparse
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

NS = {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}


def frameworks(project: Path) -> list[str]:
    text = project.read_text(encoding="utf-8")
    found = re.search(r"<TargetFrameworks?>([^<]+)</TargetFrameworks?>", text)
    if not found:
        raise SystemExit(f"{project}: no <TargetFramework(s)>")
    return [f.strip() for f in found.group(1).split(";") if f.strip()]


def framework_of(trx: Path, root: ET.Element, names: list[str]) -> str | None:
    """The target framework a TRX ran: from the test assembly's path (bin/<config>/<framework>/), or, for a run
    without tests, from the file name dotnet test gives it (..._<framework>.trx)."""
    for method in root.iterfind(".//t:TestDefinitions/t:UnitTest/t:TestMethod", NS):
        parts = re.split(r"[\\/]", method.get("codeBase", ""))
        for name in names:
            if name in parts:
                return name
    for name in names:
        if trx.name.endswith(f"_{name}.trx"):
            return name
    return None


def check(results: Path, project: Path, min_tests: int, only_test: str | None) -> list[str]:
    expected = frameworks(project)
    problems: list[str] = []
    seen: dict[str, Path] = {}
    files = sorted(results.glob("**/*.trx"))
    if not files:
        return [f"no TRX file under {results}: did any test run?"]
    for trx in files:
        root = ET.parse(trx).getroot()
        framework = framework_of(trx, root, expected)
        label = f"{trx.name} ({framework or 'framework unknown'})"
        if framework is None:
            problems.append(f"{label}: no test of a target framework of {project.name} ({', '.join(expected)})")
            continue
        if framework in seen:
            problems.append(f"{label}: a second run for {framework} (also {seen[framework].name})")
        seen[framework] = trx
        counters = root.find(".//t:ResultSummary/t:Counters", NS)
        if counters is None:
            problems.append(f"{label}: no result counters")
            continue
        count = {k: int(v) for k, v in counters.attrib.items() if v.isdigit()}
        total, executed, passed = count.get("total", 0), count.get("executed", 0), count.get("passed", 0)
        print(f"{label}: total={total} executed={executed} passed={passed} "
              f"failed={count.get('failed', 0)} notExecuted={count.get('notExecuted', 0)}")
        if total < min_tests:
            problems.append(f"{label}: {total} tests, expected at least {min_tests} (a filter that matches nothing?)")
        if not (total == executed == passed):
            problems.append(f"{label}: {total} tests but {executed} run and {passed} passed: "
                            "every test must run and pass (skipped is not green)")
        results_ = list(root.iterfind(".//t:Results/t:UnitTestResult", NS))
        not_passed = [r.get("testName", "?") for r in results_ if r.get("outcome") != "Passed"]
        if not_passed:
            problems.append(f"{label}: {len(not_passed)} not passed: {', '.join(not_passed[:10])}")
        if only_test is not None:
            names = [r.get("testName", "") for r in results_]
            if len(names) != 1 or not names[0].endswith("." + only_test):
                ran = ", ".join(names[:5]) + (f" and {len(names) - 5} more" if len(names) > 5 else "") or "none"
                problems.append(f"{label}: expected exactly the test {only_test}, ran {ran}")
    missing = [f for f in expected if f not in seen]
    if missing:
        problems.append(f"no test run for {', '.join(missing)} (target frameworks of {project.name}: {', '.join(expected)})")
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("results", type=Path, help="the --results-directory of dotnet test")
    parser.add_argument("--project", type=Path, required=True, help="the test project (its target frameworks)")
    parser.add_argument("--min-tests", type=int, default=1)
    parser.add_argument("--only-test", help="the one test each run must consist of")
    args = parser.parse_args()
    problems = check(args.results, args.project, args.min_tests, args.only_test)
    for problem in problems:
        print(f"::error::{problem}")
    if not problems:
        print("Every target framework ran its tests, and every test passed.")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
