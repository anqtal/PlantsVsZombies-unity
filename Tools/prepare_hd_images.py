"""Build the optional 1080_images pack without changing source images or layout."""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import time

import numpy as np
from PIL import Image


def extend_edge_colors(image: Image.Image, distance: int = 20) -> Image.Image:
    """Extend RGB into transparent padding while leaving alpha and canvas intact."""
    pixels = np.array(image.convert("RGBA"))
    known = pixels[:, :, 3] > 0
    if known.all() or not known.any():
        return Image.fromarray(pixels)
    rgb = pixels[:, :, :3].copy()
    height, width = known.shape
    for _ in range(distance):
        values = np.zeros((height, width, 3), dtype=np.uint16)
        counts = np.zeros((height, width), dtype=np.uint8)
        for dest, source in [
            ((slice(1, None), slice(None)), (slice(None, -1), slice(None))),
            ((slice(None, -1), slice(None)), (slice(1, None), slice(None))),
            ((slice(None), slice(1, None)), (slice(None), slice(None, -1))),
            ((slice(None), slice(None, -1)), (slice(None), slice(1, None))),
        ]:
            valid = known[source]
            counts[dest] += valid
            values[dest] += rgb[source].astype(np.uint16) * valid[:, :, None]
        new = ~known & (counts > 0)
        if not new.any():
            break
        rgb[new] = (values[new] // counts[new, None]).astype(np.uint8)
        known[new] = True
    pixels[:, :, :3] = rgb
    return Image.fromarray(pixels)


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--waifu2x", required=True, type=Path)
    parser.add_argument("--models", required=True, type=Path)
    parser.add_argument("--work-dir", type=Path)
    parser.add_argument("--scale", type=int, default=4, choices=[2, 4])
    parser.add_argument("--noise", type=int, default=0, choices=[-1, 0, 1, 2, 3])
    args = parser.parse_args()
    project = Path(__file__).resolve().parents[1]
    pvz = project / "Assets/StreamingAssets/PvZ"
    output = pvz / "1080_images"
    work = args.work_dir or project / "Temp/HdImages"
    output.mkdir(parents=True, exist_ok=True)
    work.mkdir(parents=True, exist_ok=True)
    files = []
    for folder in ["images", "reanim", "particles"]:
        for path in sorted((pvz / folder).rglob("*")):
            if path.suffix.lower() in [".png", ".jpg", ".jpeg"]:
                key = path.relative_to(pvz).as_posix()
                relative = path.relative_to(pvz / "images") if folder == "images" else path.relative_to(pvz)
                if path.suffix.lower() != ".png":
                    relative = relative.with_name(relative.name + ".png")
                files.append((path, key, relative.as_posix(), args.scale, "image"))
    loading = project / "Assets/Scripts/Audio/Resources/PvZLoading"
    for path in sorted(loading.glob("*.png")):
        files.append((path, "loading/" + path.name, "loading/" + path.name, args.scale, "image"))
    fonts = project / "Assets/Scripts/UI/BitmapFonts/Resources/PvZBitmapFonts/Atlases"
    for path in sorted(fonts.glob("*.bytes")):
        if path.read_bytes().startswith(b"\x89PNG"):
            name = path.stem + ".png"
            files.append((path, "fonts/" + name, "fonts/" + name, 2, "font"))
    for path in sorted((project / "Assets").rglob("*.bytes")):
        if path.parent == fonts or "Resources" not in path.parts:
            continue
        if path.read_bytes().startswith(b"\x89PNG"):
            resource = Path(*path.parts[path.parts.index("Resources") + 1:]).with_suffix(".png").as_posix()
            files.append((path, "resources/" + resource, "resources/" + resource, args.scale, "resource"))
    mask_paths = {
        path.with_name(path.stem + "_.png")
        for path, _, _, _, _ in files
        if path.suffix.lower() in [".jpg", ".jpeg"]
    }
    explicit_masks = {
        "images/ZombieNoteHelpBlack.png": pvz / "images/ZombieNoteHelp.png",
    }
    mask_paths.update(explicit_masks.values())
    entries = []
    queued = {}
    started = time.perf_counter()
    for index, (path, key, relative, scale, kind) in enumerate(files):
        with Image.open(path) as source:
            image = source.convert("RGBA")
        width, height = image.size
        alpha_merged = False
        mask_path = explicit_masks.get(key, path.with_name(path.stem + "_.png"))
        if (key in explicit_masks or path.suffix.lower() in [".jpg", ".jpeg"]) and mask_path.is_file():
            with Image.open(mask_path) as mask:
                if mask.size != image.size:
                    raise ValueError("Mask size mismatch: " + str(path))
                image.putalpha(mask.convert("RGB").getchannel("R"))
            alpha_merged = True
        font_alpha = False
        if kind == "font" and path.stem.startswith("_"):
            pixels = np.asarray(image)
            if np.all(pixels[:, :, 3] == 255) and np.all(pixels[:, :, 0] == pixels[:, :, 1]) and np.all(pixels[:, :, 1] == pixels[:, :, 2]):
                alpha = image.getchannel("R")
                image = Image.new("RGBA", image.size, (255, 255, 255, 255))
                image.putalpha(alpha)
                font_alpha = True
        target = output / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        entry = {
            "source": key, "file": relative,
            "width": width, "height": height,
            "pixelWidth": width * scale, "pixelHeight": height * scale,
            "scale": scale, "kind": kind,
            "alphaMerged": alpha_merged,
            "sourceSha256": hashlib.sha256(path.read_bytes()).hexdigest(),
        }
        solid_color = key in explicit_masks
        if path in mask_paths or font_alpha or solid_color or image.getchannel("A").getextrema()[1] == 0:
            image.resize((width * scale, height * scale), Image.Resampling.LANCZOS).save(target)
            entry["processing"] = "mask-lanczos" if path in mask_paths or font_alpha else "solid-color-lanczos"
        else:
            group = queued.setdefault(scale, [])
            input_folder = work / ("input-" + str(scale))
            input_folder.mkdir(exist_ok=True)
            token = f"{index:05d}.png"
            prepared = extend_edge_colors(image)
            if image.getchannel("A").getextrema()[0] == 255:
                prepared = prepared.convert("RGB")
            prepared.save(input_folder / token)
            group.append((token, target, entry))
            entry["processing"] = "waifu2x-cunet"
        entries.append(entry)
        if (index + 1) % 200 == 0:
            print("PREPARED", index + 1, "/", len(files), flush=True)
    for scale, jobs in queued.items():
        result_folder = work / ("output-" + str(scale))
        result_folder.mkdir(exist_ok=True)
        command = [str(args.waifu2x), "-i", str(work / ("input-" + str(scale))), "-o", str(result_folder),
                   "-m", str(args.models), "-s", str(scale), "-n", str(args.noise),
                   "-g", "0", "-t", "128", "-j", "2:2:2", "-f", "png", "-v"]
        print("INFERENCE", scale, "x", len(jobs), "textures", flush=True)
        with (work / ("inference-" + str(scale) + ".log")).open("w") as log:
            subprocess.run(command, check=True, stdout=log, stderr=subprocess.STDOUT, timeout=1800)
        for token, target, entry in jobs:
            image_path = result_folder / token
            image_path.replace(target)
    for entry in entries:
        target = output / entry["file"]
        with Image.open(target) as image:
            if image.size != (entry["pixelWidth"], entry["pixelHeight"]):
                raise ValueError("Incorrect output size: " + entry["file"])
        entry["sha256"] = hashlib.sha256(target.read_bytes()).hexdigest()
    manifest = {"version": 1, "model": "waifu2x-cunet", "noise": args.noise,
                "scale": args.scale, "entries": entries}
    (output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n")
    report = {"textures": len(entries), "seconds": round(time.perf_counter() - started, 3),
              "model": "waifu2x-cunet", "noise": args.noise, "scale": args.scale,
              "sourcePixels": sum(e["width"] * e["height"] for e in entries),
              "outputPixels": sum(e["pixelWidth"] * e["pixelHeight"] for e in entries),
              "outputBytes": sum((output / e["file"]).stat().st_size for e in entries)}
    (work / "generation-report.json").write_text(json.dumps(report, indent=2))
    print("COMPLETE", json.dumps(report), flush=True)


if __name__ == "__main__":
    main()
