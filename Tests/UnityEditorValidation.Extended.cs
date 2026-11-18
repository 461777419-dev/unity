using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;
using Suyao.ResourceOrganizer.ValidationFixtures;

namespace Suyao.ResourceOrganizer
{
    public static partial class UnityEditorValidation
    {
        // Interactive visual fixture: opens only this marked isolated project and
        // invokes the real scene query. No fabricated query results or red markers.
        public static void ShowPreview()
        {
            if (!File.Exists("organizer-validation-project.marker")) throw new InvalidOperationException("Disposable project marker missing.");
            EditorSceneManager.OpenScene(Root + "/AnalyzeScene.unity", OpenSceneMode.Single);
            var window = EditorWindow.GetWindow<ResourceOrganizerWindow>();
            window.position = new Rect(40, 60, 1120, 820);
            using (var serialized = new SerializedObject(window))
            {
                serialized.FindProperty("tab").intValue = 1;
                var targets = serialized.FindProperty("targets");
                targets.arraySize = 1;
                targets.GetArrayElementAtIndex(0).objectReferenceValue = AssetDatabase.LoadAssetAtPath<DefaultAsset>(Root + "/Analysis");
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }
            Selection.activeObject = AssetDatabase.LoadMainAssetAtPath(Root + "/Analysis/Surface.mat");
            EditorUtility.FocusProjectWindow();
            window.Show();
            EditorApplication.delayCall += () => typeof(ResourceOrganizerWindow)
                .GetMethod("StartQuery", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(window, null);
        }

        // Reopen the real fixtures created by Run() for interactive UI verification.
        public static void ShowUnifiedUiPreview()
        {
            if (!File.Exists("organizer-validation-project.marker")) throw new InvalidOperationException("Disposable project marker missing.");
            string scenePath = Root + "/CombinedRename/Combined.unity";
            EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/CombinedRename/Combined.prefab");
            var instance = SceneManager.GetActiveScene().GetRootGameObjects().First();
            instance.name = "待同步实例名称";
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            var window = EditorWindow.GetWindow<ResourceOrganizerWindow>();
            var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            Action<string, object> set = (name, value) => typeof(ResourceOrganizerWindow).GetField(name, flags).SetValue(window, value);
            var names = new RenamePlan();
            var query = new SceneQuery();
            var analysis = new ResourceAnalysis();
            using (var job = new EditorJob())
            {
                var scan = RenameService.FindRelated(prefab, 0, names, job);
                while (scan.MoveNext()) { }
                scan = query.Scan(new Object[] { AssetDatabase.LoadAssetAtPath<DefaultAsset>(Root) }, AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath), false, job);
                while (scan.MoveNext()) { }
                scan = analysis.Scan(2, new Object[] { AssetDatabase.LoadAssetAtPath<DefaultAsset>(Root) }, null, job);
                while (scan.MoveNext()) { }
            }
            foreach (var row in names.Items) row.DraftName = "";
            RenameService.ValidatePreview(names);
            set("renameSeed", prefab); set("renamePlan", names); set("renameMainName", "Combined");
            set("query", query); set("cleanupCandidates", CleanupCandidates.FromQuery(query));
            set("sceneNamePlan", SceneNameSync.Preview()); set("analysis", analysis);
            string moving = Root + "/DependencyInput";
            set("source", AssetDatabase.LoadAssetAtPath<DefaultAsset>(moving));
            set("targetPath", Root + "/UiDestination");
            set("plan", MergeService.Plan(moving, Root + "/UiDestination", ConflictPolicy.Rename, false));
            set("message", ""); set("tab", 3);
            window.position = new Rect(40, 50, 1120, 840);
            window.Show(); window.Focus(); window.Repaint();
        }

