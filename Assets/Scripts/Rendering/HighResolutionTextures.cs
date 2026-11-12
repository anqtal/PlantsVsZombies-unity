#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace PlantsVsZombies.Rendering
{
    /// <summary>Loads optional HD pixels while retaining source-pixel layout coordinates.</summary>
    public static class HighResolutionTextures
    {
        /// <summary>Associates a decoded image with its original design dimensions.</summary>
        private static readonly ConditionalWeakTable<Texture2D, SourceSize> Sizes = new ConditionalWeakTable<Texture2D, SourceSize>();

        /// <summary>Caches manifests separately for the game and isolated test fixtures.</summary>
        private static readonly Dictionary<string, Catalog> Catalogs = new Dictionary<string, Catalog>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Tracks unavailable entries so an optional pack cannot flood the log.</summary>
        private static readonly HashSet<string> Warnings = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Gets or sets whether new loads prefer the HD pack.</summary>
        public static bool Enabled { get; set; } = Array.IndexOf(Environment.GetCommandLineArgs(), "-pvz-original-images") < 0;

        /// <summary>Gets the number of successfully decoded HD textures in this process.</summary>
        public static int LoadedCount { get; private set; }

        /// <summary>Gets a texture's width in original design pixels.</summary>
        /// <param name="texture">The loaded texture, or null.</param>
        /// <returns>The original width, physical width for ordinary textures, or zero.</returns>
        public static int Width(Texture? texture)
        {
            if (texture == null)
            {
                return 0;
            }
            if (texture is Texture2D image && Sizes.TryGetValue(image, out var size))
            {
                return size.Width;
            }
            return texture.width;
        }

        /// <summary>Gets a texture's height in original design pixels.</summary>
        /// <param name="texture">The loaded texture, or null.</param>
        /// <returns>The original height, physical height for ordinary textures, or zero.</returns>
        public static int Height(Texture? texture)
        {
            if (texture == null)
            {
                return 0;
            }
            if (texture is Texture2D image && Sizes.TryGetValue(image, out var size))
            {
                return size.Height;
            }
            return texture.height;
        }

        /// <summary>Tests whether this loader owns an image from an HD pack.</summary>
        /// <param name="texture">The image to inspect.</param>
        /// <returns>True when source dimensions have been registered.</returns>
        public static bool IsHighResolution(Texture2D? texture)
        {
            return texture != null && Sizes.TryGetValue(texture, out _);
        }

        /// <summary>Tries to load an HD replacement for an original image path.</summary>
        /// <param name="sourcePath">The original path below images, reanim, or particles.</param>
        /// <param name="texture">The decoded replacement on success.</param>
        /// <returns>False when disabled, missing, or invalid, allowing the original fallback.</returns>
        public static bool TryLoad(string sourcePath, out Texture2D texture)
        {
            texture = null!;
            if (!Enabled)
            {
                return false;
            }
            var fullPath = Path.GetFullPath(sourcePath);
            var directory = Path.GetDirectoryName(fullPath);
            while (!string.IsNullOrEmpty(directory))
            {
                var name = Path.GetFileName(directory);
                if (name == "images" || name == "reanim" || name == "particles")
                {
                    var root = Path.GetDirectoryName(directory);
                    if (root == null)
                    {
                        return false;
                    }
                    var key = fullPath.Substring(root.Length + 1).Replace('\\', '/');
                    return TryLoadEntry(root, key, false, out texture);
                }
                directory = Path.GetDirectoryName(directory);
            }
            return false;
        }

        /// <summary>Tries an HD replacement for a Resources image or bitmap-font page.</summary>
        /// <param name="resource">The original Resources-relative key without extension.</param>
        /// <param name="texture">The decoded replacement on success.</param>
        /// <param name="readable">Whether the caller still needs to inspect decoded pixels.</param>
        /// <returns>True when the HD page exists and its declared dimensions are valid.</returns>
        public static bool TryLoadResource(string resource, out Texture2D texture, bool readable = false)
        {
            var key = "resources/" + resource + ".png";
            if (resource.StartsWith("PvZLoading/", StringComparison.Ordinal))
            {
                key = "loading/" + resource.Substring("PvZLoading/".Length) + ".png";
            }
            else if (resource.StartsWith("PvZBitmapFonts/Atlases/", StringComparison.Ordinal))
            {
                key = "fonts/" + resource.Substring("PvZBitmapFonts/Atlases/".Length) + ".png";
            }
            return TryLoadEntry(Path.Combine(Application.streamingAssetsPath, "PvZ"), key, readable, out texture);
        }

        /// <summary>Loads, validates, and registers one manifest entry.</summary>
        /// <param name="root">The directory beside images and 1080_images.</param>
        /// <param name="source">The normalized source key.</param>
        /// <param name="readable">Whether to retain CPU-readable pixel storage.</param>
        /// <param name="texture">The replacement when successful.</param>
        /// <returns>False if the original loader should be used.</returns>
        private static bool TryLoadEntry(string root, string source, bool readable, out Texture2D texture)
        {
            texture = null!;
            if (!Enabled)
            {
                return false;
            }
            if (!Catalogs.TryGetValue(root, out var catalog))
            {
                catalog = ReadCatalog(root);
                Catalogs.Add(root, catalog);
            }
            if (!catalog.Entries.TryGetValue(source, out var entry))
            {
                return false;
            }
            var limit = SystemInfo.maxTextureSize;
            if (limit > 0 && (entry.pixelWidth > limit || entry.pixelHeight > limit))
            {
                WarnOnce(root + "/" + source, "HD image exceeds this device's texture-size limit: " + source);
                return false;
            }
            Texture2D? decoded = null;
            try
            {
                var path = Path.Combine(root, "1080_images", entry.file);
                decoded = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                decoded.name = Path.GetFileName(source);
                decoded.hideFlags = HideFlags.HideAndDontSave;
                if (!ImageConversion.LoadImage(decoded, File.ReadAllBytes(path), false)
                    || decoded.width != entry.pixelWidth || decoded.height != entry.pixelHeight)
                {
                    throw new InvalidDataException("HD image dimensions do not match its manifest: " + source);
                }
                decoded.filterMode = FilterMode.Trilinear;
                decoded.wrapMode = TextureWrapMode.Clamp;
                decoded.Apply(true, !readable);
                Sizes.Add(decoded, new SourceSize(entry.width, entry.height));
                LoadedCount++;
                texture = decoded;
                return true;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException || error is UnityException)
            {
                if (decoded != null)
                {
                    if (Application.isPlaying)
                    {
                        UnityEngine.Object.Destroy(decoded);
                    }
                    else
                    {
                        UnityEngine.Object.DestroyImmediate(decoded);
                    }
                }
                WarnOnce(root + "/" + source, error.Message);
                return false;
            }
        }

        /// <summary>Reads valid entries while rejecting paths outside the optional pack.</summary>
        /// <param name="root">The parent of 1080_images.</param>
        /// <returns>An empty catalog when no usable manifest exists.</returns>
        private static Catalog ReadCatalog(string root)
        {
            var catalog = new Catalog();
            var path = Path.Combine(root, "1080_images", "manifest.json");
            if (!File.Exists(path))
            {
                return catalog;
            }
            try
            {
                var manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(path));
                if (manifest == null || manifest.version != 1 || manifest.entries == null)
                {
                    throw new InvalidDataException("Unsupported HD image manifest.");
                }
                foreach (var entry in manifest.entries)
                {
                    if (entry.width <= 0 || entry.height <= 0 || entry.pixelWidth <= 0 || entry.pixelHeight <= 0
                        || string.IsNullOrEmpty(entry.source) || string.IsNullOrEmpty(entry.file)
                        || Path.IsPathRooted(entry.file) || entry.file.Contains("..") || entry.file.Contains("\\"))
                    {
                        throw new InvalidDataException("Invalid HD image manifest entry.");
                    }
                    catalog.Entries.Add(entry.source, entry);
                }
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException || error is ArgumentException)
            {
                catalog.Entries.Clear();
                WarnOnce(path, error.Message);
            }
            return catalog;
        }

        /// <summary>Reports an optional-pack failure once while preserving the source fallback.</summary>
        /// <param name="key">The affected entry or manifest path.</param>
        /// <param name="message">The validation or I/O failure.</param>
        private static void WarnOnce(string key, string message)
        {
            if (Warnings.Add(key))
            {
                Debug.LogWarning("[HighResolutionTextures] Using original image: " + message);
            }
        }

        /// <summary>Stores source-pixel dimensions independently of texture memory dimensions.</summary>
        private sealed class SourceSize
        {
            /// <summary>The original design width.</summary>
            public readonly int Width;
            /// <summary>The original design height.</summary>
            public readonly int Height;

            /// <summary>Creates source dimensions.</summary>
            /// <param name="width">The original width.</param>
            /// <param name="height">The original height.</param>
            public SourceSize(int width, int height)
            {
                Width = width;
                Height = height;
            }
        }

        /// <summary>Indexes serialized entries by original relative path.</summary>
        private sealed class Catalog
        {
            /// <summary>The case-insensitive source-path index used by existing resource lookups.</summary>
            public readonly Dictionary<string, Entry> Entries = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Matches the lower-case keys of the versioned on-disk manifest.</summary>
        [Serializable]
        private sealed class Manifest
        {
            /// <summary>The manifest schema version.</summary>
            public int version = 0;
            /// <summary>The replacement textures and their source dimensions.</summary>
            public Entry[] entries = Array.Empty<Entry>();
        }

        /// <summary>Describes one HD image without modifying source geometry.</summary>
        [Serializable]
        private sealed class Entry
        {
            /// <summary>The original path relative to its resource root.</summary>
            public string source = string.Empty;
            /// <summary>The PNG path relative to 1080_images.</summary>
            public string file = string.Empty;
            /// <summary>The original design width.</summary>
            public int width = 0;
            /// <summary>The original design height.</summary>
            public int height = 0;
            /// <summary>The expected decoded HD width.</summary>
            public int pixelWidth = 0;
            /// <summary>The expected decoded HD height.</summary>
            public int pixelHeight = 0;
        }
    }
}
