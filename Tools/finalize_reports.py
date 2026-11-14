"""Generate a measured handoff report and contact sheet after all capture checks pass."""
import hashlib
import json
import re
import subprocess
from pathlib import Path
from PIL import Image, ImageDraw, ImageFont

ROOT = Path(__file__).resolve().parent.parent


def read(path):
    return json.loads((ROOT / path).read_text(encoding="utf-8-sig"))


def main():
    build = read("Reports/build-report.json")
    validation = read("Reports/capture-validation.json")
    run = read("Captures/Presentation/run-report.json")
    videos = read("Presentation/video-manifest.json")
    info = read("Captures/Presentation/build-info.json")
    assert build["result"] == "Succeeded" and build["errors"] == 0
    assert validation["result"] == "PASS" and validation["frames"] == run["frames"] == videos["frames"] == 900
    for item in info["sourceFiles"]:
        assert hashlib.sha256((ROOT / item["path"]).read_bytes()).hexdigest() == item["sha256"], item["path"]
    encoder = next((ROOT / "Tools/PythonPackages/imageio_ffmpeg/binaries").glob("ffmpeg*.exe"))
    decoded_videos = []
    for item in videos["videos"]:
        result = subprocess.run([str(encoder), "-hide_banner", "-loglevel", "error", "-i", item["path"], "-map", "0:v:0", "-progress", "pipe:1", "-f", "null", "-"], capture_output=True, text=True, check=True)
        counts = re.findall(r"^frame=(\d+)$", result.stdout, re.MULTILINE)
        assert counts and int(counts[-1]) == run["frames"], item["path"]
        decoded_videos.append({"file": Path(item["path"]).name, "decoded_frames": int(counts[-1]), "result": "PASS"})
    (ROOT / "Reports/video-validation.json").write_text(json.dumps(decoded_videos, indent=2), encoding="utf-8")
    font = ImageFont.truetype("C:/Windows/Fonts/segoeui.ttf", 17)
    font_bold = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 23)
    sheet = Image.new("RGB", (1016, 1140), (12, 22, 27))
    draw = ImageDraw.Draw(sheet)
    draw.text((20, 10), "JUNCTION LAB  /  FINAL CAPTURE CONTACT SHEET", font=font_bold, fill=(232, 243, 239))
    rows = [0, 200, 340, 720]
    for index, frame in enumerate(rows):
        y = 53 + index * 269
        draw.text((20, y), f"FRAME {frame:06d}  /  t = {40 + frame/10:.1f} s  |  RGB only, labels stored separately", font=font, fill=(112, 221, 197))
        pinhole = Image.open(ROOT / f"Captures/Presentation/pinhole/frame_{frame:06d}.png").convert("RGB")
        fisheye = Image.open(ROOT / f"Captures/Presentation/fisheye/frame_{frame:06d}.png").convert("RGB")
        pinhole.thumbnail((560, 232)); fisheye.thumbnail((232, 232))
        sheet.paste(pinhole, (20, y + 28)); sheet.paste(fisheye, (650, y + 28))
    sheet.save(ROOT / "Reports/contact-sheet.png")
    text = f"""# Báo cáo nghiệm thu — Junction Lab

Ngày 21/09/2026. Đạt phạm vi **demo kỹ thuật có dữ liệu mẫu**; chưa đạt nghiệm thu dữ liệu photoreal cho sản xuất/huấn luyện. Kết quả dưới đây được đo trên bản bàn giao, không phải mục tiêu dự kiến.

## Kết quả đã đo

| Hạng mục | Kết quả | Bằng chứng |
|---|---|---|
| Unity Windows build | {build['result']}, {build['errors']} lỗi, {build['warnings']} cảnh báo; Unity {run['unityVersion']} | `Reports/build-report.json`, `Reports/release-build.log` |
| GPU lấy ảnh | {run['graphicsDevice']}; Direct3D 11 | `Captures/Presentation/player.log` |
| Thời lượng mẫu | {run['frames']} cặp tại {run['fps']} fps; video {videos['duration_seconds']:.0f} giây; t={run['startSimulationTime']:.3f}…{run['endSimulationTime']:.3f} | `run-report.json`, `capture_summary.json` |
| Dân số | {run['people']} người, {run['vehicles']} xe trong mỗi frame | `frames.jsonl` |
| Tín hiệu | Đủ {len(validation['traffic_phase_frames'])}/9 pha; {run['redLightViolations']} vi phạm xe, {run['pedestrianRedLightViolations']} vi phạm người | `run-report.json`, `Reports/capture-validation.json` |
| Hành vi trong khoảng ghi | {run['completedCrossings']} lượt hoàn tất; {run['collisions']} sự kiện chồng hình kiểm tra XZ | `run-report.json` |
| Cấu trúc dữ liệu | Decode đủ {run['frames']*2} PNG; {validation['error_count']} lỗi; metadata, timestamp, ID, bounds và phép chiếu hợp lệ | `Reports/capture-validation.json` |
| Camera | 211 kiểm số học, 14 marker GPU; tâm, ±30°, ±65°, mask ngoài hình tròn đạt | `sensor-validation/sensor_projection_checks.txt` |
| Chuyển động | {validation['moving_tracks_by_category']['pedestrian']} track người và {validation['moving_tracks_by_category']['car']} track xe có dịch chuyển; ID xe được đổi khi tái xuất hiện | `Reports/capture-validation.json` |
| Tái lập và stress lõi | 4.086.453 assertions đạt; baseline 600 s, 5 seed × 900 s, mật độ tới 48 người/24 xe, tracking và timestep | `Reports/simulation-tests.txt` |
| Video | Cả 3 MP4 H.264 decode đủ {run['frames']} frame | `Reports/video-validation.json` |
| Phiên bản | {len(info['sourceFiles'])} hash SHA256 mã/shader khớp nguồn bàn giao | `Captures/Presentation/build-info.json` |

## Đối chiếu tiêu chí demo

**D01–D08, D10 đạt trong phạm vi các phép kiểm mô tả.** D02 bao phủ đủ chín pha và người/xe di chuyển. D03/D04 kiểm bằng hình chữ nhật xe, đĩa người và điều kiện pha trong lõi C#; không phải chứng nhận mesh collider hoặc mọi chướng ngại vật ở mọi mật độ. D06 áp dụng bounds dương khi có giao miền cảm biến, còn đối tượng ngoài ảnh có cờ riêng. D08 xác nhận trạng thái lõi lặp lại cùng seed/timestep; không cam kết RGB giống bit giữa các GPU/driver.

**D09 đạt kiểm tra ảnh cảm biến ở mức demo**, qua decode toàn bộ, marker, overlay và ảnh nhiều thời điểm. Không có ảnh cảm biến đen/trống hay vật liệu hồng trong bộ bàn giao. Hình ảnh vẫn lộ mô hình tạo bằng mã, lá cây hình học, nhân vật khớp đơn giản và aliasing. Ảnh `Presentation/JunctionLab.png` là UI khi chạy tương tác; contact sheet và overlay nằm trong `Reports`. Ảnh giao diện tự chụp khi cửa sổ ẩn có thể đen vì không có backbuffer hiển thị; vì vậy chúng không được dùng làm ảnh RGB hoặc bằng chứng nghiệm thu.

## Các tiêu chí sản xuất chưa đạt hoặc chưa đo

| Tiêu chí | Trạng thái và phần việc tiếp theo |
|---|---|
| P01 — camera thật | Chỉ kiểm mô hình equidistant lý tưởng; chưa có bảng hiệu chuẩn/ống kính mục tiêu, hệ số méo thực hay RMS trên 30 pose |
| P02 — mask / visible box | Chưa triển khai mask/độ che khuất; hộp hiện tại là AABB chiếu xấp xỉ, không có phép đo IoU với mask |
| P03 — hành vi tự nhiên | Gait tạo bằng mã; cần rig/retarget, blend, foot IK, rẽ và phản ứng xã hội, đánh giá clip bởi người |
| P04 — photoreal | Chưa đạt; cần nhân vật/quần áo/tóc/xe chất lượng cao với quyền tạo dữ liệu ML phù hợp |
| P05 — độ phủ | Một cảnh, ánh sáng cố định, xe đi thẳng; cần camera/thời tiết/mật độ/địa điểm độc lập và kiểm phân bố |
| P06 — hiệu năng realtime | Có chạy tương tác và ghi đầy đủ; chưa profile p95 1080p trong 5 phút. Tốc độ ghi offline không phải FPS realtime |
| P07 — stress sản xuất | Lõi có kiểm nhiều seed nhưng chưa đạt 20 seed × 30 phút cùng renderer và theo dõi RAM/VRAM |
| P08 — hiệu quả AI | Chưa huấn luyện hoặc so sánh trên video thật; cần baseline, synthetic-only, real-only, mixed và test theo clip/địa điểm độc lập |

## Lưu ý diễn giải

`in_sensor_fov` chỉ cho biết giao trường nhìn, không chứng minh đối tượng nhìn thấy qua các vật che. Phép kiểm chiếu giữ sai số 0,04 pixel trong trường nhìn; với tâm ở rất xa ngoài ảnh và gần near plane, dung sai tương đối float32 được ghi trong validator. Không chọn vài frame đẹp để tuyên bố cả dataset đạt chất lượng huấn luyện.

Tài nguyên tích hợp: ba bộ texture CC0 Poly Haven, nguồn/checksum trong `Documentation/asset-manifest.json`; hình học và gait được tạo trong dự án. Không tích hợp nội dung Mixamo hoặc Unity Asset Store bị hạn chế ML. FFmpeg là công cụ phát triển bên ngoài player; MP4 chỉ là bản nén cho trình diễn, PNG+JSONL là nguồn dữ liệu.
"""
    (ROOT / "Reports/Acceptance.vi.md").write_text(text, encoding="utf-8")
    print(f"PASS: {run['frames']} paired frames, {len(validation['traffic_phase_frames'])} phases, {len(decoded_videos)} videos; source hashes match.")


if __name__ == "__main__":
    main()
