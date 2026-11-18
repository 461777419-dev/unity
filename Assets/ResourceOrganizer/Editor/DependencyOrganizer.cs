using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Suyao.ResourceOrganizer
{
    internal sealed class DependencyCandidate
    {
        public string Path;
        public int Owners;
    }

    internal sealed class DependencyCollection
    {
        public readonly List<DependencyCandidate> Candidates = new List<DependencyCandidate>();
        public string Scope;
        public int PrefabCount, Excluded;
    }

    internal static class DependencyOrganizer
    {
        public static DependencyCollection Collect(string source, string scope)
        {
            AssetUtility.RequireEditMode();
            source = ResourcePath.Normalize(source);
            if (!ResourcePath.IsAsset(source)) throw new InvalidOperationException("请选择Assets中的Prefab或文件夹。");
            scope = string.IsNullOrEmpty(scope) ? (AssetDatabase.IsValidFolder(source) ? source : ResourcePath.Parent(source)) : ResourcePath.Normalize(scope);
            if ((scope != "Assets" && !ResourcePath.IsAsset(scope)) || !AssetDatabase.IsValidFolder(scope))
                throw new InvalidOperationException("依赖范围必须是Assets内已导入的文件夹。");
            string[] prefabs = AssetDatabase.IsValidFolder(source)
                ? AssetDatabase.FindAssets("t:Prefab", new[] { source }).Select(AssetDatabase.GUIDToAssetPath).OrderBy(p => p, StringComparer.Ordinal).ToArray()
                : source.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) ? new[] { source } : new string[0];
            if (prefabs.Length == 0) throw new InvalidOperationException("源范围内没有Prefab。");
            var result = new DependencyCollection { Scope = scope, PrefabCount = prefabs.Length };
            var owners = new Dictionary<string, int>(StringComparer.Ordinal);
            var excluded = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabs[i]) == null) throw new IOException("Prefab无法加载：" + prefabs[i]);
                    if (EditorUtility.DisplayCancelableProgressBar("收集选定Prefab依赖", prefabs[i], (float)i / prefabs.Length))
                        throw new InvalidOperationException("依赖收集已取消；没有移动资源。");
                    foreach (string path in AssetDatabase.GetDependencies(prefabs[i], true).Concat(new[] { prefabs[i] }).Distinct(StringComparer.Ordinal))
                    {
                        string ext = Path.GetExtension(path).ToLowerInvariant();
                        if (!ResourcePath.IsAsset(path) || (!ResourcePath.IsWithin(path, scope) && !prefabs.Contains(path)) || AssetDatabase.IsValidFolder(path) ||
                            !string.IsNullOrEmpty(ResourcePath.ProtectedLocation(path)) ||
                            !(ResourcePath.CanCleanType(path) || new[] { ".asset", ".shader", ".compute", ".shadersubgraph" }.Contains(ext)))
                        { excluded.Add(path); continue; }
                        int count;
                        owners.TryGetValue(path, out count);
                        owners[path] = count + 1;
                    }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            result.Excluded = excluded.Count;
            foreach (var pair in owners) result.Candidates.Add(new DependencyCandidate { Path = pair.Key, Owners = pair.Value });
            return result;
        }
    }
}
