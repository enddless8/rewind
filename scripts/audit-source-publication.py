"""Read-only audit of objects reachable from one explicit publication ref.

Run: python scripts/audit-source-publication.py main artifacts/publication-audit.json
This does not approve asset rights, modify refs, or publish anything.
"""
import hashlib
import json
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def git(*args):
    return subprocess.check_output(
        ["git", "-c", f"safe.directory={ROOT.as_posix()}", *args], cwd=ROOT
    ).decode("utf-8").strip()


def audit(ref):
    objects = {}
    for line in git("rev-list", "--objects", ref).splitlines():
        sha, _, path = line.partition(" ")
        objects[sha] = path
    result = subprocess.run(
        ["git", "-c", f"safe.directory={ROOT.as_posix()}", "cat-file", "--batch"],
        cwd=ROOT, input=("\n".join(objects) + "\n").encode(), capture_output=True, check=True,
    ).stdout
    position = 0
    fonts, blocked, credentials = [], [], []
    allowed_fonts = {hashlib.sha256(p.read_bytes()).hexdigest() for p in (ROOT / "assets/fonts").glob("*.ttf")}
    signature = re.compile(rb"(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AKIA[A-Z0-9]{16}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|sk-(?:proj-)?[A-Za-z0-9_-]{40,})")
    for sha, path in objects.items():
        end = result.index(b"\n", position)
        header = result[position:end].split()
        size = int(header[2])
        data = result[end + 1:end + 1 + size]
        position = end + size + 2
        if header[1] != b"blob":
            continue
        if data[:4] in (b"\0\1\0\0", b"OTTO", b"true", b"ttcf", b"wOFF", b"wOF2"):
            digest = hashlib.sha256(data).hexdigest()
            coolvetica = b"coolvetica" in data.lower() or "coolvetica".encode("utf-16-be") in data.lower()
            fonts.append({"object": sha, "path": path, "sha256": digest, "coolvetica": coolvetica})
            if coolvetica or digest not in allowed_fonts:
                blocked.append({"object": sha, "path": path, "reason": "unexpected or Coolvetica font binary"})
        if re.search(r"(^|/)(?:\.tools|artifacts|bin|obj|publish|dist|recordings|captures|runtime|\.aws|\.ssh)(?:/|$)|\.(?:exe|dll|pfx|p12|pem|key)$|(?:^|/)(?:settings|compression-queue)\.json$", path, re.I):
            blocked.append({"object": sha, "path": path, "reason": "generated/runtime/binary/credential path"})
        if signature.search(data):
            credentials.append({"object": sha, "path": path})  # Never print matched values.
    return {"ref": ref, "head": git("rev-parse", ref), "commits": int(git("rev-list", "--count", ref)),
            "objects": len(objects), "fonts": fonts, "blocked": blocked, "credential_pattern_matches": credentials,
            "passed_object_checks": not blocked and not credentials,
            "refs": git("for-each-ref", "--format=%(refname) %(objectname)").splitlines(),
            "remotes": git("remote", "-v").splitlines(),
            "rights_review": "Separate graphics provenance/redistribution review is still required."}


if __name__ == "__main__":
    report = audit(sys.argv[1])
    output = Path(sys.argv[2]).resolve()
    if not output.is_relative_to(ROOT / "artifacts"):
        raise ValueError("Write audit reports only inside artifacts")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({k: report[k] for k in ("ref", "head", "commits", "objects", "passed_object_checks", "blocked", "credential_pattern_matches")}))
    sys.exit(0 if report["passed_object_checks"] else 1)
