"""Merge Cobertura reports by source line and reject missing required modules."""
import argparse
import collections
import json
import math
from pathlib import Path
import sys
import xml.etree.ElementTree as ET


def collect(paths):
    modules = collections.defaultdict(lambda: collections.defaultdict(dict))
    for path in paths:
        root = ET.parse(path).getroot()
        if root.tag != "coverage":
            raise ValueError(f"Not a Cobertura report: {path}")
        sources = [item.text or "" for item in root.findall("./sources/source")]
        for package in root.findall("./packages/package"):
            # The native shared library's ABI filename deliberately has no dot.
            # Its source directory and report identity still use the project name.
            module = {"SpaceTangentNative": "SpaceTangent.Native"}.get(package.attrib["name"], package.attrib["name"])
            if not module.startswith("SpaceTangent.") or module.endswith(".Tests"):
                continue
            marker = "bindings/unity/" if module == "SpaceTangent.UnityBinding" else module + "/"
            for item in package.findall("./classes/class"):
                source = item.attrib["filename"].replace("\\", "/")
                candidates = [source] + [(prefix + "/" + source).replace("\\", "/") for prefix in sources]
                source = next((value for value in candidates if marker in value), "")
                if not source:
                    raise ValueError(f"Source mapping unavailable for {module}: {item.attrib['filename']}")
                source = module + "/" + source.split(marker, 1)[1]
                parts = source.split("/")
                if any(part in {"obj", "bin", ".godot", "tests"} for part in parts) or source.endswith(".g.cs"):
                    continue
                lines = modules[module][source]
                for line in item.findall("./lines/line"):
                    number, hits = int(line.attrib["number"]), int(line.attrib["hits"])
                    if number < 1 or hits < 0:
                        raise ValueError(f"Invalid source hit in {path}")
                    lines[number] = lines.get(number, False) or hits > 0
    return modules


def summarize(modules, required=()):
    result, files = {}, []
    for module, sources in sorted(modules.items()):
        valid = sum(len(lines) for lines in sources.values())
        if not valid:
            continue
        covered = sum(sum(lines.values()) for lines in sources.values())
        result[module] = {"covered": covered, "executable": valid, "percent": 100 * covered / valid,
                          "additional_for_95": max(0, math.ceil(.95 * valid) - covered)}
        for source, lines in sorted(sources.items()):
            files.append({"file": source, "covered": sum(lines.values()), "executable": len(lines),
                          "uncovered_lines": [number for number, hit in sorted(lines.items()) if not hit]})
    for name in required:
        if name not in result or result[name]["covered"] == 0:
            raise ValueError(f"Required module is absent, empty or has no recorded hits: {name}")
    if not result:
        raise ValueError("No production source lines were measured.")
    covered, valid = sum(x["covered"] for x in result.values()), sum(x["executable"] for x in result.values())
    return {"modules": result, "total": {"covered": covered, "executable": valid, "percent": 100 * covered / valid},
            "files": sorted(files, key=lambda x: len(x["uncovered_lines"]), reverse=True)}


def enforce_minimum(result, thresholds):
    seen = set()
    for value in thresholds:
        name, separator, number = value.partition("=")
        if not separator or name in seen:
            raise ValueError("Coverage minimum must be a unique MODULE=PERCENT pair.")
        minimum = float(number)
        if not math.isfinite(minimum) or not 0 <= minimum <= 100:
            raise ValueError("Coverage minimum must be finite and between 0 and 100.")
        seen.add(name)
        actual = result["modules"].get(name)
        if actual is None or actual["percent"] < minimum:
            raise ValueError(f"Coverage minimum not met: {name} requires {minimum:g}%.")


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("reports", nargs="+")
    parser.add_argument("--require", action="append", default=[])
    parser.add_argument("--minimum", action="append", default=[], metavar="MODULE=PERCENT")
    parser.add_argument("--output", required=True)
    args = parser.parse_args(argv)
    try:
        result = summarize(collect(args.reports), args.require)
        enforce_minimum(result, args.minimum)
        output = Path(args.output)
        output.parent.mkdir(parents=True, exist_ok=True)
        output.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")
        for module, item in result["modules"].items():
            print(f"{module}: {item['covered']}/{item['executable']} lines ({item['percent']:.2f}%)")
        print("Only the listed modules are measured; shader execution is outside C# line coverage.")
        return 0
    except (OSError, ValueError, KeyError, ET.ParseError) as error:
        print(f"Coverage report rejected: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
