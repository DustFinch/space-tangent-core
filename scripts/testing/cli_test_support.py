"""Preserve the CLI's real exit code and streams during coverlet instrumentation."""
import json
import os
from pathlib import Path
import shlex
import shutil
import subprocess
import sys


def run_cli(tool, arguments, case, sequence, module):
    command = ["dotnet", str(tool), *map(str, arguments)]
    coverlet = os.environ.get("ST_CLI_COVERLET")
    if not coverlet:
        return subprocess.run(command, capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=90)
    directory = Path(os.environ["ST_CLI_COVERAGE_DIRECTORY"]) / f"{case}-{sequence}"
    directory.mkdir(parents=True, exist_ok=False)
    shutil.copytree(tool.parent, directory / "bin")
    local = directory / "bin" / tool.name
    target = [str(Path(__file__).resolve()), str(directory / "result.json"), "dotnet", str(local), *map(str, arguments)]
    quoted = subprocess.list2cmdline(target) if os.name == "nt" else shlex.join(target)
    runner = subprocess.run([coverlet, str(local), "--include-test-assembly", "--target", sys.executable,
        "--targetargs", quoted, "--include", f"[{module}]*", "--exclude-by-file", "**/obj/**",
        "--format", "cobertura", "--output", str(directory / "coverage.xml")],
        capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120)
    if runner.returncode != 0 or not (directory / "coverage.xml").is_file():
        raise RuntimeError("CLI coverage failed: " + runner.stdout + runner.stderr)
    result = json.loads((directory / "result.json").read_text(encoding="utf-8"))
    return subprocess.CompletedProcess(command, result["returncode"], result["stdout"], result["stderr"])


if __name__ == "__main__":
    # Expected command failures still let coverlet finalize. The outer acceptance
    # test checks the actual captured status and stderr, never coverlet's normalized status.
    result = subprocess.run(sys.argv[2:], capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=90)
    Path(sys.argv[1]).write_text(json.dumps({"returncode": result.returncode,
        "stdout": result.stdout, "stderr": result.stderr}), encoding="utf-8")
