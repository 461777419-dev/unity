// Copy with Fixtures/FR2_Cache.cs beside the tool's Editor scripts in a disposable
// Unity project; run -batchmode -nographics -executeMethod
// Suyao.ResourceOrganizer.UnityEditorValidation.Run. Never run in a user project.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Suyao.ResourceOrganizer.ValidationFixtures;

namespace Suyao.ResourceOrganizer
{
    public static partial class UnityEditorValidation
    {
        private const string Root = "Assets/OrganizerValidation";
        private static readonly List<string> results = new List<string>();

        public static void Run()
        {
            int exit = 0;
            try
            {
                // Hard stop if accidentally invoked outside the disposable project.
                if (!File.Exists("organizer-validation-project.marker"))
                    throw new InvalidOperationException("Disposable project marker missing.");
                Folder(Root);
                var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
                EditorSceneManager.SaveScene(scene, Root + "/Validation.unity");
                Folder("Assets/Editor/FindReference2");
                var cache = ScriptableObject.CreateInstance<FR2_Cache>();
                const string cachePath = "Assets/Editor/FindReference2/FR2_Cache.asset";
                AssetDatabase.CreateAsset(cache, cachePath);
                AssetDatabase.SaveAssets();
                cache.revision++;
                EditorUtility.SetDirty(cache);
                Check(ProjectState.UnsavedAssets().Contains(cachePath), "Unsaved-items display still lists dirty FR2 cache");
                Check(!ProjectState.UnsavedAssets(true).Contains(cachePath), "Merge ignores exact FR2 cache type/path");
                AssetUtility.RequireClean();
                Check(true, "Default cleanup readiness permits dirty known FR2 index");
                ExpectFailure(() => AssetUtility.RequireClean(false), "Explicit strict save check still detects FR2 cache");
                AssetUtility.RequireClean(true);
                Check(true, "Merge readiness permits dirty FR2 index");

                string matPath = Root + "/Unsaved.mat";
                var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
                AssetDatabase.CreateAsset(material, matPath);
                AssetDatabase.SaveAssets();
                EditorUtility.SetDirty(material);
                ExpectFailure(() => AssetUtility.RequireClean(true), "Unsaved material still blocks merge");
                ExpectFailure(() => AssetUtility.RequireClean(), "Unsaved material still blocks default cleanup check");
                AssetDatabase.SaveAssets();
                EditorUtility.SetDirty(cache);

                string source = Root + "/MergeSource";
                string target = Root + "/MergeTarget";
                string moving = Text(source + "/a.txt", "a");
                Text(source + "/same.txt", "source");
                Text(target + "/same.txt", "target");
                string nested = Text(source + "/Nested/child.txt", "nested");
                string nestedGuid = AssetDatabase.AssetPathToGUID(nested);
                string folderGuid = AssetDatabase.AssetPathToGUID(source + "/Nested");
                string guid = AssetDatabase.AssetPathToGUID(moving);
                var plan = MergeService.Plan(source, target, ConflictPolicy.Skip);
                Check(plan.Items.Any(i => i.Action == MergeAction.Skip && i.IsConflict), "Skip collision marked as conflict");
                var report = MergeService.Execute(plan);
                Check(File.Exists(target + "/a.txt") && !File.Exists(moving), "Actual file moved with dirty FR2 cache");
                Check(AssetDatabase.AssetPathToGUID(target + "/a.txt") == guid, "Moved file GUID preserved");
                Check(AssetDatabase.AssetPathToGUID(target + "/Nested") == folderGuid, "Whole directory GUID preserved");
                Check(AssetDatabase.AssetPathToGUID(target + "/Nested/child.txt") == nestedGuid, "Nested child GUID preserved");
                Check(File.ReadAllText(target + "/same.txt") == "target" && File.Exists(source + "/same.txt"), "Skip never overwrites either file");
                Check(Directory.Exists(source), "Source directory identity retained");
                Check(report.entries.Count(e => e.status == "成功" && e.action == "Move") == 2, "Report counts actual moves");

                source = Root + "/SelectiveSource";
                target = Root + "/SelectiveTarget";
                Text(source + "/selected.txt", "selected");
                Text(source + "/no.txt", "not selected");
                plan = MergeService.Plan(source, target, ConflictPolicy.Rename, true);
                plan.Items.Single(i => i.Source == source + "/no.txt").Selected = false;
                Check(plan.SelectedMoves == 1, "Selection controls move count");
                report = MergeService.Execute(plan);
                Check(File.Exists(target + "/Other/selected.txt") && File.Exists(source + "/no.txt"), "Only checked row is moved");
                Check(report.entries.Any(e => e.source == source + "/no.txt" && e.status == "未勾选"), "Unchecked row reported distinctly");
                Check(report.entries.Count(e => e.action == "CreateFolder" && e.status == "成功") == 2, "Required planned parents created in order");

                source = Root + "/ClassifiedSource";
                target = Root + "/ClassifiedTarget";
                Folder(source);
                material = new Material(Shader.Find("Hidden/InternalErrorShader"));
                AssetDatabase.CreateAsset(material, source + "/look.mat");
                Text(source + "/one/same.txt", "one");
                Text(source + "/two/same.txt", "two");
                AssetDatabase.SaveAssets();
                EditorUtility.SetDirty(cache);
                guid = AssetDatabase.AssetPathToGUID(source + "/look.mat");
                plan = MergeService.Plan(source, target, ConflictPolicy.Rename, true);
                Check(!plan.HasErrors, "Classified plan reuses planned category folders");
                Check(plan.Items.Any(i => i.Action == MergeAction.Move && i.IsConflict && i.Target.EndsWith("same_1.txt")), "Auto-rename collision marked as conflict");
                plan.Items.Single(i => i.Source == source + "/look.mat").Selected = false;
                report = MergeService.Execute(plan);
                Check(!Directory.Exists(target + "/Materials"), "Unchecked category creates no empty destination");
                Check(File.Exists(target + "/Other/same.txt") && File.Exists(target + "/Other/same_1.txt"), "Flattened duplicate names safely renamed");
                plan = MergeService.Plan(source, target, ConflictPolicy.Rename, true);
                report = MergeService.Execute(plan);
                Check(AssetDatabase.AssetPathToGUID(target + "/Materials/look.mat") == guid, "Native material classified with GUID preserved");

                source = Root + "/AbortSource";
                target = Root + "/AbortTarget";
                Text(source + "/same.txt", "source");
                Text(target + "/same.txt", "target");
                plan = MergeService.Plan(source, target, ConflictPolicy.Abort);
                Check(plan.HasErrors, "Abort policy blocks conflicting batch");
                var abort = plan;
                ExpectFailure(() => MergeService.Execute(abort), "Blocked plan cannot execute");
                Check(File.ReadAllText(target + "/same.txt") == "target", "Blocked operation leaves target unchanged");

                source = Root + "/StaleSource";
                target = Root + "/StaleTarget";
                string stalePath = Text(source + "/a.txt", "old");
                plan = MergeService.Plan(source, target, ConflictPolicy.Skip);
                File.AppendAllText(stalePath, "changed after preview");
                var stale = plan;
                ExpectFailure(() => MergeService.Execute(stale), "Local snapshot rejects post-preview changes");
                Check(!Directory.Exists(target), "Stale preview creates no destination");
                plan = MergeService.Plan(source, target, ConflictPolicy.Skip);
                foreach (var item in plan.Items) item.Selected = false;
                var empty = plan;
                ExpectFailure(() => MergeService.Execute(empty), "No selection cannot execute");
                Check(!Directory.Exists(target), "No selection creates no destination");
                AssetDatabase.Refresh();
                AssetDatabase.SaveAssets();
                RunExtendedTests(cache);
            }
            catch (Exception exception)
            {
                exit = 1;
                results.Add("FAIL: " + exception);
                Debug.LogException(exception);
            }
            string summary = string.Join("\n", results) + "\nUnity " + Application.unityVersion +
                "\nResult: " + (exit == 0 ? "PASS" : "FAIL");
            File.WriteAllText("validation-result.txt", summary);
            Debug.Log(summary);
            EditorApplication.Exit(exit);
        }

        private static void Check(bool condition, string label)
        {
            if (!condition) throw new InvalidOperationException(label);
            results.Add("PASS: " + label);
        }

        private static void ExpectFailure(Action action, string label)
        {
            try { action(); }
            catch (InvalidOperationException) { Check(true, label); return; }
            throw new InvalidOperationException("Expected rejection: " + label);
        }

        private static void Folder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = ResourcePath.Parent(path);
            Folder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static string Text(string path, string value)
        {
            Folder(ResourcePath.Parent(path));
            File.WriteAllText(path, value);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            return path;
        }
    }
}
