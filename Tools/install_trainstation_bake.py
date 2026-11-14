"""Copy verified Unity-authored scene assets from the isolated authoring project."""
from pathlib import Path
import json
import re
import shutil

project = Path(__file__).resolve().parents[1]
bake = project / "Temp/TrainStationBake"
report_dir = bake / "Reports/TrainStation"
validation = json.loads((report_dir / "validation.json").read_text(encoding="utf-8-sig"))
access = json.loads((report_dir / "access-check.json").read_text(encoding="utf-8-sig"))
if validation["errors"] or access["errors"]:
    raise RuntimeError("Only a validated scene can be installed")
for name in ("Materials", "Meshes", "Scenes"):
    source = bake / "Assets/TrainStation" / name
    target = project / "Assets/TrainStation" / name
    for asset in source.rglob("*"):
        destination = target / asset.relative_to(source)
        if asset.is_file() and destination.exists() and asset.read_bytes() != destination.read_bytes():
            raise RuntimeError(f"Preserving existing edited asset: {destination}. Use the Editor's separate-copy command instead.")
for name in ("Materials", "Meshes", "Scenes"):
    source = bake / "Assets/TrainStation" / name
    target = project / "Assets/TrainStation" / name
    shutil.copytree(source, target, dirs_exist_ok=True)
    meta = Path(str(source) + ".meta")
    if meta.exists():
        shutil.copy2(meta, Path(str(target) + ".meta"))
shutil.copytree(report_dir, project / "Reports/TrainStation", dirs_exist_ok=True)
meta_text = (project / "Assets/TrainStation/Scenes/TrainStation.unity.meta").read_text(encoding="utf-8")
guid = re.search(r"^guid: ([a-f0-9]+)$", meta_text, re.M)[1]
settings = project / "ProjectSettings/EditorBuildSettings.asset"
text = settings.read_text(encoding="utf-8-sig")
scene_path = "Assets/TrainStation/Scenes/TrainStation.unity"
if scene_path not in text:
    block = f"  - enabled: 1\n    path: {scene_path}\n    guid: {guid}\n"
    text = text.replace("  m_configObjects:", block + "  m_configObjects:")
    settings.write_text(text, encoding="utf-8")
print(json.dumps({"scene": scene_path, "objects": validation["objects"], "routes": access["authoredRoutes"], "samples": access["sampledPoints"]}, ensure_ascii=False))
