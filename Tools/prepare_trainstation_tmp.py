"""Install the project's bundled TMP Essential Resources without a GUI import dialog."""
from pathlib import Path
import sys
import tarfile

project = Path(__file__).resolve().parents[1]
package = next((project / "Library/PackageCache").glob("com.unity.ugui@*/Package Resources/TMP Essential Resources.unitypackage"))
destinations = [project] + [Path(value).resolve() for value in sys.argv[1:]]
with tarfile.open(package, "r:gz") as archive:
    members = {item.name: item for item in archive.getmembers()}
    count = 0
    for name, item in members.items():
        if not name.endswith("/pathname"):
            continue
        path = archive.extractfile(item).read().decode("utf-8").strip()
        if not path.startswith("Assets/TextMesh Pro") or ".." in Path(path).parts:
            raise ValueError(f"Unexpected package path: {path}")
        prefix = name.rsplit("/", 1)[0]
        for destination in destinations:
            target = destination / path
            asset = members.get(prefix + "/asset")
            if asset:
                target.parent.mkdir(parents=True, exist_ok=True)
                if not target.exists():
                    target.write_bytes(archive.extractfile(asset).read())
            else:
                target.mkdir(parents=True, exist_ok=True)
            meta = members.get(prefix + "/asset.meta")
            if meta and not Path(str(target) + ".meta").exists():
                Path(str(target) + ".meta").write_bytes(archive.extractfile(meta).read())
        count += 1
print(f"Prepared {count} bundled TMP assets in {len(destinations)} project(s). Existing files preserved.")
