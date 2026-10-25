"""Check the proposed public tree for local paths, private diagnostics and credential patterns."""
from pathlib import Path
import re
import subprocess
import sys

root = Path(__file__).resolve().parents[1]
excluded = {"bin", "obj", ".git", "work", "outputs", "artifacts", ".vs", "TestResults"}
result = subprocess.run(["git", "ls-files", "-z", "--cached", "--others", "--exclude-standard"], cwd=root, capture_output=True)
paths = [root / path for path in result.stdout.decode("utf-8").split("\0") if path] if result.returncode == 0 else list(root.rglob("*"))
violations = []
for path in paths:
    if not path.is_file() or any(part in excluded for part in path.relative_to(root).parts):
        continue
    relative = path.relative_to(root).as_posix()
    if path.suffix.lower() in {".db", ".log", ".ndjson", ".pdb", ".dmp", ".pfx", ".pem", ".key"} or path.name.startswith(("private-", ".env")):
        violations.append((relative, "private runtime file"))
    data = path.read_bytes()
    if path.suffix.lower() in {".jpg", ".jpeg"}:
        if b"Exif\0\0" in data: violations.append((relative, "EXIF-bearing image; review before publication"))
        continue
    if path.suffix.lower() in {".png", ".ico"}:
        continue
    try: text = data.decode("utf-8-sig")
    except UnicodeDecodeError:
        violations.append((relative, "unexpected binary")); continue
    patterns = {
        "absolute user path": r"(?i)[A-Z]:[\\/]+Users[\\/]+[A-Za-z0-9]",
        "credential-like token": r"\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{30,})\b",
        "device identifier": r"(?i)usb[#\\]vid_[a-f0-9]{4}",
    }
    for label, pattern in patterns.items():
        if re.search(pattern, text): violations.append((relative, label))
    if not relative.startswith("third_party/") and re.search(r"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}", text):
        violations.append((relative, "email address"))
if violations:
    for relative, reason in violations: print(f"BLOCKED {relative}: {reason}")
    sys.exit(1)
print("Public tree check passed: no matched private paths, diagnostics, device identifiers or credentials.")
