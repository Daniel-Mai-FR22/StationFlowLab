"""Check that serialized TrainStation references resolve in the delivered project."""
from pathlib import Path
import json
import re

project = Path(__file__).resolve().parents[1]
guid_pattern = re.compile(r"guid: ([a-f0-9]{32})")
refs = set()
for asset in (project / "Assets/TrainStation").rglob("*"):
    if asset.suffix in {".unity", ".mat", ".asset"}:
        refs.update(guid_pattern.findall(asset.read_text(encoding="utf-8", errors="ignore")))
refs = {guid for guid in refs if not guid.startswith("0000000000000000")}
resolved = {}
for folder in (project / "Assets", project / "Library/PackageCache"):
    for meta in folder.rglob("*.meta"):
        try:
            match = guid_pattern.search(meta.read_text(encoding="utf-8", errors="ignore")[:1024])
        except OSError:
            continue
        if match and match[1] in refs:
            resolved[match[1]] = str(meta.relative_to(project)).replace("\\", "/")[:-5]
result = {"referencedAssetGuids": len(refs), "resolvedAssets": len(resolved), "missingGuids": sorted(refs - resolved.keys())}
(project / "Reports/TrainStation/delivered-references.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps(result))
if result["missingGuids"]:
    raise SystemExit(1)
