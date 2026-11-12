#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using PlantsVsZombies.Rendering;
using PvZReplica.Reanimation;
using PvZReplica.UI.BitmapFonts;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace PlantsVsZombies.Tests.Editor
{
    /// <summary>Checks HD pack coverage, native geometry, resource routing, and fallback.</summary>
    public static class HdImageValidation
    {
        /// <summary>Runs deterministic integration checks in the specified Unity Editor.</summary>
        /// <exception cref="InvalidOperationException">An image or geometry check fails.</exception>
        public static void Run()
        {
            var root = Path.Combine(Application.streamingAssetsPath, "PvZ");
            ValidatePack(root);
            ValidateWritingMask(root);
            ValidateTextureDimensions(root);
            ValidateAnimationBounds(root);
            ValidateResourcePages();
            ValidateFallback();
            HighResolutionTextures.Enabled = true;
            Debug.Log("PVZ_HD_VALIDATION_PASS: pack coverage, native bounds, atlas cells, font metrics, resource routes and fallback; HD loads=" + HighResolutionTextures.LoadedCount);
        }

        /// <summary>Runs integration checks and builds a macOS Player for rendering smoke tests.</summary>
        /// <exception cref="InvalidOperationException">Validation or the Player build fails.</exception>
        public static void BuildMac()
        {
            Run();
            var arguments = Environment.GetCommandLineArgs();
            var index = Array.IndexOf(arguments, "-hd-build-output");
            if (index < 0 || index + 1 >= arguments.Length)
            {
                throw new InvalidOperationException("-hd-build-output is required.");
            }
            var output = arguments[index + 1];
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            var report = BuildPipeline.BuildPlayer(new[] { "Assets/Scenes/Lawn.unity" }, output,
                BuildTarget.StandaloneOSX, BuildOptions.Development);
            Require(report.summary.result == BuildResult.Succeeded, "macOS Player build failed.");
            Debug.Log("PVZ_HD_PLAYER_BUILD_PASS: " + output);
        }

        /// <summary>Checks every manifest entry and confirms all source PNG/JPEG files are covered.</summary>
        /// <param name="root">The original PvZ resource root.</param>
        private static void ValidatePack(string root)
        {
            var pack = Path.Combine(root, "1080_images");
            var manifest = JsonUtility.FromJson<Pack>(File.ReadAllText(Path.Combine(pack, "manifest.json")));
            Require(manifest != null && manifest.entries.Length > 1600, "HD manifest is incomplete.");
            var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in manifest!.entries)
            {
                Require(sources.Add(entry.source), "Duplicate source key: " + entry.source);
                var bytes = File.ReadAllBytes(Path.Combine(pack, entry.file));
                Require(bytes.Length >= 24 && bytes[0] == 137 && bytes[1] == 80 && bytes[2] == 78 && bytes[3] == 71,
                    "Output is not PNG: " + entry.file);
                Require(BigEndian(bytes, 16) == entry.pixelWidth && BigEndian(bytes, 20) == entry.pixelHeight,
                    "Wrong image dimensions: " + entry.file);
                Require(entry.pixelWidth == entry.width * entry.scale && entry.pixelHeight == entry.height * entry.scale,
                    "Incorrect source scale: " + entry.file);
            }
            foreach (var folder in new[] { "images", "reanim", "particles" })
            {
                foreach (var file in Directory.GetFiles(Path.Combine(root, folder), "*", SearchOption.AllDirectories))
                {
                    var extension = Path.GetExtension(file).ToLowerInvariant();
                    if (extension == ".png" || extension == ".jpg" || extension == ".jpeg")
                    {
                        Require(sources.Contains(file.Substring(root.Length + 1).Replace('\\', '/')), "Missing HD source: " + file);
                    }
                }
            }
            Debug.Log("PVZ_HD_PACK_PASS: " + manifest.entries.Length + " PNG files.");
        }

        /// <summary>Verifies physical pixels, original layout dimensions, and atlas normalization.</summary>
        /// <param name="root">The original PvZ resource root.</param>
        private static void ValidateTextureDimensions(string root)
        {
            HighResolutionTextures.Enabled = true;
            foreach (var name in new[] { "images/SeedBank.png", "images/background1.jpg", "images/options_menuback.jpg",
                "images/ProjectilePea.png", "images/ZombieNoteHelpBlack.png", "reanim/PEASHOOTER_HEAD.png", "reanim/ZOMBIE_HEAD.png", "reanim/SUN1.png" })
            {
                var path = Path.Combine(root, name);
                var original = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                Require(ImageConversion.LoadImage(original, File.ReadAllBytes(path)), "Cannot decode original: " + name);
                Require(HighResolutionTextures.TryLoad(path, out var hd), "HD route was not used: " + name);
                try
                {
                    Require(hd.width == original.width * 4 && hd.height == original.height * 4, "HD dimensions: " + name);
                    Require(HighResolutionTextures.Width(hd) == original.width && HighResolutionTextures.Height(hd) == original.height,
                        "Design dimensions changed: " + name);
                    Require(HighResolutionTextures.Width(hd) / 4f == original.width / 4f, "Atlas cell geometry changed.");
                    Require(hd.filterMode == FilterMode.Trilinear && hd.wrapMode == TextureWrapMode.Clamp, "HD sampling mode changed.");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(original);
                    UnityEngine.Object.DestroyImmediate(hd);
                }
            }
        }

        /// <summary>Protects the explicit PNG color/mask pair from becoming an opaque black rectangle.</summary>
        /// <param name="root">The PvZ resource root.</param>
        private static void ValidateWritingMask(string root)
        {
            var image = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            try
            {
                Require(ImageConversion.LoadImage(image, File.ReadAllBytes(Path.Combine(root, "1080_images/ZombieNoteHelpBlack.png"))),
                    "Help writing PNG could not be decoded.");
                var transparent = 0;
                var visible = 0;
                foreach (var pixel in image.GetPixels32())
                {
                    if (pixel.a == 0)
                    {
                        transparent++;
                    }
                    if (pixel.a > 0)
                    {
                        visible++;
                    }
                }
                Require(transparent > image.width * image.height / 2 && visible > 0,
                    "Help writing lost its separate alpha mask.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(image);
            }
        }

        /// <summary>Compares real reanimation bounds and timing with original and HD pixel data.</summary>
        /// <param name="root">The original PvZ resource root.</param>
        private static void ValidateAnimationBounds(string root)
        {
            var names = new[] { "Peashooter", "SunFlower", "Wallnut", "PotatoMine", "SnowPea", "CherryBomb", "Zombie", "LawnMower", "LoadBar_sprout", "LoadBar_Zombiehead" };
            var original = new Dictionary<string, ReanimationRenderer.AnimationInfo>();
            HighResolutionTextures.Enabled = false;
            using (var renderer = new ReanimationRenderer(root))
            {
                foreach (var name in names)
                {
                    Require(renderer.TryGetAnimationInfo(name, null, out var info), "Original animation: " + name + ": " + renderer.LastError);
                    original.Add(name, info);
                }
            }
            var previousLoads = HighResolutionTextures.LoadedCount;
            HighResolutionTextures.Enabled = true;
            using (var renderer = new ReanimationRenderer(root))
            {
                foreach (var name in names)
                {
                    Require(renderer.TryGetAnimationInfo(name, null, out var hd), "HD animation: " + name + ": " + renderer.LastError);
                    var source = original[name];
                    Require(source.FrameCount == hd.FrameCount && source.FramesPerSecond == hd.FramesPerSecond,
                        "Animation timing changed: " + name);
                    Require(source.SourceBounds == hd.SourceBounds, "Animation placement/bounds changed: " + name);
                }
            }
            Require(HighResolutionTextures.LoadedCount > previousLoads + 20, "Animation textures did not use the HD pack.");
            Debug.Log("PVZ_HD_ANIMATION_BOUNDS_PASS: " + names.Length + " original/HD animations have identical bounds and timing.");
        }

        /// <summary>Checks loading images and bitmap fonts preserve their source metrics.</summary>
        private static void ValidateResourcePages()
        {
            foreach (var name in new[] { "title", "logo", "grass", "dirt", "sodroll", "popcap" })
            {
                var source = Resources.Load<Texture2D>("PvZLoading/" + name);
                Require(source != null, "Loading image missing: " + name);
                Require(HighResolutionTextures.TryLoadResource("PvZLoading/" + name, out var hd), "Loading HD route failed.");
                Require(HighResolutionTextures.Width(hd) == source!.width && HighResolutionTextures.Height(hd) == source.height,
                    "Loading geometry changed: " + name);
                UnityEngine.Object.DestroyImmediate(hd);
            }
            var previousLoads = HighResolutionTextures.LoadedCount;
            foreach (var name in new[] { "BrianneTod16", "DwarvenTodcraft18GreenInset", "DwarvenTodcraft36GreenInset" })
            {
                HighResolutionTextures.Enabled = false;
                using (var original = ImGuiBitmapFont.LoadFromResources(name))
                {
                    var dimensions = original.Layout("Plants vs. Zombies");
                    HighResolutionTextures.Enabled = true;
                    using (var hd = ImGuiBitmapFont.LoadFromResources(name))
                    {
                        var actual = hd.Layout("Plants vs. Zombies");
                        Require(dimensions.Width == actual.Width && dimensions.Height == actual.Height, "Font layout changed: " + name);
                    }
                }
            }
            Require(HighResolutionTextures.LoadedCount > previousLoads, "Fonts did not use HD pages.");
        }

        /// <summary>Checks ordinary and disabled-pack lookups preserve their original fallback.</summary>
        private static void ValidateFallback()
        {
            var path = Path.Combine(Application.dataPath, "../Temp/HdImageValidation/images/missing.png");
            HighResolutionTextures.Enabled = true;
            Require(!HighResolutionTextures.TryLoad(path, out _), "Missing HD pack did not fall back.");
            HighResolutionTextures.Enabled = false;
            Require(!HighResolutionTextures.TryLoad(Path.Combine(Application.streamingAssetsPath, "PvZ/images/SeedBank.png"), out _),
                "Disabled pack did not fall back.");
        }

        /// <summary>Reads a PNG big-endian dimension.</summary>
        /// <param name="bytes">The PNG file bytes.</param>
        /// <param name="offset">The dimension offset in IHDR.</param>
        /// <returns>The positive image dimension.</returns>
        private static int BigEndian(byte[] bytes, int offset)
        {
            return bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3];
        }

        /// <summary>Fails the batch invocation when an integration assertion is false.</summary>
        /// <param name="condition">The required invariant.</param>
        /// <param name="message">The invariant failure.</param>
        /// <exception cref="InvalidOperationException">The required invariant is false.</exception>
        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException(message);
            }
        }

        /// <summary>Reads the manifest's serialized entry array.</summary>
        [Serializable]
        private sealed class Pack
        {
            /// <summary>The file manifest entries.</summary>
            public Entry[] entries = Array.Empty<Entry>();
        }

        /// <summary>Contains the fields required by pack validation.</summary>
        [Serializable]
        private sealed class Entry
        {
            /// <summary>The original resource key.</summary>
            public string source = string.Empty;
            /// <summary>The replacement PNG path.</summary>
            public string file = string.Empty;
            /// <summary>The original design width.</summary>
            public int width = 0;
            /// <summary>The original design height.</summary>
            public int height = 0;
            /// <summary>The physical output width.</summary>
            public int pixelWidth = 0;
            /// <summary>The physical output height.</summary>
            public int pixelHeight = 0;
            /// <summary>The integer image scale.</summary>
            public int scale = 0;
        }
    }
}
