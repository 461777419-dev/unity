using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace Suyao.ResourceOrganizer
{
    internal sealed class ReferenceRow
    {
        public string Path, Guid, Type;
        public bool Selected;
        public readonly List<string> Scenes = new List<string>();
        public string CleanupReason = "尚未清理复核";
        public bool Cleanable;
    }

    internal sealed class SceneQuery
    {
        public readonly List<ReferenceRow> Rows = new List<ReferenceRow>();
        public readonly List<string> Errors = new List<string>();
        public int Revision, SceneCount;
        public string Settings, Label;
        public bool Complete;

        public bool IsCurrent { get { return Complete && Revision == ProjectState.Revision && Settings == ProjectState.SettingsStamp(); } }

        public IEnumerator Scan(IEnumerable<UnityEngine.Object> targets, SceneAsset single, bool all, EditorJob job)
        {
            Revision = ProjectState.Revision;
            Settings = ProjectState.SettingsStamp();
            Label = all ? "工程全部场景（含可解析包场景）" : AssetDatabase.GetAssetPath(single);
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in AssetUtility.TargetPaths(targets))
            {
                paths.Add(path);
                yield return null;
            }
            if (paths.Count == 0) throw new InvalidOperationException("所选对象中没有可查询文件。");
            foreach (string path in paths.OrderBy(p => p, StringComparer.Ordinal))
            {
                var type = AssetDatabase.GetMainAssetTypeAtPath(path);
                Rows.Add(new ReferenceRow { Path = path, Guid = AssetDatabase.AssetPathToGUID(path), Type = type == null ? "Unknown" : type.Name });
                yield return null;
            }
            string[] scenes = all ? AssetUtility.AllPaths().Where(p => p.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)).ToArray()
                : new[] { AssetDatabase.GetAssetPath(single) };
            if (scenes.Length == 0 || scenes.Any(string.IsNullOrEmpty)) throw new InvalidOperationException("未找到场景或指定场景为空。");
            SceneCount = scenes.Length;
            var rowsByPath = Rows.ToDictionary(r => r.Path, StringComparer.Ordinal);
            for (int i = 0; i < scenes.Length; i++)
            {
                job.Message = "查询场景 " + (i + 1) + "/" + scenes.Length + "：" + scenes[i];
                job.Progress = (float)i / scenes.Length;
                try
                {
                    if (AssetDatabase.LoadAssetAtPath<SceneAsset>(scenes[i]) == null) throw new IOException("场景无法加载");
                    foreach (string dependency in AssetDatabase.GetDependencies(scenes[i], true))
                    {
                        ReferenceRow row;
                        if (rowsByPath.TryGetValue(dependency, out row)) row.Scenes.Add(scenes[i]);
                    }
                }
                catch (Exception exception) { Errors.Add(scenes[i] + "：" + exception.Message); }
                yield return null;
            }
            if (Revision != ProjectState.Revision || Settings != ProjectState.SettingsStamp())
                throw new InvalidOperationException("扫描期间工程发生变化，请重新扫描。");
            Complete = Errors.Count == 0;
        }

        public static string FindChain(string scene, string target)
        {
            var queue = new Queue<string>();
            var parent = new Dictionary<string, string>(StringComparer.Ordinal) { { scene, null } };
            queue.Enqueue(scene);
            while (queue.Count > 0)
            {
                string path = queue.Dequeue();
                if (EditorUtility.DisplayCancelableProgressBar("查找引用链", path, 0))
                    return "已取消引用链查询。";
                if (path == target)
                {
                    var chain = new List<string>();
                    while (path != null) { chain.Add(path); path = parent[path]; }
                    chain.Reverse();
                    return string.Join("\n→ ", chain);
                }
                foreach (string dependency in AssetDatabase.GetDependencies(path, false))
                    if (!parent.ContainsKey(dependency)) { parent.Add(dependency, path); queue.Enqueue(dependency); }
            }
            return "未找到直接依赖链；递归索引可能来自导入器的聚合依赖。";
        }
    }

    internal sealed class ProjectIndex
    {
        public readonly Dictionary<string, string[]> Dependencies = new Dictionary<string, string[]>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Protected = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> FolderReferences = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly HashSet<string> cleanupFolders = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> MergeCacheNotes = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> IgnoredIndexRecords = new HashSet<string>(StringComparer.Ordinal);
        public readonly HashSet<string> AddressablePaths = new HashSet<string>(StringComparer.Ordinal);
        public readonly List<string> Errors = new List<string>();
        public string[] Paths;
        public int Revision;
        public string Settings;
        public bool Complete;
        private readonly Dictionary<string, string> guidPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Regex GuidToken = new Regex(@"(?<![0-9a-fA-F])[0-9a-fA-F]{32}(?![0-9a-fA-F])", RegexOptions.Compiled);
        private static readonly HashSet<string> Serialized = new HashSet<string>(
            (".unity .prefab .asset .mat .controller .overridecontroller .anim .mask .playable .rendertexture " +
             ".spriteatlas .spriteatlasv2 .lighting .guiskin .flare .physicmaterial .physicsmaterial2d .terrainlayer " +
             ".shadergraph .shadersubgraph .vfx .json .uxml .uss").Split(' '), StringComparer.OrdinalIgnoreCase);

        public bool IsCurrent { get { return Complete && Revision == ProjectState.Revision && Settings == ProjectState.SettingsStamp(); } }

        public IEnumerator Build(IEnumerable<UnityEngine.Object> keep, TextAsset manifest, EditorJob job)
        {
            Revision = ProjectState.Revision;
            Settings = ProjectState.SettingsStamp();
            Paths = AssetUtility.AllPaths();
            foreach (string path in AssetDatabase.GetAllAssetPaths())
            {
                string guid = AssetDatabase.AssetPathToGUID(path);
                if (!string.IsNullOrEmpty(guid)) guidPaths[guid] = path;
            }
            for (int i = 0; i < Paths.Length; i++)
            {
                string path = Paths[i];
                job.Message = "全工程复核 " + (i + 1) + "/" + Paths.Length + "：" + path;
                job.Progress = Paths.Length == 0 ? 0 : (float)i / Paths.Length;
                try
                {
                    Type ownerType = AssetDatabase.GetMainAssetTypeAtPath(path);
                    string ownerTypeName = ownerType == null ? null : ownerType.Name;
                    bool indexCache = FolderReferencePolicy.IsFr2Index(path, ownerTypeName);
                    // Keep actual Unity object dependencies even for the known FR2
                    // cache, but do not turn its string GUID catalogue into usage.
                    var deps = new HashSet<string>(AssetDatabase.GetDependencies(path, false), StringComparer.Ordinal);
                    if (indexCache) IgnoredIndexRecords.Add(path);
                    if (!indexCache && Serialized.Contains(System.IO.Path.GetExtension(path)))
                        foreach (string referenced in TextReferences(path)) deps.Add(referenced);
                    deps.Remove(path);
                    Dependencies[path] = deps.ToArray();
                    foreach (string dependency in deps)
                        if (AssetDatabase.IsValidFolder(dependency))
                        {
                            FolderReferencePolicy.Record(path, dependency, FolderReferences, cleanupFolders, ownerTypeName);
                            if (FolderReferencePolicy.IsFr2Index(path, ownerTypeName)) MergeCacheNotes.Add(path);
                        }
                    string reason = ResourcePath.ProtectedLocation(path);
                    if (!path.StartsWith("Assets/", StringComparison.Ordinal)) reason = "包资源与其依赖";
                    else if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) reason = "工程场景";
                    else if (!ResourcePath.CanCleanType(path)) reason = "默认保护的文件类型";
                    var importer = AssetImporter.GetAtPath(path);
                    if (importer != null && !string.IsNullOrEmpty(importer.assetBundleName)) reason = "AssetBundle 条目";
                    if (!string.IsNullOrEmpty(reason)) Protect(path, reason);
                }
                catch (Exception exception) { Errors.Add(path + "：" + exception.Message); }
                yield return null;
            }
            // Settings may hold GUID references not represented by an asset dependency graph.
            if (Directory.Exists("ProjectSettings"))
                foreach (string path in Directory.GetFiles("ProjectSettings", "*", SearchOption.AllDirectories))
                {
                    try
                    {
                        foreach (string asset in TextReferences(path))
                        {
                            Protect(asset, "项目设置引用：" + path);
                            if (AssetDatabase.IsValidFolder(asset))
                                FolderReferencePolicy.Record(path, asset, FolderReferences, cleanupFolders);
                        }
                    }
                    catch (Exception exception) { Errors.Add(path + "：" + exception.Message); }
                    yield return null;
                }
            foreach (UnityEngine.Object asset in PlayerSettings.GetPreloadedAssets())
                if (asset != null) Protect(AssetDatabase.GetAssetPath(asset), "预加载资源");
            foreach (UnityEngine.Object asset in keep)
                if (asset != null) Protect(AssetDatabase.GetAssetPath(asset), "用户保留列表");
            if (manifest != null) ReadManifest(manifest.text);
            try { ReadAddressables(); }
            catch (Exception exception) { Errors.Add("Addressables 解析失败：" + exception.GetBaseException().Message); }
            // Folder references imply use of the folder's contents. Folder nodes are not
            // returned by AllPaths, so explicitly connect them to their descendants.
            foreach (string folder in cleanupFolders)
                Dependencies[folder] = Paths.Where(p => ResourcePath.IsWithin(p, folder)).ToArray();
            if (Revision != ProjectState.Revision || Settings != ProjectState.SettingsStamp())
                throw new InvalidOperationException("复核期间工程发生变化，请重新扫描。");
            Complete = Errors.Count == 0;
        }

        private IEnumerable<string> TextReferences(string path)
        {
            // Package paths are virtual: use PackageInfo rather than assume a physical Packages folder.
            string diskPath = path;
            if (path.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
                if (info == null) throw new IOException("无法解析包路径：" + path);
                diskPath = System.IO.Path.Combine(info.resolvedPath, path.Substring(("Packages/" + info.name + "/").Length));
            }
            if (!File.Exists(diskPath)) throw new IOException("无法读取文件：" + path);
            // Stream by line to avoid retaining large serialized assets in memory.
            using (var reader = new StreamReader(diskPath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                    foreach (Match match in GuidToken.Matches(line))
                    {
                        string referenced;
                        if (guidPaths.TryGetValue(match.Value, out referenced)) yield return referenced;
                    }
            }
        }

        private void Protect(string path, string reason)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (AssetDatabase.IsValidFolder(path))
            {
                FolderReferences[path] = reason;
                cleanupFolders.Add(path);
                foreach (string child in Paths)
                    if (ResourcePath.IsWithin(child, path)) Protected[child] = reason;
            }
            else Protected[path] = reason;
        }

        private void ReadManifest(string text)
        {
            foreach (string raw in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = raw.Trim();
                if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                string path = Regex.IsMatch(line, @"\A[0-9a-fA-F]{32}\z") ? AssetDatabase.GUIDToAssetPath(line) : ResourcePath.Normalize(line);
                if (string.IsNullOrEmpty(path) || (!AssetDatabase.IsValidFolder(path) && string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))))
                    Errors.Add("动态加载清单包含无效路径或 GUID：" + line);
                else Protect(path, "动态加载清单");
            }
        }

        private static object Member(object target, string name)
        {
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property != null) return property.GetValue(target, null);
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.Instance);
            if (field != null) return field.GetValue(target);
            throw new MissingMemberException(type.FullName, name);
        }

        private void ReadAddressables()
        {
            bool installed = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().Any(p => p.name == "com.unity.addressables");
            if (!installed) return;
            Type type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject")).FirstOrDefault(t => t != null);
            if (type == null) throw new InvalidOperationException("已安装包但其编辑器程序集未加载");
            MethodInfo get = type.GetMethod("GetSettings", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
            if (get == null) throw new MissingMethodException("找不到 GetSettings(bool)，不支持此 Addressables 版本");
            object settings = get.Invoke(null, new object[] { false });
            if (settings == null) return; // Never create settings just to scan.
            foreach (object group in (IEnumerable)Member(settings, "groups"))
            {
                if (group == null) continue;
                foreach (object entry in (IEnumerable)Member(group, "entries"))
                {
                    string path = Member(entry, "AssetPath") as string;
                    if (string.IsNullOrEmpty(path)) throw new InvalidOperationException("发现无法解析的 Addressables 条目");
                    AddressablePaths.Add(path);
                    Protect(path, "Addressables 条目（含文件夹条目）");
                }
            }
        }
    }
}
