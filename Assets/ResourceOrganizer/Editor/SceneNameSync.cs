using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Suyao.ResourceOrganizer
{
    internal sealed class SceneNameItem
    {
        public GameObject Instance;
        public string OldName, NewName, SourceGuid, SourcePath;
        public bool Selected = true;
    }

    internal sealed class SceneNamePlan
    {
        public readonly List<SceneNameItem> Items = new List<SceneNameItem>();
        public int SceneHandle;
    }

    internal static class SceneNameSync
    {
        private static Scene Current()
        {
            AssetUtility.RequireEditMode();
            var scene = SceneManager.GetActiveScene();
            if (PrefabStageUtility.GetCurrentPrefabStage() != null || !scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                throw new InvalidOperationException("请在普通场景中操作，先退出Prefab编辑模式。");
            return scene;
        }

        public static SceneNamePlan Preview()
        {
            var scene = Current();
            var plan = new SceneNamePlan { SceneHandle = scene.handle };
            foreach (var transform in scene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren<Transform>(true)))
            {
                GameObject instance = transform.gameObject;
                if (!PrefabUtility.IsAnyPrefabInstanceRoot(instance)) continue;
                var source = PrefabUtility.GetCorrespondingObjectFromSource(instance);
                if (source == null || source.name == instance.name) continue;
                string path = AssetDatabase.GetAssetPath(source);
                plan.Items.Add(new SceneNameItem { Instance = instance, OldName = instance.name, NewName = source.name,
                    SourcePath = path, SourceGuid = AssetDatabase.AssetPathToGUID(path) });
            }
            return plan;
        }

        public static int Apply(SceneNamePlan plan)
        {
            var scene = Current();
            if (plan == null || scene.handle != plan.SceneHandle) throw new InvalidOperationException("当前场景已变化，请重新扫描。");
            var selected = plan.Items.Where(i => i.Selected).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("没有勾选需要同步的实例。");
            foreach (var item in selected)
            {
                var source = item.Instance == null ? null : PrefabUtility.GetCorrespondingObjectFromSource(item.Instance);
                if (item.Instance == null || item.Instance.scene != scene || item.Instance.name != item.OldName || source == null ||
                    source.name != item.NewName || AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(source)) != item.SourceGuid)
                    throw new InvalidOperationException("场景实例或源Prefab已变化，请重新扫描。");
            }
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("同步Prefab实例名称");
            foreach (var item in selected)
            {
                Undo.RecordObject(item.Instance, "同步Prefab实例名称");
                item.Instance.name = item.NewName;
                PrefabUtility.RecordPrefabInstancePropertyModifications(item.Instance);
            }
            Undo.FlushUndoRecordObjects();
            Undo.CollapseUndoOperations(group);
            EditorSceneManager.MarkSceneDirty(scene);
            return selected.Length;
        }
    }
}