        private static void RunExtendedTests(FR2_Cache cache)
        {
            string renameRoot = Root + "/Renamer";
            string path = Text(renameRoot + "/tree.txt", "tree");
            string guid = AssetDatabase.AssetPathToGUID(path);
            var options = new RenameOptions { Prefix = "art_", Find = "tree", Replace = "leaf", Suffix = "_", Numbering = 1, Start = 3, Digits = 2 };
            var rename = RenameService.Preview(new[] { AssetDatabase.LoadMainAssetAtPath(path) }, options);
            Check(rename.Items[0].Target == renameRoot + "/art_leaf_03.txt", "Rename rules produce correct target");
            EditorUtility.SetDirty(cache);
            var renamed = RenameService.Execute(rename);
            path = renameRoot + "/art_leaf_03.txt";
            Check(File.Exists(path) && !File.Exists(renameRoot + "/tree.txt"), "Actual batch rename executed with dirty FR2 cache");
            Check(AssetDatabase.AssetPathToGUID(path) == guid, "Rename preserves GUID");
            Check(renamed.entries.Single().status == "成功", "Rename reports actual success");
            Text(renameRoot + "/occupied.txt", "occupied");
            options = new RenameOptions { BaseName = "occupied" };
            rename = RenameService.Preview(new[] { AssetDatabase.LoadMainAssetAtPath(path) }, options);
            Check(!rename.Items.Single().Selected && rename.Items.Single().Error.Contains("存在"), "Occupied rename target protected");
            var occupiedRename = rename;
            ExpectFailure(() => RenameService.Execute(occupiedRename), "Conflicting rename cannot execute");
            options = new RenameOptions { BaseName = "same" };
            rename = RenameService.Preview(new[] { AssetDatabase.LoadAssetAtPath<DefaultAsset>(renameRoot) }, options);
            Check(rename.Items.All(i => !i.Selected && i.Error.Contains("相同")), "Duplicate batch target names all rejected");
            options = new RenameOptions { Prefix = "new_" };
            rename = RenameService.Preview(new[] { AssetDatabase.LoadMainAssetAtPath(path) }, options);
            string occupiedAfterPreview = rename.Items.Single().Target;
            File.WriteAllText(occupiedAfterPreview, "race");
            var staleRename = rename;
            ExpectFailure(() => RenameService.Execute(staleRename), "New target occupancy after preview is rejected");
            Check(File.ReadAllText(occupiedAfterPreview) == "race" && File.Exists(path), "Rejected rename does not overwrite files");
            AssetDatabase.ImportAsset(occupiedAfterPreview, ImportAssetOptions.ForceSynchronousImport);

            string dependencyRoot = Root + "/DependencyInput";
            Folder(dependencyRoot);
            var shared = new Material(Shader.Find("Hidden/InternalErrorShader"));
            string sharedPath = dependencyRoot + "/Shared.mat";
            AssetDatabase.CreateAsset(shared, sharedPath);
            var external = new Material(Shader.Find("Hidden/InternalErrorShader"));
            string externalPath = Root + "/External.mat";
            AssetDatabase.CreateAsset(external, externalPath);
            var a = Prefab(dependencyRoot + "/A.prefab", shared);
            Prefab(dependencyRoot + "/B.prefab", shared, external);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            var collection = DependencyOrganizer.Collect(dependencyRoot, null);
            Check(collection.PrefabCount == 2, "Dependency collector only selects scoped prefabs");
            Check(collection.Candidates.Single(c => c.Path == sharedPath).Owners == 2, "Shared material usage counts selected prefabs");
            Check(!collection.Candidates.Any(c => c.Path == externalPath), "External dependency remains outside default scope");
            var depPlan = MergeService.Plan(dependencyRoot, Root + "/DependencyOutput", ConflictPolicy.Rename, false, collection.Candidates);
            Check(!depPlan.Items.Single(i => i.Source == sharedPath).Selected, "Shared dependency unchecked by default");
            Check(depPlan.Items.Count(i => i.Action == MergeAction.Move && i.Selected) == 2, "Selected prefab roots are movable");
            EditorUtility.SetDirty(cache);
            var depReport = MergeService.Execute(depPlan);
            Check(File.Exists(Root + "/DependencyOutput/Prefabs/A.prefab") && File.Exists(sharedPath), "Prefab moved while unchecked shared resource stays");
            Check(File.Exists(externalPath), "Dependency organization leaves out-of-scope assets unchanged");
            var movedA = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/DependencyOutput/Prefabs/A.prefab");
            Check(movedA.GetComponent<MeshRenderer>().sharedMaterial == shared, "Moved prefab retains material reference");
            collection = DependencyOrganizer.Collect(AssetDatabase.GetAssetPath(movedA), dependencyRoot);
            Check(collection.Candidates.Any(c => c.Path == AssetDatabase.GetAssetPath(movedA)), "Explicit dependency scope does not exclude selected prefab root");
            Check(collection.Candidates.Any(c => c.Path == sharedPath), "Explicit dependency scope includes referenced material");
            depPlan = MergeService.Plan(AssetDatabase.GetAssetPath(movedA), Root + "/DependencyAgain", ConflictPolicy.Rename, false, collection.Candidates);
            File.AppendAllText(sharedPath, "\n");
            var externalStale = depPlan;
            ExpectFailure(() => MergeService.Execute(externalStale), "Dependency outside source path is included in stale-plan snapshot");
            AssetDatabase.ImportAsset(sharedPath, ImportAssetOptions.ForceSynchronousImport);

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(movedA);
            instance.name = "Custom scene name";
            var sync = SceneNameSync.Preview();
            Check(sync.Items.Any(i => i.Instance == instance && i.NewName == movedA.name), "Scene name preview finds prefab root difference");
            foreach (var item in sync.Items) item.Selected = item.Instance == instance;
            Check(SceneNameSync.Apply(sync) == 1 && instance.name == movedA.name, "Selected scene name synchronized");
            Check(SceneManager.GetActiveScene().isDirty, "Scene synchronization marks scene dirty");
            Undo.PerformUndo();
            Check(instance.name == "Custom scene name", "Scene name sync supports Undo");
            Check(movedA.name != "Custom scene name", "Scene sync does not rename source prefab");
            sync = SceneNameSync.Preview();
            instance.name = "Edited after preview";
            var staleSync = sync;
            ExpectFailure(() => SceneNameSync.Apply(staleSync), "Scene name changes after preview invalidate sync");
            Object.DestroyImmediate(instance);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());

