"""Create a portable local handoff; does not publish or transmit the archive."""
import hashlib
import json
from pathlib import Path
import zipfile

root = Path(__file__).resolve().parent.parent
out = root / "Delivery"
out.mkdir(exist_ok=True)
target = out / "StationFlow-JunctionLab-Demo.zip"
entries = []
for path in (root / "Builds/JunctionLab").rglob("*"):
    if path.is_file() and "DoNotShip" not in str(path) and "Presentation" not in path.parts:
        entries.append((path, "JunctionLab/" + path.relative_to(root / "Builds/JunctionLab").as_posix()))
for path in (root / "Documentation").glob("*"):
    if path.is_file(): entries.append((path, "Documentation/" + path.name))
for filename in ("JunctionLab-comparison.mp4", "JunctionLab-pinhole.mp4", "JunctionLab-fisheye.mp4", "JunctionLab.png", "video-manifest.json"):
    entries.append((root / "Presentation" / filename, "Presentation/" + filename))
for filename in ("Acceptance.vi.md", "build-report.json", "capture-validation.json", "video-validation.json", "simulation-tests.txt", "label-overlay.png", "contact-sheet.png"):
    entries.append((root / "Reports" / filename, "Reports/" + filename))
readme = """STATIONFLOW — JUNCTION LAB

Giải nén toàn bộ ZIP, mở JunctionLab/JunctionLab.exe trên Windows 64-bit.
Không tách file EXE khỏi thư mục dữ liệu và DLL cạnh nó.

Video trình bày: Presentation/JunctionLab-comparison.mp4 (90 giây).
Hướng dẫn: Documentation/Quickstart.vi.md.
Phân tích, kỹ năng và quy trình: Documentation/Requirements-and-Evaluation.vi.md.
Kết quả và giới hạn: Reports/Acceptance.vi.md.

Space: pause, 1/2/3: camera, R: ghi/dừng, F1: ẩn HUD, F12: ảnh, Esc: thoát.
Ghi dữ liệu mới: nút RECORD SEQUENCE, rồi STOP & SAVE. Các file nằm trong Captures cạnh EXE.
PNG gốc 900 cặp và mã nguồn Unity nằm trong workspace bàn giao riêng; không kèm ZIP trình diễn này.

Đây là demo kỹ thuật, chưa đạt nhân vật/animation photoreal hoặc nghiệm thu huấn luyện trên dữ liệu thật.
"""
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
    archive.writestr("START-HERE.vi.txt", readme)
    for source, destination in entries:
        archive.write(source, destination)
with zipfile.ZipFile(target) as archive:
    assert "JunctionLab/JunctionLab.exe" in archive.namelist()
    assert archive.testzip() is None
manifest = {"archive": target.name, "bytes": target.stat().st_size, "sha256": hashlib.sha256(target.read_bytes()).hexdigest(), "files": len(entries)+1, "crc_check": "PASS", "contains": "Portable Windows player, Vietnamese documentation, reports, 3 MP4s; raw PNG data and Unity source remain in workspace."}
(out / "release-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print(json.dumps(manifest, indent=2))
