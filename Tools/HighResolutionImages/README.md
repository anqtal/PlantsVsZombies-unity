# HD texture pack

The game now prefers `Assets/StreamingAssets/PvZ/1080_images`, beside the original
`images` directory. The original files remain the fallback and define all layout
coordinates. No scene, animation track, hitbox, or global canvas scale is enlarged.

## Contents and selection

The pack contains 1,637 PNG files: the 1,614 source PNG/JPEG images, 22 bitmap-font
pages, and the embedded final-wave image. Sprite, UI, loading, and background
images use **waifu2x CUNet 4x, noise level 0**. Font pages use 2x to keep large
atlases below typical texture-size limits. Alpha-only masks are interpolated rather
than denoised. RGB is extended into transparent padding before neural inference to
reduce dark or white fringes.

Seven 4x configurations were compared on identical plant, zombie, UI, and background
samples on an M4 Mac mini. CUNet level 0 retained more original shading and texture
than stronger denoising, with sharper small shapes than the tested CUGAN SE and
AnimeVideo models. Anime6B softened or simplified some source textures. These are
visual judgments, not ground-truth reconstruction scores. Timings and the selected
configuration are recorded in `model-comparison.json`. Prior 16x tests produced
larger files and stronger style changes without a proportionate benefit at 1080p.

`1080_images/manifest.json` records every source key, original size, output size,
scale, and SHA-256. Original `images` files map directly into `1080_images`;
animation pieces, particles, loading images, and embedded resources use subfolders.
JPEG outputs retain `.jpg` in the name followed by `.png`, avoiding collisions.
JPEG/alpha-mask pairs also have a merged RGBA replacement for the color image.
The help page's explicit `ZombieNoteHelpBlack.png` / `ZombieNoteHelp.png` pair is
merged as well; its text mask is preserved rather than treated as opaque artwork.

## Runtime integration

`HighResolutionTextures` loads the optional pack and keeps source dimensions in a
weak metadata table. Reanimation bounds, part quads, centered origins, sprite-sheet
cells, source crops, tiled UI materials, nine-slice borders, and loading-bar crops
use those design dimensions. Normalized UVs still address the full HD image.
Bitmap-font glyph positions, advances, and atlas coordinates stay unchanged.
HD textures have mipmaps, trilinear sampling, and clamped addressing. Read-only
pixel buffers are released after upload.
Devices with a smaller maximum texture size use the original image for entries
that exceed their limit.

An absent or invalid HD entry falls back to the original resource. Launch with
`-pvz-original-images` for an original/HD comparison. No visible product setting
or profile migration is required.

## Rebuild the pack

Use Python with Pillow and NumPy, plus the official waifu2x-ncnn-vulkan executable
and its `models-cunet` directory:

```sh
python Tools/prepare_hd_images.py \
  --waifu2x /path/to/waifu2x-ncnn-vulkan \
  --models /path/to/models-cunet
```

Temporary inference files default to the ignored `Temp/HdImages` directory.
The generator validates every output size and writes a reproducibility manifest.
On Windows, pass the corresponding `.exe` path.

## Validation

Use **Unity 6000.3.17f1**. Run the editor integration checks:

```sh
Unity -batchmode -nographics -quit -projectPath "$PWD" \
  -executeMethod PlantsVsZombies.Tests.Editor.HdImageValidation.Run \
  -logFile /path/to/hd-validation.log
```

The checks cover all pack entries, original/HD native animation bounds and timing,
sprite-sheet cell geometry, loading images, bitmap-font layout, and fallback.

`HdImageValidation.BuildMac` also builds the existing Lawn scene for Player checks;
pass `-hd-build-output /path/to/PvZHd.app`. The existing feedback smoke scenario
passed 19 integration checks in each of original and HD mode at 1920x1080.
Its screenshots include plants, zombies, detached parts, rewards, and next-level UI.

The focused visual scenario waits for startup before capturing selector, help,
options, almanac, and populated native combat:

```sh
PvZUnity -pvz-ui-review -pvz-hd-layout-smoke \
  -screen-fullscreen 0 -screen-width 1920 -screen-height 1080 \
  -pvz-smoke-output /path/to/screenshots
```

Add `-pvz-original-images` for the baseline. Both flags are required for the focused
scenario so it always runs inside the existing isolated profile/review mode.
Both final original and HD runs passed at 1920x1080; the HD run loaded 231 pack
textures, and the baseline loaded zero. Help writing transparency is also covered
by a dedicated editor regression assertion.

The older general UI and experimental level-one smoke scenarios fail in both
original and HD modes: the UI script still expects an unavailable store notice,
and the level-one script checks image caches before startup finishes. These
baseline failures were not changed as part of texture integration.
Windows, Linux, Android, and iOS Player builds were not run in this macOS session.