            string analysisRoot = Root + "/Analysis";
            Folder(analysisRoot);
            var mesh = new Mesh { name = "AnalysisTriangle", vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            AssetDatabase.CreateAsset(mesh, analysisRoot + "/Triangle.asset");
            var texture = new Texture2D(8, 4) { name = "AnalysisTexture" };
            AssetDatabase.CreateAsset(texture, analysisRoot + "/Texture.asset");
            var analysisMaterial = new Material(Shader.Find("Hidden/InternalErrorShader"));
            AssetDatabase.CreateAsset(analysisMaterial, analysisRoot + "/Surface.mat");
            string missingPrefabPath = analysisRoot + "/Missing.prefab";
            Prefab(missingPrefabPath, analysisMaterial);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            AddMissingScript(missingPrefabPath);
            var analysis = new ResourceAnalysis();
            using (var job = new EditorJob())
            {
                var routine = analysis.Scan(2, new Object[] { AssetDatabase.LoadAssetAtPath<DefaultAsset>(analysisRoot) }, null, job);
                while (routine.MoveNext()) { }
            }
            Check(analysis.Complete, "Folder analysis completes");
            Check(analysis.Rows.Any(r => r.Type == "网格" && r.Details.Contains("三角形 1")), "Mesh vertex and triangle metadata analyzed");
            Check(analysis.Rows.Any(r => r.Type == "贴图" && r.Details.Contains("8 × 4")), "Texture dimensions analyzed");
            Check(analysis.Rows.Any(r => r.Type == "材质" && r.Details.Contains("Shader")), "Material shader analyzed");
            Check(analysis.Rows.Any(r => r.Path == missingPrefabPath && r.Missing > 0), "Missing prefab script detected");
            Check(analysis.Rows.All(r => r.Memory >= 0), "Runtime memory estimates are nonnegative");

            var sceneObject = new GameObject("AnalysisSceneObject");
            sceneObject.AddComponent<MeshFilter>().sharedMesh = mesh;
            sceneObject.AddComponent<MeshRenderer>().sharedMaterial = analysisMaterial;
            string scenePath = Root + "/AnalyzeScene.unity";
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene(), scenePath);
            int handle = SceneManager.GetActiveScene().handle;
            bool dirty = SceneManager.GetActiveScene().isDirty;
            int scenes = SceneManager.sceneCount;
            analysis = new ResourceAnalysis();
            using (var job = new EditorJob())
            {
                var routine = analysis.Scan(1, new Object[0], AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath), job);
                while (routine.MoveNext()) { }
            }
            Check(analysis.Complete && analysis.Rows.Any(r => r.Asset == mesh), "Saved scene analysis includes mesh dependency");
            Check(SceneManager.sceneCount == scenes && SceneManager.GetActiveScene().handle == handle && SceneManager.GetActiveScene().isDirty == dirty,
                "Saved scene analysis does not open or dirty scenes");
            analysis = new ResourceAnalysis();
            using (var job = new EditorJob())
            {
                var routine = analysis.Scan(0, new Object[0], null, job);
                while (routine.MoveNext()) { }
            }
            Check(analysis.Complete && analysis.Rows.Any(r => r.Asset == analysisMaterial), "Active scene analysis uses current object dependencies");

