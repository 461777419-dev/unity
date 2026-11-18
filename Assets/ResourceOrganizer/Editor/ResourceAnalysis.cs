using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Suyao.ResourceOrganizer
{
    internal sealed class AnalysisRow
    {
        public Object Asset;
        public string Name, Path, Type, Details;
        public long Memory;
        public int Missing;
    }

    internal sealed class ResourceAnalysis
    {
        public readonly List<AnalysisRow> Rows = new List<AnalysisRow>();
        public readonly List<string> Errors = new List<string>();
        public string Scope, Time;
        public bool Complete;
        public int Revision;

        // mode: 0 active scene, 1 saved SceneAsset, 2 selected files/folders.
        public IEnumerator Scan(int mode, IEnumerable<Object> targets, SceneAsset single, EditorJob job)
        {
            AssetUtility.RequireEditMode();
            Revision = ProjectState.Revision;
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            var objects = new List<Object>();
            if (mode == 0)
            {
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded || PrefabStageUtility.GetCurrentPrefabStage() != null)
                    throw new InvalidOperationException("请在普通场景中分析。");
                Scope = "当前场景（含未保存内容）：" + scene.name;
                var roots = scene.GetRootGameObjects();
                objects.AddRange(EditorUtility.CollectDependencies(roots.Cast<Object>().ToArray()));
                foreach (var root in roots) AddSceneMissing(root, scene.path);
            }
            else if (mode == 1)
            {
                string scenePath = AssetDatabase.GetAssetPath(single);
                if (single == null || string.IsNullOrEmpty(scenePath)) throw new InvalidOperationException("请放入要分析的场景。");
                Scope = "指定场景磁盘版本：" + scenePath;
                foreach (string path in AssetDatabase.GetDependencies(scenePath, true))
                { objects.AddRange(AssetDatabase.LoadAllAssetsAtPath(path)); yield return null; }
                AddSavedSceneMissing(single, scenePath);
            }
            else
            {
                Scope = "所选文件/文件夹内资源（含子资源，不扩展外部依赖）";
                foreach (string path in AssetUtility.TargetPaths(targets))
                { objects.AddRange(AssetDatabase.LoadAllAssetsAtPath(path)); yield return null; }
            }
            var unique = objects.Where(o => o != null && EditorUtility.IsPersistent(o)).GroupBy(o => o.GetInstanceID()).Select(g => g.First()).ToArray();
            for (int i = 0; i < unique.Length; i++)
            {
                Object asset = unique[i];
                job.Message = "分析资源 " + (i + 1) + "/" + unique.Length + "：" + asset.name;
                job.Progress = (float)i / unique.Length;
                string path = AssetDatabase.GetAssetPath(asset);
                if (!string.IsNullOrEmpty(path) && (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)))
                {
                    try { Rows.Add(Inspect(asset, path)); }
                    catch (Exception exception) { Errors.Add(path + "：" + exception.Message); }
                }
                yield return null;
            }
            if (Revision != ProjectState.Revision) Errors.Add("分析期间工程变化，结果仅为不完整快照，请重新分析。");
            Complete = Errors.Count == 0;
        }

        private void AddSceneMissing(GameObject root, string scenePath)
        {
            string details;
            int count = MissingHierarchy(root, out details);
            if (count > 0) Rows.Add(new AnalysisRow { Asset = root, Name = root.name, Path = scenePath,
                Type = "缺失引用", Missing = count, Details = details });
        }

        private void AddSavedSceneMissing(SceneAsset asset, string path)
        {
            // Inspect saved GUIDs without opening scenes or executing edit-mode scripts.
            string text = File.ReadAllText(path);
            if (!text.StartsWith("%YAML", StringComparison.Ordinal))
            { Errors.Add("无法检查二进制场景的缺失GUID，请使用文本序列化：" + path); return; }
            int count = 0, line = 1, cursor = 0;
            var details = new List<string>();
            foreach (Match match in Regex.Matches(text, @"guid:\s*([0-9a-fA-F]{32})"))
            {
                for (; cursor < match.Index; cursor++) if (text[cursor] == '\n') line++;
                string guid = match.Groups[1].Value.ToLowerInvariant();
                if (guid == "00000000000000000000000000000000" || guid == "0000000000000000e000000000000000" || guid == "0000000000000000f000000000000000") continue;
                if (!string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid))) continue;
                count++;
                if (details.Count < 8) details.Add("行 " + line + "：" + guid);
            }
            if (count > 0) Rows.Add(new AnalysisRow { Asset = asset, Name = asset.name, Path = path,
                Type = "缺失引用", Missing = count, Details = "磁盘场景未解析的GUID引用（不含场景内部fileID校验）：" + string.Join("；", details) });
        }

        private static AnalysisRow Inspect(Object asset, string path)
        {
            var row = new AnalysisRow { Asset = asset, Name = asset.name, Path = path, Type = "其他", Details = asset.GetType().Name };
            // Loaded runtime object memory, not disk size, GPU residency or build size.
            row.Memory = Profiler.GetRuntimeMemorySizeLong(asset);
            var texture = asset as Texture;
            var material = asset as Material;
            var mesh = asset as Mesh;
            var prefab = asset as GameObject;
            if (texture != null)
            {
                row.Type = "贴图";
                row.Details = texture.width + " × " + texture.height;
                var t2 = texture as Texture2D;
                if (t2 != null) row.Details += "；" + t2.format + "；Mipmap层数 " + t2.mipmapCount;
            }
            else if (material != null)
            {
                row.Type = "材质";
                row.Details = "Shader " + (material.shader == null ? "缺失" : material.shader.name) + "；RenderQueue " + material.renderQueue;
            }
            else if (mesh != null)
            {
                row.Type = "网格";
                ulong triangles = 0;
                for (int s = 0; s < mesh.subMeshCount; s++)
                    if (mesh.GetTopology(s) == MeshTopology.Triangles) triangles += mesh.GetIndexCount(s) / 3;
                row.Details = "顶点 " + mesh.vertexCount + "；三角形 " + triangles + "；子网格 " + mesh.subMeshCount;
            }
            else if (prefab != null)
            {
                row.Type = PrefabUtility.GetPrefabAssetType(prefab) == PrefabAssetType.Model ? "模型" : "预制体";
                string missing;
                row.Missing = MissingHierarchy(prefab, out missing);
                row.Details += string.IsNullOrEmpty(missing) ? "" : "；" + missing;
                return row;
            }
            string referenceDetails;
            row.Missing = MissingObject(asset, out referenceDetails);
            if (!string.IsNullOrEmpty(referenceDetails)) row.Details += "；" + referenceDetails;
            return row;
        }

        private static int MissingHierarchy(GameObject root, out string details)
        {
            int count = 0;
            var notes = new List<string>();
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
            {
                int scripts = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject);
                count += scripts;
                if (scripts > 0 && notes.Count < 8) notes.Add(transform.name + "：缺失脚本 " + scripts);
                foreach (var component in transform.GetComponents<Component>())
                {
                    if (component == null) continue;
                    string missing;
                    count += MissingObject(component, out missing);
                    if (!string.IsNullOrEmpty(missing) && notes.Count < 8) notes.Add(transform.name + "/" + component.GetType().Name + "：" + missing);
                }
            }
            details = string.Join("；", notes);
            return count;
        }

        private static int MissingObject(Object asset, out string details)
        {
            int count = 0;
            var paths = new List<string>();
            using (var serialized = new SerializedObject(asset))
            {
                var property = serialized.GetIterator();
                while (property.Next(true))
                    if (property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue == null && property.objectReferenceInstanceIDValue != 0)
                    { count++; if (paths.Count < 8) paths.Add(property.propertyPath); }
            }
            details = count == 0 ? "" : "缺失引用 " + count + "：" + string.Join(", ", paths);
            return count;
        }
    }
}
