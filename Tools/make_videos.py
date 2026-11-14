"""Encode presentation copies of a completed lossless capture. Raw PNGs/labels are preserved."""
import argparse
import json
from pathlib import Path
import subprocess
from PIL import Image, ImageDraw, ImageFont


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("capture", type=Path)
    parser.add_argument("--ffmpeg", type=Path)
    parser.add_argument("--output", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    executable = args.ffmpeg or next((root / "Tools/PythonPackages/imageio_ffmpeg/binaries").glob("ffmpeg*.exe"), None)
    if executable is None or not executable.exists():
        raise SystemExit("Install imageio-ffmpeg locally or pass --ffmpeg /path/to/ffmpeg.")
    capture = args.capture.resolve()
    config = json.loads((capture / "calibration.json").read_text(encoding="utf-8-sig"))
    summary = json.loads((capture / "capture_summary.json").read_text(encoding="utf-8-sig"))
    output = (args.output or root / "Presentation").resolve()
    output.mkdir(parents=True, exist_ok=True)
    fps = int(summary["fps"])
    common = [str(executable), "-hide_banner", "-loglevel", "warning", "-y"]
    encoding = ["-c:v", "libx264", "-preset", "medium", "-crf", "18", "-pix_fmt", "yuv420p", "-movflags", "+faststart"]
    outputs = []
    for name in ("pinhole", "fisheye"):
        target = output / f"JunctionLab-{name}.mp4"
        subprocess.run(common + ["-framerate", str(fps), "-i", str(capture / name / "frame_%06d.png")] + encoding + [str(target)], check=True)
        outputs.append(target)
    overlay = Image.new("RGBA", (1580, 644), (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    def font(size, bold=False):
        path = Path("C:/Windows/Fonts") / ("segoeuib.ttf" if bold else "segoeui.ttf")
        return ImageFont.truetype(str(path), size) if path.exists() else ImageFont.load_default()
    draw.rectangle((0, 0, 1579, 63), fill=(12, 22, 27, 255))
    draw.rectangle((0, 604, 1579, 643), fill=(12, 22, 27, 255))
    draw.text((22, 10), "STATIONFLOW  /  JUNCTION LAB", font=font(25, True), fill=(234, 243, 242))
    draw.text((566, 22), "01   PINHOLE  |  70° vertical", font=font(18), fill=(110, 225, 198))
    draw.text((1020, 22), "02   FISHEYE  |  180° equidistant", font=font(18), fill=(110, 225, 198))
    draw.text((22, 611), f"EUROPEAN QUARTER  ·  SEED {summary['seed']}  ·  {fps} FPS  ·  SYNCHRONIZED SENSOR VIEWS", font=font(16), fill=(168, 188, 195))
    draw.text((1040, 611), "TECHNICAL DEMO  /  ORIGINAL PROCEDURAL ACTORS", font=font(15), fill=(168, 188, 195))
    overlay_path = output / "video-title-overlay.png"
    overlay.save(overlay_path)
    graph = "[1:v]scale=540:540,pad=620:540:40:0:color=0x0c161b[fish];[0:v][fish]hstack=inputs=2,pad=1580:644:0:64:color=0x0c161b[base];[base][2:v]overlay=0:0[v]"
    target = output / "JunctionLab-comparison.mp4"
    subprocess.run(common + ["-framerate", str(fps), "-i", str(capture / "pinhole/frame_%06d.png"), "-framerate", str(fps), "-i", str(capture / "fisheye/frame_%06d.png"), "-i", str(overlay_path), "-filter_complex", graph, "-map", "[v]"] + encoding + [str(target)], check=True)
    outputs.append(target)
    manifest = {"source": str(capture), "fps": fps, "frames": summary["frames"], "duration_seconds": summary["frames"] / fps, "codec": "H.264 / yuv420p", "purpose": "Lossy presentation copies. Use source PNG + JSONL for data.", "videos": [{"path": str(p), "bytes": p.stat().st_size} for p in outputs]}
    (output / "video-manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print(json.dumps(manifest, indent=2))


if __name__ == "__main__":
    main()