            var query = new SceneQuery();
            using (var job = new EditorJob())
            {
                var routine = query.Scan(new Object[] { analysisMaterial, texture }, AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath), false, job);
                while (routine.MoveNext()) { }
            }
            Check(query.Complete && query.Rows.Single(r => r.Guid == AssetDatabase.AssetPathToGUID(analysisRoot + "/Surface.mat")).Scenes.Count == 1,
                "Real scene query identifies material GUID for Project marking");
            Check(query.Rows.Single(r => r.Guid == AssetDatabase.AssetPathToGUID(analysisRoot + "/Texture.asset")).Scenes.Count == 0,
                "Unreferenced texture is excluded from referenced Project markers");

            var linked = CleanupCandidates.FromQuery(query);
            Check(linked.Rows.Count == 1 && linked.Rows[0].Path == analysisRoot + "/Texture.asset", "Query link copies only unreferenced Assets rows");
            Check(!ReferenceEquals(linked.Rows[0], query.Rows.Single(r => r.Path == linked.Rows[0].Path)), "Cleanup candidates do not share mutable query rows");
            Check(!linked.Rows[0].Selected, "Protected native types are not selected automatically");
            var invalidQuery = new SceneQuery();
            ExpectFailure(() => CleanupCandidates.FromQuery(invalidQuery), "Incomplete query cannot seed cleanup");
            query.Revision--;
            ExpectFailure(() => CleanupCandidates.FromQuery(query), "Stale query cannot seed cleanup");
            query.Revision++;

            // Dispose the deliberately broken analysis fixture before testing the
            // complete cleanup graph; unresolved source fixtures must not be hidden.
            AssetDatabase.DeleteAsset(missingPrefabPath);
            string cleanupRoot = Root + "/LinkedCleanup";
            Folder(cleanupRoot);
            var unused = new Material(Shader.Find("Hidden/InternalErrorShader"));
            var usedByPrefab = new Material(Shader.Find("Hidden/InternalErrorShader"));
            string unusedPath = cleanupRoot + "/Unused.mat";
            string retainedPath = cleanupRoot + "/UsedByPrefab.mat";
            AssetDatabase.CreateAsset(unused, unusedPath);
            AssetDatabase.CreateAsset(usedByPrefab, retainedPath);
            var actualCacheReference = new Material(Shader.Find("Hidden/InternalErrorShader"));
            string actualCachePath = cleanupRoot + "/ActualCacheReference.mat";
            AssetDatabase.CreateAsset(actualCacheReference, actualCachePath);
            var spoofProtected = new Material(Shader.Find("Hidden/InternalErrorShader"));
            string spoofProtectedPath = cleanupRoot + "/WrongLocationReference.mat";
            AssetDatabase.CreateAsset(spoofProtected, spoofProtectedPath);
            cache.AssetGuids = new[] { AssetDatabase.AssetPathToGUID(unusedPath), AssetDatabase.AssetPathToGUID(retainedPath),
                AssetDatabase.AssetPathToGUID(cleanupRoot), AssetDatabase.AssetPathToGUID(actualCachePath) };
            cache.ActualReference = actualCacheReference;
            EditorUtility.SetDirty(cache);
            var wrongLocationCache = ScriptableObject.CreateInstance<FR2_Cache>();
            wrongLocationCache.AssetGuids = new[] { AssetDatabase.AssetPathToGUID(spoofProtectedPath) };
            AssetDatabase.CreateAsset(wrongLocationCache, cleanupRoot + "/FR2_Cache.asset");
            Prefab(cleanupRoot + "/Retained.prefab", usedByPrefab);
            EditorSceneManager.SaveScene(SceneManager.GetActiveScene());
            AssetDatabase.SaveAssets();
            query = new SceneQuery();
            using (var job = new EditorJob())
            {
                var routine = query.Scan(new Object[] { unused, usedByPrefab, analysisMaterial, actualCacheReference, spoofProtected }, AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath), false, job);
                while (routine.MoveNext()) { }
            }
            linked = CleanupCandidates.FromQuery(query);
            Check(linked.Rows.Count == 4 && linked.Rows.All(r => r.Selected), "Unreferenced supported art types arrive selected");
            Check(!linked.Rows.Any(r => r.Path == analysisRoot + "/Surface.mat"), "Referenced scene material never enters linked cleanup");
            EditorUtility.SetDirty(cache);
            Check(EditorUtility.IsDirty(cache), "FR2 cache is dirty immediately before cleanup review");
            var review = new CleanupPreview();
            using (var job = new EditorJob())
            {
                var routine = review.Review(linked.Rows, new Object[0], null, job);
                while (routine.MoveNext()) { }
            }
            Check(review.Decision.Allowed.Contains(unusedPath), "Full review allows truly unused linked material");
            Check(review.Decision.Blocked.ContainsKey(retainedPath), "Full review protects material used by retained prefab despite scene-unreferenced status");
            Check(review.Index.IgnoredIndexRecords.Contains("Assets/Editor/FindReference2/FR2_Cache.asset"), "Known FR2 string catalogue identified during cleanup");
            Check(!review.Index.Dependencies["Assets/Editor/FindReference2/FR2_Cache.asset"].Contains(unusedPath), "Indexed GUID string is not treated as asset usage");
            Check(review.Decision.Blocked.ContainsKey(actualCachePath), "Real Unity object reference in known cache remains protected");
            Check(review.Decision.Blocked.ContainsKey(spoofProtectedPath), "Same cache type and filename outside known location retains GUID-string protection");
            Check(EditorUtility.IsDirty(cache), "Cleanup review does not silently save FR2 cache");
            var trashReport = review.Trash(linked.Rows);
            Check(!File.Exists(unusedPath) && !File.Exists(unusedPath + ".meta"), "Linked unused resource and meta moved through trash API");
            Check(File.Exists(retainedPath) && File.Exists(analysisRoot + "/Surface.mat"), "Cleanup retains prefab-used and scene-used resources");
            Check(File.Exists(actualCachePath) && File.Exists(spoofProtectedPath), "Actual cache reference and wrong-location reference survive cleanup");
            Check(trashReport.entries.Any(e => e.source == unusedPath && e.status == "成功"), "Linked cleanup reports actual trash success");

            // Explicit query-result mode: no full-project graph and no second review.
            query = new SceneQuery();
            using (var job = new EditorJob())
            {
                var routine = query.Scan(new Object[] { usedByPrefab, analysisMaterial }, AssetDatabase.LoadAssetAtPath<SceneAsset>(scenePath), false, job);
                while (routine.MoveNext()) { }
            }
            linked = CleanupCandidates.FromQuery(query);
            var direct = CleanupPreview.FromQueryResult(linked);
            Check(direct.Index == null && direct.Decision.Allowed.SetEquals(new[] { retainedPath }), "Direct cleanup reuses scene query without building project index");
            linked.Rows[0].Selected = false;
            Check(!direct.Matches(linked.Rows), "Direct cleanup detects changed selection");
            linked.Rows[0].Selected = true;
            ProjectState.Changed();
            ExpectFailure(() => CleanupPreview.FromQueryResult(linked), "Direct cleanup rejects stale query results");
            linked.Revision = ProjectState.Revision; // No files changed in this invalidation test.
            direct = CleanupPreview.FromQueryResult(linked);
            trashReport = direct.Trash(linked.Rows);
            Check(!File.Exists(retainedPath) && !File.Exists(retainedPath + ".meta"), "Explicit direct cleanup trashes selected scene-unreferenced file without full review");
            Check(File.Exists(analysisRoot + "/Surface.mat"), "Direct cleanup leaves scene-referenced resource untouched");
            Check(trashReport.entries.Single().status == "成功", "Direct cleanup reports real deletion success");
            ExpectFailure(() => CleanupPreview.FromQueryResult(new CleanupCandidates { Complete = true, Revision = ProjectState.Revision, Settings = ProjectState.SettingsStamp() }), "Manual candidates cannot bypass review using query mode");
            RunRelatedRenameTests();
            RunCombinedRenameTests();
            RunAutoSuffixRenameTests();
            RunPreviewOnlyRenameTests();
            RunRenameStatusFilterTests();
        }

        private static void RunCombinedRenameTests()
        {
            string folder = Root + "/CombinedRename";
            Folder(folder);
            string modelPath = Text(folder + "/Triangle.obj", "o Triangle\nv 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n");
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(modelPath);
            var texture = new Texture2D(2, 2);
            string texturePath = folder + "/Surface.png";
            File.WriteAllBytes(texturePath, texture.EncodeToPNG());
            Object.DestroyImmediate(texture);
            AssetDatabase.ImportAsset(texturePath, ImportAssetOptions.ForceSynchronousImport);
            var material = new Material(Shader.Find("Unlit/Texture"));
            material.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            string materialPath = folder + "/Surface.mat";
            AssetDatabase.CreateAsset(material, materialPath);
            var root = new GameObject("Combined");
            root.AddComponent<MeshFilter>().sharedMesh = model.GetComponentInChildren<MeshFilter>().sharedMesh;
            root.AddComponent<MeshRenderer>().sharedMaterial = material;
            string prefabPath = folder + "/Combined.prefab";
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            Object.DestroyImmediate(root);
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            PrefabUtility.InstantiatePrefab(prefab);
            string scenePath = folder + "/Combined.unity";
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.SaveAssets();
            var combined = new RenamePlan();
            using (var job = new EditorJob())
            {
                var scan = RenameService.FindRelated(prefab, 0, combined, job);
                while (scan.MoveNext()) { }
            }
            Check(combined.Items.Single(i => i.Source == prefabPath).Relation == "本体", "Combined lookup labels selected asset");
            Check(combined.Items.Single(i => i.Source == modelPath).Relation == "依赖", "Combined lookup includes imported model dependency");
            Check(combined.Items.Single(i => i.Source == materialPath).Relation == "依赖", "Combined lookup includes material dependency");
            Check(combined.Items.Single(i => i.Source == texturePath).Relation == "依赖", "Combined lookup includes texture through material");
            Check(combined.Items.Single(i => i.Source == scenePath).Relation == "引用", "Combined lookup includes referring scene alongside dependencies");
            Check(combined.Items.All(i => !string.IsNullOrEmpty(i.TypeName)), "File rows expose resource type metadata");
            Check(combined.Embedded.Any(e => e.Asset is Mesh && e.OwnerGuid == AssetDatabase.AssetPathToGUID(modelPath)), "Imported model mesh is listed as embedded under owner GUID");
            Check(combined.Items.Count(i => i.Source == modelPath) == 1, "Embedded mesh does not create duplicate file rename rows");
            Check(combined.Items.Where(i => i.Source == modelPath || i.Source == materialPath || i.Source == texturePath).All(i => i.Icon != null), "Model material and texture expose Unity file icons");
        }

        private static void RunRelatedRenameTests()
        {
            string folder = Root + "/RelatedRename";
            Folder(folder);
            var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            string materialPath = folder + "/Surface.mat";
            AssetDatabase.CreateAsset(material, materialPath);
            var owner = Prefab(folder + "/Owner.prefab", material);
            AssetDatabase.SaveAssets();
            var related = new RenamePlan();
            using (var job = new EditorJob())
            {
                var scan = RenameService.FindRelated(material, false, related, job);
                while (scan.MoveNext()) { }
            }
            Check(related.Items.Any(i => i.Source == materialPath) && related.Items.Any(i => i.Source == folder + "/Owner.prefab"), "Reverse rename lookup includes seed and referring prefab");
            Check(related.Items.All(i => !i.Selected), "Unchanged names start unchecked");
            var dependencies = new RenamePlan();
            using (var job = new EditorJob())
            {
                var scan = RenameService.FindRelated(owner, true, dependencies, job);
                while (scan.MoveNext()) { }
            }
            Check(dependencies.Items.Any(i => i.Source == materialPath), "Forward rename lookup includes material dependency");
            var row = related.Items.Single(i => i.Source == materialPath);
            row.DraftName = "Leaf"; row.Suffix = "_mat";
            RenameService.Validate(related);
            Check(row.Target == folder + "/Leaf_mat.mat" && string.IsNullOrEmpty(row.Error), "Editable name and per-row suffix preserve extension");
            row.Selected = false;
            var single = RenameService.SingleRow(related, row);
            Check(single.Items.Count == 1 && single.Items[0].Selected && !row.Selected, "Single-row sync works independently of batch checkbox");
            Check(!related.Items.Single(i => i.Source.EndsWith("Owner.prefab")).Selected, "Single-row sync does not select other rows");
            var report = RenameService.Execute(single);
            RenameService.RefreshAfterExecute(related, report);
            Check(File.Exists(folder + "/Leaf_mat.mat") && !File.Exists(materialPath), "Single-row synchronization actually renames file");
            Check(owner.GetComponent<MeshRenderer>().sharedMaterial == material, "Single-row synchronization preserves referring prefab material");
            Check(AssetDatabase.AssetPathToGUID(row.Source) == row.Guid && related.IsCurrent, "Rename refresh preserves GUID and keeps remaining list current");
            Check(row.DraftName == "Leaf" && row.Suffix == "_mat", "Applied rename re-reads main name and suffix without duplication");
            var ownerRow = related.Items.Single(i => i.Source.EndsWith("Owner.prefab"));
            ownerRow.DraftName = "RenamedOwner"; ownerRow.Suffix = "_02"; ownerRow.Selected = true;
            RenameService.Validate(related);
            report = RenameService.Execute(related);
            RenameService.RefreshAfterExecute(related, report);
            Check(File.Exists(folder + "/RenamedOwner_02.prefab") && File.Exists(row.Source), "Remaining rows can rename after single-row synchronization");
            row.DraftName = "bad/name";
            RenameService.Validate(related);
            Check(!string.IsNullOrEmpty(row.Error), "Manual rename rejects path separators");
            row.DraftName = "Leaf_mat";
            string other = Text(folder + "/Other.mat", "occupied");
            row.DraftName = "Other";
            row.Suffix = "";
            RenameService.Validate(related);
            Check(row.Error.Contains("存在"), "Manual rename rejects existing destination");
            var stale = related;
            ProjectState.Changed();
            ExpectFailure(() => RenameService.Execute(stale), "External project changes invalidate editable rename list");
        }

        private static void RunAutoSuffixRenameTests()
        {
            string main, suffix;
            RenameService.ReadName("shequ_caoduo1_n", out main, out suffix);
            Check(main == "shequ_caoduo1" && suffix == "_n", "Suffix reads final underscore segment only");
            RenameService.ReadName("caoduo_1", out main, out suffix);
            Check(main == "caoduo" && suffix == "_1", "Numeric suffix is read automatically");
            RenameService.ReadName("white", out main, out suffix);
            Check(main == "white" && suffix == "", "Name without underscore keeps empty suffix");
            string folder = Root + "/AutoSuffix";
            Folder(folder);
            var first = new Material(Shader.Find("Hidden/InternalErrorShader"));
            var second = new Material(Shader.Find("Hidden/InternalErrorShader"));
            AssetDatabase.CreateAsset(first, folder + "/Surface_1.mat");
            AssetDatabase.CreateAsset(second, folder + "/Surface_n.mat");
            var prefab = Prefab(folder + "/Plant_source.prefab", first, second);
            AssetDatabase.SaveAssets();
            var plan = new RenamePlan();
            using (var job = new EditorJob())
            {
                var scan = RenameService.FindRelated(prefab, 1, plan, job);
                while (scan.MoveNext()) { }
            }
            var row = plan.Items.Single(i => i.Source == folder + "/Surface_n.mat");
            Check(row.DraftName == "Surface" && row.Suffix == "_n" && row.Target == row.Source, "Query auto-fills suffix without changing original target");
            Check(row.Error == "名称未变化" && !row.Selected, "Unchanged name clearly remains a no-op");
            RenameService.ApplyMainName(plan, "Leaf");
            Check(row.Target == folder + "/Leaf_n.mat" && string.IsNullOrEmpty(row.Error) && row.Selected, "Applying main name preserves suffix and enables selected rename");
            var report = RenameService.Execute(plan);
            RenameService.RefreshAfterExecute(plan, report);
            Check(File.Exists(folder + "/Leaf_1.mat") && File.Exists(folder + "/Leaf_n.mat"), "Batch rename executes with distinct auto-read suffixes");
            Check(row.DraftName == "Leaf" && row.Suffix == "_n" && row.Target == row.Source, "Rename completion retains parsed suffix without appending twice");
            Check(prefab.GetComponent<MeshRenderer>().sharedMaterials[1] == second, "Auto-suffix renaming keeps prefab material reference");
        }

        private static void RunPreviewOnlyRenameTests()
        {
            string folder = Root + "/PreviewOnly";
            Folder(folder);
            var material = new Material(Shader.Find("Hidden/InternalErrorShader"));
            string path = folder + "/Original_n.mat";
            AssetDatabase.CreateAsset(material, path);
            AssetDatabase.SaveAssets();
            var plan = new RenamePlan { Revision = ProjectState.Revision };
            var row = new RenameItem { Source = path, Target = path, Guid = AssetDatabase.AssetPathToGUID(path), DraftName = "Original", Suffix = "_n", Selected = true };
            plan.Items.Add(row);
            RenameService.ValidatePreview(plan);
            Check(row.Selected && row.Error == "名称未变化", "Unchanged preview remains selectable");
            Check(RenameService.SelectedPreview(plan).Items.Count == 0, "Selected unchanged names are skipped");
            RenameService.SyncRowPreview(plan, row, "Updated");
            Check(File.Exists(path) && !File.Exists(folder + "/Updated_n.mat"), "Per-row sync changes preview only and leaves disk untouched");
            Check(row.DraftName == "Updated" && row.Suffix == "_n" && row.Selected, "Per-row preview sync preserves suffix and checkbox");
            var batch = RenameService.SelectedPreview(plan);
            Check(File.Exists(path) && batch.Items.Count == 1, "Preparing selected rename does not modify disk");
            var report = RenameService.Execute(batch);
            RenameService.RefreshAfterExecute(plan, report);
            Check(File.Exists(folder + "/Updated_n.mat") && !File.Exists(path), "Only executing selected preview actually renames files");
            row.DraftName = "bad/name"; row.Selected = true;
            RenameService.ValidatePreview(plan);
            Check(row.Selected && !string.IsNullOrEmpty(row.Error), "Invalid preview keeps selection so reason remains visible");
            ExpectFailure(() => RenameService.SelectedPreview(plan), "Selected invalid preview is rejected with reason");
        }

        private static void RunRenameStatusFilterTests()
        {
            var plan = new RenamePlan();
            plan.Items.Add(new RenameItem { Source = "Assets/Plant.fbx", Relation = "依赖", DraftName = "" });
            plan.Items.Add(new RenameItem { Source = "Assets/Surface.mat", Relation = "依赖", DraftName = "Leaf", Error = "", Selected = true });
            plan.Items.Add(new RenameItem { Source = "Assets/Scene.unity", Relation = "引用", DraftName = "Scene", Error = "名称未变化" });
            Check(RenameService.Filter(plan, "依赖", "模型", "").Single().Source.EndsWith(".fbx"), "Relation and model filters combine");
            Check(RenameService.Filter(plan, "引用", "全部类型", "").Count() == 1, "Referring scenes can be isolated from dependencies");
            Check(RenameService.Filter(plan, "全部关系", "材质", "Leaf").Count() == 1, "Type filter and draft-name search combine");
            Check(plan.Items[1].Selected, "Filtering leaves hidden selection unchanged");
            Check(RenameService.Category("Assets/Surface.PNG") == "贴图", "Texture category handles uppercase extension");
            Check(RenameService.Status(plan.Items[0], true) == "待填写", "Empty draft displays pending state");
            Check(RenameService.Status(plan.Items[1], true) == "可执行" && RenameService.Status(plan.Items[2], true) == "未变化", "Valid and unchanged rows have explicit states");
            var conflict = new RenameItem { DraftName = "Leaf", Error = "目标名称已存在，不覆盖或交换名称" };
            Check(RenameService.Status(conflict, true) == "重名", "Conflict state is visible without tooltip");
            conflict.Error = "脚本与程序集名称受保护";
            Check(RenameService.Status(conflict, true) == "受保护", "Protected state is explicit");
            conflict.Error = ""; conflict.Source = conflict.Target = conflict.CompletedPath = "Assets/Done.mat";
            Check(RenameService.Status(conflict, true) == "已完成" && RenameService.Status(conflict, false) == "已过期", "Completion state yields to stale project state");
        }

        private static GameObject Prefab(string path, params Material[] materials)
        {
            Folder(ResourcePath.Parent(path));
            var root = new GameObject(Path.GetFileNameWithoutExtension(path));
            root.AddComponent<MeshRenderer>().sharedMaterials = materials;
            try { return PrefabUtility.SaveAsPrefabAsset(root, path); }
            finally { Object.DestroyImmediate(root); }
        }

        private static void AddMissingScript(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            string guid; long id;
            AssetDatabase.TryGetGUIDAndLocalFileIdentifier(prefab, out guid, out id);
            string text = File.ReadAllText(path);
            var pattern = new Regex(@"m_Component:\r?\n");
            text = pattern.Replace(text, "m_Component:\n  - component: {fileID: 123456789}\n", 1);
            text += "\n--- !u!114 &123456789\nMonoBehaviour:\n  m_ObjectHideFlags: 0\n  m_CorrespondingSourceObject: {fileID: 0}\n  m_PrefabInstance: {fileID: 0}\n  m_PrefabAsset: {fileID: 0}\n  m_GameObject: {fileID: " + id + "}\n  m_Enabled: 1\n  m_EditorHideFlags: 0\n  m_Script: {fileID: 11500000, guid: aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa, type: 3}\n  m_Name: \n  m_EditorClassIdentifier: \n";
            File.WriteAllText(path, text);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
