using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Suyao.ResourceOrganizer
{
    [InitializeOnLoad]
    internal static class ProjectState
    {
        public static int Revision { get; private set; }
        static ProjectState()
        {
            EditorApplication.projectChanged += Changed;
            Undo.undoRedoPerformed += Changed;
        }
        public static void Changed() { Revision++; }

        public static bool HasDirtyScenes()
        {
            return UnsavedScenes().Count > 0;
        }

        public static List<string> UnsavedScenes()
        {
            var result = new List<string>();
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.scene.isDirty)
                result.Add(prefabStage.assetPath + "（Prefab 编辑模式有未保存修改，请先在该模式保存或退出）");
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (prefabStage != null && scene == prefabStage.scene) continue;
                if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) continue;
                if (scene.isDirty || string.IsNullOrEmpty(scene.path))
                    result.Add(string.IsNullOrEmpty(scene.path) ? scene.name + "（新建场景，尚未保存路径）" : scene.path);
            }
            return result;
        }

        public static bool HasDirtyAssets()
        {
            return UnsavedAssets().Count > 0;
        }

        public static List<string> UnsavedAssets(bool ignoreKnownIndexCache = false)
        {
            // Imported output can remain dirty, but SaveAssets cannot serialize it
            // back to source files. Native assets and importer settings still block.
            return Resources.FindObjectsOfTypeAll<Object>().Where(o => o != null &&
                EditorUtility.IsDirty(o) &&
                UnsavedAssetPolicy.RequiresSave(AssetDatabase.GetAssetPath(o),
                    o is AssetImporter, EditorUtility.IsPersistent(o) && AssetDatabase.IsNativeAsset(o),
                    ignoreKnownIndexCache, ignoreKnownIndexCache ? AssetDatabase.GetMainAssetTypeAtPath(AssetDatabase.GetAssetPath(o))?.Name : null))
                .Select(o => AssetDatabase.GetAssetPath(o))
                .Where(p => !string.IsNullOrEmpty(p) &&
                    (p.StartsWith("Assets/", StringComparison.Ordinal) || p.StartsWith("Packages/", StringComparison.Ordinal)) &&
                    !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(p)))
                .Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToList();
        }

        public static string SettingsStamp()
        {
            // Includes package configuration: a package change invalidates old safety results.
            var files = Directory.Exists("ProjectSettings")
                ? Directory.GetFiles("ProjectSettings", "*", SearchOption.AllDirectories).ToList()
                : new List<string>();
            foreach (string path in new[] { "Packages/manifest.json", "Packages/packages-lock.json" })
                if (File.Exists(path)) files.Add(path);
            return string.Join("|", files.OrderBy(p => p, StringComparer.Ordinal).Select(p =>
            {
                var info = new FileInfo(p);
                return p + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks;
            }));
        }
    }

    internal sealed class ResourceChangeWatcher : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] oldPaths)
        {
            if (imported.Length + deleted.Length + moved.Length > 0) ProjectState.Changed();
        }
    }

    internal sealed class EditorJob : IDisposable
    {
        private IEnumerator routine;
        private Action completed;
        private Action<string> failed;
        public string Message = "准备中";
        public float Progress;
        public bool Running { get { return routine != null; } }

        public void Start(IEnumerator work, Action done, Action<string> error)
        {
            Dispose();
            routine = work;
            completed = done;
            failed = error;
            EditorApplication.update += Tick;
        }

        private void Tick()
        {
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
            { Finish("编辑器正在编译或进入运行模式，任务已取消。"); return; }
            try
            {
                var watch = Stopwatch.StartNew();
                do
                {
                    if (!routine.MoveNext()) { Finish(null); return; }
                } while (watch.ElapsedMilliseconds < 8);
            }
            catch (Exception exception) { Finish(exception.Message); UnityEngine.Debug.LogException(exception); }
        }

        private void Finish(string error)
        {
            Action done = completed;
            Action<string> onError = failed;
            Dispose();
            if (error == null) { if (done != null) done(); }
            else if (onError != null) onError(error);
        }

        public void Dispose()
        {
            EditorApplication.update -= Tick;
            var disposable = routine as IDisposable;
            routine = null;
            completed = null;
            failed = null;
            if (disposable != null) disposable.Dispose();
        }
    }

    [Serializable]
    internal sealed class OperationEntry
    {
        public string action, source, destination, guid, status, reason;
    }

    [Serializable]
    internal sealed class OperationReport
    {
        public string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        public string operation;
        public List<OperationEntry> entries = new List<OperationEntry>();
        public string summary;
        [NonSerialized] public string SavedPath;

        public void Save()
        {
            string folder = Path.GetFullPath("Library/ResourceOrganizerReports");
            Directory.CreateDirectory(folder);
            if (string.IsNullOrEmpty(SavedPath))
                SavedPath = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".json");
            File.WriteAllText(SavedPath, JsonUtility.ToJson(this, true), new System.Text.UTF8Encoding(false));
        }
    }

    internal static class AssetUtility
    {
        public static IEnumerable<string> TargetPaths(IEnumerable<Object> targets)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Object target in targets.Where(o => o != null))
            {
                string root = AssetDatabase.GetAssetPath(target);
                if (!ResourcePath.IsAsset(root) && root != "Assets")
                    throw new InvalidOperationException("操作对象必须位于 Assets 内。");
                if (AssetDatabase.IsValidFolder(root))
                {
                    foreach (string guid in AssetDatabase.FindAssets("", new[] { root }))
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        if (!AssetDatabase.IsValidFolder(path) && seen.Add(path)) yield return path;
                    }
                }
                else if (seen.Add(root)) yield return root;
            }
        }

        public static string[] AllPaths()
        {
            return AssetDatabase.GetAllAssetPaths().Where(p =>
                (p.StartsWith("Assets/", StringComparison.Ordinal) || p.StartsWith("Packages/", StringComparison.Ordinal)) &&
                !AssetDatabase.IsValidFolder(p)).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }

        public static bool IsEditable(string path)
        {
            return AssetDatabase.IsOpenForEdit(path) && AssetDatabase.IsOpenForEdit(path + ".meta") &&
                (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReadOnly) == 0) &&
                (!File.Exists(path + ".meta") || (File.GetAttributes(path + ".meta") & FileAttributes.ReadOnly) == 0);
        }

        public static void RequireClean(bool ignoreKnownIndexCache = true)
        {
            RequireEditMode();
            var scenes = ProjectState.UnsavedScenes();
            // FR2's known index is editor bookkeeping, not an unsaved runtime
            // asset. The cleanup graph still retains every cache dependency.
            var assets = ProjectState.UnsavedAssets(ignoreKnownIndexCache);
            if (scenes.Count == 0 && assets.Count == 0) return;
            string details = "";
            if (scenes.Count > 0) details += "\n场景：\n" + string.Join("\n", scenes.Take(10));
            if (assets.Count > 0) details += "\n资源：\n" + string.Join("\n", assets.Take(10));
            if (scenes.Count > 10 || assets.Count > 10) details += "\n（这里只显示前 10 项；可使用窗口顶部查看完整列表。）";
            throw new InvalidOperationException("操作尚未执行：有 " + scenes.Count + " 个未保存场景、" + assets.Count +
                " 个未保存资源。请使用窗口顶部“未保存项 / 保存修改”，保存后重新生成预览或扫描。" + details);
        }

        public static void RequireEditMode()
        {
            if (EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("请在非运行、非编译状态操作。");
        }
    }
}
