using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Suyao.ResourceOrganizer
{
    // Kept independent of Unity so safety rules can be verified without an Editor install.
    public static class ResourcePath
    {
        public static string Normalize(string path)
        {
            return (path ?? "").Replace('\\', '/').Trim().TrimEnd('/');
        }

        public static bool IsWithin(string path, string root)
        {
            path = Normalize(path);
            root = Normalize(root);
            return path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase);
        }

        public static bool IsAsset(string path)
        {
            path = Normalize(path);
            return path.StartsWith("Assets/", StringComparison.Ordinal) &&
                   !path.Split('/').Any(p => p == ".." || p == "." || p.Length == 0) &&
                   path.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|' }) < 0;
        }

        public static string Parent(string path)
        {
            int slash = path.LastIndexOf('/');
            return slash < 0 ? "" : path.Substring(0, slash);
        }

        public static bool Related(string a, string b)
        {
            return IsWithin(a, b) || IsWithin(b, a);
        }

        private static readonly HashSet<string> ArtExtensions = new HashSet<string>(
            (".prefab .mat .fbx .obj .blend .dae .3ds .dxf .max .ma .mb " +
             ".png .jpg .jpeg .tga .tif .tiff .psd .bmp .gif .exr .hdr .dds .cubemap .rendertexture " +
             ".anim .controller .overridecontroller .mask .spriteatlas .spriteatlasv2 " +
             ".wav .mp3 .ogg .aif .aiff .flac .mp4 .webm .mov .avi " +
             ".terrainlayer .physicmaterial .physicsmaterial2d .flare .guiskin").Split(' '),
            StringComparer.OrdinalIgnoreCase);

        public static bool CanCleanType(string path)
        {
            return ArtExtensions.Contains(Path.GetExtension(path));
        }

        public static bool HasSegment(string path, string name)
        {
            return Normalize(path).Split('/').Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase));
        }

        public static string ProtectedLocation(string path)
        {
            foreach (string segment in new[] { "Resources", "Editor", "Editor Default Resources", "StreamingAssets", "Plugins", "Gizmos", "ResourceOrganizer" })
                if (HasSegment(path, segment)) return "保护目录：" + segment;
            return "";
        }

        public static string Unique(string target, ISet<string> occupied)
        {
            string parent = Parent(target);
            string stem = Path.GetFileNameWithoutExtension(target);
            string ext = Path.GetExtension(target);
            for (int i = 1; ; i++)
            {
                string path = parent + "/" + stem + "_" + i + ext;
                if (!occupied.Contains(path)) return path;
            }
        }
    }

    public sealed class CleanupDecision
    {
        public readonly HashSet<string> Allowed = new HashSet<string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Blocked = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public static class UnsavedAssetPolicy
    {
        public static bool RequiresSave(string path, bool isImporter, bool isNativeAsset,
            bool ignoreKnownIndexCache = false, string mainTypeName = null)
        {
            path = ResourcePath.Normalize(path);
            return (path.StartsWith("Assets/", StringComparison.Ordinal) ||
                path.StartsWith("Packages/", StringComparison.Ordinal)) &&
                (isImporter || isNativeAsset) &&
                !(ignoreKnownIndexCache && !isImporter && FolderReferencePolicy.IsFr2Index(path, mainTypeName));
        }
    }

    public static class AssetNaming
    {
        public static string Compose(string original, string baseName, string prefix, string suffix,
            string find, string replace, int number, int numbering, int digits)
        {
            string name = string.IsNullOrEmpty(baseName) ? original : baseName;
            if (!string.IsNullOrEmpty(find)) name = name.Replace(find, replace ?? "");
            string index = "";
            if (numbering == 1) index = number.ToString("D" + Math.Max(1, Math.Min(9, digits)));
            if (numbering == 2)
            {
                int n = number;
                while (n > 0) { n--; index = (char)('a' + n % 26) + index; n /= 26; }
            }
            return (prefix ?? "") + name + (suffix ?? "") + index;
        }

        public static bool ValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.EndsWith(".", StringComparison.Ordinal) ||
                name.IndexOfAny(new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|', '\n', '\r', '\t' }) >= 0) return false;
            string stem = name.Split('.')[0].ToUpperInvariant();
            if (new[] { "CON", "PRN", "AUX", "NUL" }.Contains(stem)) return false;
            return !(stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] >= '1' && stem[3] <= '9');
        }
    }

    public static class AssetCategory
    {
        public static string Folder(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".prefab") return "Prefabs";
            if (new[] { ".fbx", ".obj", ".blend", ".dae", ".3ds", ".dxf", ".max", ".ma", ".mb" }.Contains(ext)) return "Models";
            if (ext == ".mat") return "Materials";
            if (new[] { ".png", ".jpg", ".jpeg", ".tga", ".tif", ".tiff", ".psd", ".bmp", ".gif", ".exr", ".hdr", ".dds", ".cubemap", ".rendertexture", ".spriteatlas", ".spriteatlasv2" }.Contains(ext)) return "Textures";
            if (new[] { ".anim", ".controller", ".overridecontroller", ".mask" }.Contains(ext)) return "Animations";
            if (new[] { ".wav", ".mp3", ".ogg", ".aif", ".aiff", ".flac" }.Contains(ext)) return "Audio";
            return "Other";
        }
    }

    public static class FolderReferencePolicy
    {
        public static bool IsFr2Index(string path, string assetTypeName)
        {
            path = ResourcePath.Normalize(path);
            return assetTypeName == "FR2_Cache" && ResourcePath.IsAsset(path) && ResourcePath.HasSegment(path, "FindReference2") &&
                Path.GetFileName(path).Equals("FR2_Cache.asset", StringComparison.OrdinalIgnoreCase);
        }

        // Cache bookkeeping is not a live DefaultAsset folder binding. Keep its edges
        // for conservative cleanup, but do not make them prevent GUID-preserving moves.
        public static void Record(string owner, string folder,
            IDictionary<string, string> mergeBindings, ISet<string> cleanupFolders, string assetTypeName = null)
        {
            cleanupFolders.Add(folder);
            if (!IsFr2Index(owner, assetTypeName)) mergeBindings[folder] = owner;
        }
    }

    public static class CleanupGraph
    {
        // Any retained asset is a root. Propagation also protects dependencies of a blocked candidate.
        // Cycles entirely inside the candidate set are deliberately allowed.
        public static CleanupDecision Evaluate(
            IEnumerable<string> candidates,
            IDictionary<string, string[]> dependencies,
            IDictionary<string, string> protectedRoots)
        {
            var result = new CleanupDecision();
            result.Allowed.UnionWith(candidates);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<string>();
            foreach (string path in dependencies.Keys)
                if (!result.Allowed.Contains(path) && visited.Add(path)) queue.Enqueue(path);
            foreach (var root in protectedRoots)
            {
                if (result.Allowed.Remove(root.Key)) result.Blocked[root.Key] = root.Value;
                if (visited.Add(root.Key)) queue.Enqueue(root.Key);
            }
            while (queue.Count > 0)
            {
                string source = queue.Dequeue();
                string[] next;
                if (!dependencies.TryGetValue(source, out next)) continue;
                foreach (string dependency in next)
                {
                    if (result.Allowed.Remove(dependency))
                        result.Blocked[dependency] = "保留资源引用：" + source;
                    if (visited.Add(dependency)) queue.Enqueue(dependency);
                }
            }
            return result;
        }
    }
}
