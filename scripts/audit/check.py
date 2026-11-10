"""Inspect the Git index, commit metadata, and objects proposed for publication."""

import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys


POLICY = "scripts/audit/allowlist.json"
CHANGED = ("--diff-filter=ACMRT", "--no-renames")


class AuditError(Exception):
    pass


def git(*args):
    result = subprocess.run(("git", *args), capture_output=True)
    if result.returncode:
        raise AuditError(f"Git {args[0]} failed.")
    return result.stdout


def entries(data):
    return (entry.decode("utf-8") for entry in data.split(b"\0") if entry)


def split_case(text):
    # Separate acronym and camel-case components without splitting ordinary words.
    result = []
    for index, char in enumerate(text):
        if index and char.isupper():
            previous = text[index - 1]
            next_is_lower = index + 1 < len(text) and text[index + 1].islower()
            if previous.islower() or (previous.isupper() and next_is_lower):
                result.append(" ")
        result.append(char)
    return "".join(result)


def decode(data):
    if data.startswith((b"\xff\xfe", b"\xfe\xff")):
        return data.decode("utf-16")
    if b"\0" in data:
        return None
    return data.decode("utf-8-sig", errors="replace")


class Scanner:
    def __init__(self):
        configured = os.environ.get("SPACE_TANGENT_STOPWORDS")
        path = Path(configured) if configured else Path.cwd().parent / ".stopwords"
        if not path.is_absolute() or path.resolve().is_relative_to(Path.cwd().resolve()):
            raise AuditError("The word list must have an absolute path outside this checkout.")
        try:
            lines = path.read_text(encoding="utf-8-sig").splitlines()
        except OSError:
            raise AuditError("Set up the external .stopwords file before using Git hooks.") from None

        patterns = set()
        for line in lines:
            term = line.strip()
            if not term or term.startswith("#") or term in ("[en]", "[ru]"):
                continue
            parts = re.split(r"[\s_-]+", split_case(term))
            phrase = r"[\W_]*".join(map(re.escape, parts))
            patterns.add(r"(?<![^\W_])" + phrase + r"(?![^\W_])")
        if not patterns:
            raise AuditError("The external word list is empty.")
        self.pattern = re.compile("|".join(sorted(patterns)), re.IGNORECASE)

    def content(self, text, location, allowed=()):
        for number, line in enumerate(text.splitlines(), 1):
            if self.pattern.search(split_case(line)):
                digest = hashlib.sha256(line.encode("utf-8")).hexdigest()
                if digest not in allowed:
                    raise AuditError(f"Restricted term in {location}, line {number}.")

    def path(self, path):
        if any(part.casefold() == ".stopwords" for part in path.split("/")):
            raise AuditError("A word-list file is tracked.")
        self.content(path, "a tracked path")

    def blobs(self, listing, changed):
        files = {}
        for row in entries(listing):
            info, path = row.split("\t", 1)
            mode, kind, oid = info.split()
            if mode != "160000":
                files[path] = oid if kind == "blob" else kind
        policy = {}
        if POLICY in files:
            rules = json.loads(git("cat-file", "blob", files[POLICY]))
            if not isinstance(rules, list):
                raise AuditError("The audit exception list must be an array.")
            for item in rules:
                if (not isinstance(item, dict)
                        or set(item) != {"path", "sha256", "reason"}
                        or not all(isinstance(value, str) and value.strip() for value in item.values())
                        or not re.fullmatch(r"[0-9a-f]{64}", item["sha256"])):
                    raise AuditError("Invalid audit exception.")
                policy.setdefault(item["path"], set()).add(item["sha256"])

        for path in entries(changed):
            self.path(path)
            if path in files:
                data = decode(git("cat-file", "blob", files[path]))
                if data is not None:
                    self.content(data, path, policy.get(path, ()))


def check_commit_message(scanner, message_path):
    for variable in ("GIT_AUTHOR_IDENT", "GIT_COMMITTER_IDENT"):
        actual = git("var", variable).decode("utf-8").strip()
        expected_name = git("config", "--local", "--get", "user.name").decode("utf-8").strip()
        expected_email = git("config", "--local", "--get", "user.email").decode("utf-8").strip()
        expected = f"{expected_name} <{expected_email}> "
        if not actual.startswith(expected) or not expected_email.lower().endswith("@users.noreply.github.com"):
            raise AuditError("Commit identity must match the locally configured public GitHub identity.")
        scanner.content(actual, "the commit identity")
    message = decode(Path(message_path).read_bytes())
    if message is None:
        raise AuditError("The commit message must be text.")
    scanner.content(message, "the commit message")


def check_push(scanner):
    inspected = set()
    for update in sys.stdin:
        local_ref, local_oid, remote_ref, remote_oid = update.split()
        if not local_oid.strip("0"):
            continue
        scanner.content(local_ref, "a local ref")
        scanner.content(remote_ref, "a remote ref")
        while git("cat-file", "-t", local_oid).strip() == b"tag":
            tag = git("cat-file", "tag", local_oid)
            scanner.content(tag.decode("utf-8", errors="replace"), "an annotated tag")
            local_oid = tag.splitlines()[0].split()[1].decode("ascii")
        if git("cat-file", "-t", local_oid).strip() != b"commit":
            raise AuditError("Only refs pointing to commits can be published.")

        base = []
        if remote_oid.strip("0"):
            exists = subprocess.run(("git", "cat-file", "-e", f"{remote_oid}^{{commit}}"),
                                    capture_output=True)
            if exists.returncode == 0:
                base = [f"^{remote_oid}"]
        for raw_commit in git("rev-list", local_oid, *base).splitlines():
            commit = raw_commit.decode("ascii")
            if commit in inspected:
                continue
            inspected.add(commit)
            metadata = git("show", "-s", "--format=%an%n%ae%n%cn%n%ce%n%B", commit)
            scanner.content(metadata.decode("utf-8", errors="replace"), f"commit {commit[:12]} metadata")
            scanner.blobs(
                git("ls-tree", "-r", "-z", commit),
                git("diff-tree", "--root", "-m", "--no-commit-id", "--name-only", "-r", "-z",
                    *CHANGED, commit),
            )


def main():
    scanner = Scanner()
    hook = sys.argv[1]
    if hook == "pre-commit":
        tree = git("write-tree").strip().decode("ascii")
        scanner.blobs(
            git("ls-tree", "-r", "-z", tree),
            git("diff", "--cached", "--name-only", "-z", *CHANGED),
        )
    elif hook == "commit-msg":
        check_commit_message(scanner, sys.argv[2])
    elif hook == "pre-push":
        check_push(scanner)
    else:
        raise AuditError("Unknown hook.")


if __name__ == "__main__":
    try:
        main()
    except AuditError as error:
        sys.exit(f"SpaceTangent audit: {error}")
    except (OSError, UnicodeError, ValueError, IndexError):
        sys.exit("SpaceTangent audit: unable to read or parse the audit inputs.")
