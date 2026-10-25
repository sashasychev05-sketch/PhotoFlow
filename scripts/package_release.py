"""Package an existing Windows publish directory, without symbols or runtime user data."""
from pathlib import Path
import argparse
import hashlib
import zipfile

parser=argparse.ArgumentParser()
parser.add_argument("--publish-dir",required=True,type=Path)
parser.add_argument("--output",required=True,type=Path)
args=parser.parse_args()
root=Path(__file__).resolve().parents[1]
publish=args.publish_dir.resolve()
if not (publish/"PhotoFlow.App.exe").is_file(): raise SystemExit("PhotoFlow.App.exe is missing")
private_prefixes=[]
for value in (str(Path.home()),str(root)):
    for variant in (value.replace("\\","/"),value.replace("/","\\")):
        private_prefixes += [variant.encode("utf-8").lower(),variant.encode("utf-16le").lower()]
entries=[]
for file in publish.rglob("*"):
    if not file.is_file(): continue
    if file.suffix.lower() in {".pdb",".log",".db",".ndjson",".dmp",".part"} or file.name.startswith("private-"): continue
    data=file.read_bytes()
    if file.name.startswith("PhotoFlow") or file.suffix.lower() in {".xbf",".pri",".json"}:
        if any(prefix in data.lower() for prefix in private_prefixes): raise SystemExit(f"Private build path found in {file.name}")
    entries.append((file,Path("PhotoFlow")/file.relative_to(publish)))
for name in ("LICENSE","THIRD_PARTY_NOTICES.md"):
    entries.append((root/name,Path("PhotoFlow")/name))
for directory in ("third_party","docs"):
    for file in (root/directory).rglob("*"):
        if file.is_file(): entries.append((file,Path("PhotoFlow")/file.relative_to(root)))
entries.append((root/"docs"/"GETTING_STARTED.md",Path("PhotoFlow")/"НАЧАТЬ.md"))
args.output.parent.mkdir(parents=True,exist_ok=True)
with zipfile.ZipFile(args.output,"w",zipfile.ZIP_DEFLATED,compresslevel=9) as archive:
    for file,name in sorted(entries,key=lambda entry:str(entry[1])): archive.write(file,name.as_posix())
with zipfile.ZipFile(args.output) as archive:
    if archive.testzip() is not None: raise SystemExit("ZIP integrity check failed")
digest=hashlib.sha256(args.output.read_bytes()).hexdigest()
args.output.with_suffix(".sha256").write_text(f"{digest}  {args.output.name}\n",encoding="utf-8")
print(f"Packaged {len(entries)} files: {args.output.name}; SHA-256 {digest}")
