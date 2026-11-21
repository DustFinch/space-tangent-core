"""Record and verify an optional local test corpus without publishing its assets."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def inventory(path):
    if path.is_symlink():
        raise ValueError(f"Test input must not be a symbolic link: {path}")
    if path.is_file():
        return "file", {".": digest(path)}
    if not path.is_dir():
        raise ValueError(f"Test input is missing: {path}")
    files = {}
    for child in sorted(path.rglob("*")):
        if child.is_symlink():
            raise ValueError(f"Test input contains a symbolic link: {child}")
        if child.is_file():
            files[child.relative_to(path).as_posix()] = digest(child)
    if not files:
        raise ValueError(f"Test input directory is empty: {path}")
    return "directory", files


def variable(name):
    if not isinstance(name, str) or not re.fullmatch(r"SPACETANGENT_[A-Z0-9_]+", name):
        raise ValueError("Only SPACETANGENT_ test-data variables are accepted.")
    return name


def capture(bindings, pathlists=()):
    result = {}
    checked = {}
    for entry, multiple in [(entry, False) for entry in bindings] + [(entry, True) for entry in pathlists]:
        name, separator, value = entry.partition("=")
        variable(name)
        if not separator or not value or name in result:
            raise ValueError(f"Invalid or duplicate binding: {name}")
        inputs = value.split(os.pathsep) if multiple else [value]
        if any(not item for item in inputs):
            raise ValueError(f"Empty input in path list: {name}")
        records = []
        for item in inputs:
            path = Path(item).resolve()
            if path not in checked:
                checked[path] = inventory(path)
            kind, files = checked[path]
            records.append({"path": str(path), "kind": kind, "files": files})
        result[name] = {"kind": "path-list", "paths": records} if multiple else records[0]
    if not result:
        raise ValueError("A data profile needs at least one binding.")
    return {"version": 1, "bindings": result}


def load(path):
    profile = json.loads(Path(path).read_text(encoding="utf-8-sig"))
    if not isinstance(profile, dict) or type(profile.get("version")) is not int or profile["version"] != 1:
        raise ValueError("Unsupported data profile version.")
    bindings = profile.get("bindings")
    if not isinstance(bindings, dict) or not bindings:
        raise ValueError("A data profile needs nonempty bindings.")
    for name, binding in bindings.items():
        variable(name)
        if not isinstance(binding, dict):
            raise ValueError(f"Invalid binding {name}.")
        records = binding.get("paths") if binding.get("kind") == "path-list" else [binding]
        if not isinstance(records, list) or not records or any(not isinstance(item, dict) for item in records):
            raise ValueError(f"Binding {name} needs nonempty input paths.")
        for record in records:
            validate_record(name, record)
    return profile


def validate_record(name, binding):
    if not isinstance(binding.get("path"), str) or not Path(binding["path"]).is_absolute():
        raise ValueError(f"Binding {name} needs an absolute local path.")
    if binding.get("kind") not in ("file", "directory"):
        raise ValueError(f"Unknown input kind for {name}.")
    files = binding.get("files")
    if not isinstance(files, dict) or not files:
        raise ValueError(f"Binding {name} needs pinned file hashes.")
    if any(not isinstance(key, str) or not isinstance(value, str) or
           not re.fullmatch(r"[0-9a-f]{64}", value) for key, value in files.items()):
        raise ValueError(f"Invalid file hash for {name}.")


def verify(profile, required=()):
    bindings = profile["bindings"]
    missing = set(required) - bindings.keys()
    if missing:
        raise ValueError("Required bindings missing: " + ", ".join(sorted(missing)))
    values = {}
    # A repeated root is hashed once even when tests use distinct variable names.
    checked = {}
    for name, binding in bindings.items():
        records = binding["paths"] if binding["kind"] == "path-list" else [binding]
        paths = []
        for record in records:
            path = Path(record["path"])
            if path not in checked:
                checked[path] = inventory(path)
            kind, actual = checked[path]
            if kind != record["kind"] or actual != record["files"]:
                raise ValueError(f"Test data changed for {name}; restore the pinned corpus before running tests.")
            paths.append(str(path))
        values[name] = os.pathsep.join(paths)
    return values


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    create = commands.add_parser("capture", help="Record local file hashes; review provenance before accepting the profile.")
    create.add_argument("--bind", action="append", default=[], metavar="VARIABLE=PATH")
    create.add_argument("--bind-list", action="append", default=[], metavar="VARIABLE=PATH_LIST")
    create.add_argument("--output", required=True)
    check = commands.add_parser("verify")
    check.add_argument("profile")
    check.add_argument("--require", action="append", default=[])
    run = commands.add_parser("run")
    run.add_argument("--profile", required=True)
    run.add_argument("--require", action="append", default=[])
    run.add_argument("program", nargs=argparse.REMAINDER)
    args = parser.parse_args(argv)
    try:
        if args.command == "capture":
            profile = capture(args.bind, args.bind_list)
            output = Path(args.output)
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(json.dumps(profile, indent=2) + "\n", encoding="utf-8")
            print(f"Recorded {len(profile['bindings'])} bindings. Review the source versions and hashes before use.")
            return 0
        values = verify(load(args.profile), args.require)
        print(f"Verified {len(values)} pinned data bindings.", flush=True)
        if args.command == "verify":
            return 0
        program = args.program[1:] if args.program[:1] == ["--"] else args.program
        if not program:
            raise ValueError("Supply the test command after --.")
        environment = {key: value for key, value in os.environ.items() if not key.upper().startswith("SPACETANGENT_")}
        environment.update(values)
        return subprocess.run(program, env=environment, check=False).returncode
    except (OSError, ValueError, KeyError) as error:
        print(f"Data profile rejected: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
