using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Suyao.ResourceOrganizer
{
    public sealed partial class ResourceOrganizerWindow
    {
        [SerializeField] private DefaultAsset dependencyScope;
        private RenamePlan renamePlan;
        private SceneNamePlan sceneNamePlan;
        private int renamePage, renameEmbeddedPage, syncPage;
        [SerializeField] private Object renameSeed;
        [SerializeField] private int renameDirection;
        private string renameListSuffix = "";
        private string renameMainName = "";
        private bool renameShowEmbedded = true;
        private int renameRelationFilter, renameTypeFilter;
        private string renameSearch = "";
        private static readonly string[] RenameRelations = { "全部关系", "本体", "依赖", "引用" };
        private static readonly string[] RenameTypes = { "全部类型", "场景", "预制体", "模型", "材质", "贴图", "其他" };

        private void DrawRenamer()
        {
            using (new EditorGUILayout.VerticalScope(ResourceTheme.Panel))
            {
                GUILayout.Label("关联资源命名", ResourceTheme.Section);
                EditorGUI.BeginChangeCheck();
                renameSeed = ResourceTheme.ObjectField("查找资源", renameSeed, typeof(Object), false);
                renameDirection = ResourceTheme.Popup("查找范围", renameDirection,
                    new[] { "依赖 + 引用（含间接关系）", "仅依赖：它使用的资源", "仅引用：使用它的资源" });
                if (EditorGUI.EndChangeCheck()) renamePlan = null;
                if (GUILayout.Button("查找依赖与引用", ResourceTheme.Primary)) RunSafe(() =>
                {
                    renamePlan = null;
                    var next = new RenamePlan();
                    StartJob(RenameService.FindRelated(renameSeed, renameDirection, next, job), () =>
                    {
                        renamePlan = next; renamePage = renameEmbeddedPage = 0;
                        RenameService.ReadName(Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(renameSeed)), out renameMainName, out _);
                        foreach (var row in renamePlan.Items) row.DraftName = "";
                        RenameService.ValidatePreview(renamePlan);
                        message = "查找完成：" + next.Items.Count + " 个文件，" + next.Embedded.Count + " 个内嵌资源。";
                    });
                });
            }
            if (renamePlan == null) return;
            bool current = renamePlan.IsCurrent;
            using (new EditorGUILayout.VerticalScope(ResourceTheme.Panel))
            {
                if (!current) ResourceTheme.Help("工程已变化，请重新查找。", MessageType.Warning);
                using (new EditorGUI.DisabledScope(!current))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        if (GUILayout.Button("读取命名", GUILayout.Width(80)))
                            RenameService.ReadName(Path.GetFileNameWithoutExtension(AssetDatabase.GetAssetPath(renameSeed)), out renameMainName, out _);
                        renameMainName = EditorGUILayout.TextField(renameMainName, ResourceTheme.Field, GUILayout.MinWidth(160), GUILayout.Height(28));
                    }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label("统一后缀", ResourceTheme.Muted, GUILayout.Width(76));
                        renameListSuffix = EditorGUILayout.TextField(renameListSuffix, ResourceTheme.Field, GUILayout.MinWidth(100), GUILayout.Height(28));
                        if (GUILayout.Button("后缀应用到列表", GUILayout.Width(124)))
                        {
                            foreach (var item in renamePlan.Items) item.Suffix = renameListSuffix;
                            RenameService.ValidatePreview(renamePlan);

                        }


                    }
                }
                GUILayout.Label(string.Join(" · ", renamePlan.Items.GroupBy(i => i.TypeName).Select(g => g.Key + " " + g.Count())) +
                    " · 已选 " + renamePlan.Items.Count(i => i.Selected), ResourceTheme.MiniWrap);
                EditorGUI.BeginChangeCheck();
                renameSearch = ResourceTheme.TextField("搜索原名 / 新名 / 路径", renameSearch);
                using (new EditorGUILayout.HorizontalScope())
                {
                    renameRelationFilter = ResourceTheme.Popup("关系", renameRelationFilter, RenameRelations);
                    renameTypeFilter = ResourceTheme.Popup("资源类型", renameTypeFilter, RenameTypes);
                }
                if (EditorGUI.EndChangeCheck()) renamePage = 0;
                var displayed = RenameService.Filter(renamePlan, RenameRelations[renameRelationFilter], RenameTypes[renameTypeFilter], renameSearch).ToArray();
                using (new EditorGUI.DisabledScope(!current))
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("勾选筛选结果")) foreach (var row in displayed) row.Selected = true;
                    if (GUILayout.Button("取消筛选勾选")) foreach (var row in displayed) row.Selected = false;
                }
                GUILayout.Label("显示 " + displayed.Length + " / " + renamePlan.Items.Count + " 项；执行包含其他筛选中已勾选的项。", ResourceTheme.MiniWrap);
                DrawPages(displayed.Length, ref renamePage);
                ResourceTheme.TableHeader(new[] { "", "原名 / 图标", "关系", "新主名", "后缀", "状态", "填入主名", "定位" }, 18, -1, 64, -1, 76, 76, 80, 48);
                foreach (var item in displayed.Skip(renamePage * PageSize).Take(PageSize))
                {
                    Rect[] cells = ResourceTheme.TableRow(18, -1, 64, -1, 76, 76, 80, 48);
                    using (new EditorGUI.DisabledScope(!current))
                        item.Selected = EditorGUI.Toggle(cells[0], item.Selected);
                    ResourceTheme.AssetCell(cells[1], item.Source, icon: item.Icon, tooltip: item.Source + "\n" + item.TypeName);
                    ResourceTheme.Cell(cells[2], item.Relation);
                    using (new EditorGUI.DisabledScope(!current))
                    {
                        EditorGUI.BeginChangeCheck();
                        item.DraftName = EditorGUI.TextField(cells[3], item.DraftName, ResourceTheme.Field);
                        item.Suffix = EditorGUI.TextField(cells[4], item.Suffix, ResourceTheme.Field);
                        if (EditorGUI.EndChangeCheck())
                        {
                            RenameService.ValidatePreview(renamePlan);

                        }
                    }
                    ResourceTheme.Cell(cells[5], RenameService.Status(item, current), string.IsNullOrEmpty(item.Error) ? "目标：" + item.Target : item.Error);
                    string tip = "仅将顶部主名填入本行预览，不修改文件。\n目标：" + Path.GetFileName(item.Target) + "\n" + item.Error;
                    using (new EditorGUI.DisabledScope(!current))
                        if (GUI.Button(cells[6], new GUIContent("填入主名", tip), ResourceTheme.Button))
                            RunSafe(() => RenameService.SyncRowPreview(renamePlan, item, renameMainName));
                    if (GUI.Button(cells[7], "定位", ResourceTheme.Button)) EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(item.Source));
                }
                GUILayout.Label("新主名默认留空；点击“填入主名”后更新该行，可继续调整。顶部输入不改列表；“执行勾选改名”才实际改名。", ResourceTheme.MiniWrap);
                using (new EditorGUI.DisabledScope(!current || !renamePlan.Items.Any(i => i.Selected)))
                    if (GUILayout.Button("执行勾选改名（" + renamePlan.Items.Count(i => i.Selected) + " 项）", ResourceTheme.Primary)) RunSafe(() =>
                    {
                        if (!EditorUtility.DisplayDialog("确认批量命名", "将重命名勾选文件并保留GUID；字符串路径需另行核对。", "执行改名", "取消")) return;
                        ExecuteRenameRows();
                    });
                if (renamePlan.Embedded.Count > 0)
                {
                    renameShowEmbedded = ResourceTheme.Foldout(renameShowEmbedded, "内嵌资源（" + renamePlan.Embedded.Count + "）· 随所属文件导入，不能单独重命名", true);
                    if (renameShowEmbedded)
                    {
                        DrawPages(renamePlan.Embedded.Count, ref renameEmbeddedPage);
                        foreach (var embedded in renamePlan.Embedded.Skip(renameEmbeddedPage * PageSize).Take(PageSize))
                        {
                            if (embedded.Asset == null) continue;
                            string owner = AssetDatabase.GUIDToAssetPath(embedded.OwnerGuid);
                            Rect[] cells = ResourceTheme.TableRow(-1, 230, 48);
                            ResourceTheme.AssetCell(cells[0], owner, embedded.Asset.name, AssetPreview.GetMiniThumbnail(embedded.Asset));
                            ResourceTheme.Cell(cells[1], embedded.Asset.GetType().Name + " · 内嵌 · " + embedded.Relation);
                            if (GUI.Button(cells[2], "定位", ResourceTheme.Button)) EditorGUIUtility.PingObject(embedded.Asset);
                        }
                    }
                }
            }
        }

        private void ExecuteRenameRows()
        {
            if (renamePlan == null || !renamePlan.IsCurrent) throw new InvalidOperationException("命名列表已过期，请重新查找。");
            RenamePlan executing = RenameService.SelectedPreview(renamePlan);
            if (executing.Items.Count == 0) { message = "已勾选项名称均未变化，已跳过；没有修改文件。"; return; }
            lastReport = RenameService.Execute(executing);
            RenameService.RefreshAfterExecute(renamePlan, lastReport);
            plan = null;
            InvalidateReferences(); InvalidateCleanup(); message = lastReport.summary; ShowMergeResult(lastReport);
        }

        private void DrawSceneSync()
        {
            using (new EditorGUILayout.VerticalScope(ResourceTheme.Panel))
            {
                GUILayout.Label("Prefab实例名称同步", ResourceTheme.Section);
                GUILayout.Label("当前活动场景：" + SceneManager.GetActiveScene().name, ResourceTheme.Wrap);
                GUILayout.Label("只同步Prefab实例根节点名称，包含未激活及嵌套实例；预览后勾选执行，支持Undo，不自动保存场景。", ResourceTheme.MiniWrap);
                if (GUILayout.Button("扫描当前场景的名称差异", ResourceTheme.Primary)) RunSafe(() =>
                { sceneNamePlan = SceneNameSync.Preview(); syncPage = 0; message = "发现 " + sceneNamePlan.Items.Count + " 个名称差异。"; });
            }
            if (sceneNamePlan == null) return;
            GUILayout.Space(12);
            using (new EditorGUILayout.VerticalScope(ResourceTheme.Panel))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("全选")) foreach (var item in sceneNamePlan.Items) item.Selected = true;
                    if (GUILayout.Button("取消全部")) foreach (var item in sceneNamePlan.Items) item.Selected = false;
                }
                DrawPages(sceneNamePlan.Items.Count, ref syncPage);
                ResourceTheme.TableHeader(new[] { "", "原名", "同步后名称", "定位" }, 18, -1, -1, 80);
                foreach (var item in sceneNamePlan.Items.Skip(syncPage * PageSize).Take(PageSize))
                {
                    Rect[] cells = ResourceTheme.TableRow(18, -1, -1, 80);
                    item.Selected = EditorGUI.Toggle(cells[0], item.Selected);
                    ResourceTheme.AssetCell(cells[1], item.SourcePath, item.OldName);
                    ResourceTheme.Cell(cells[2], item.NewName, item.SourcePath);
                    if (GUI.Button(cells[3], "定位实例", ResourceTheme.Button) && item.Instance != null) EditorGUIUtility.PingObject(item.Instance);
                }
                using (new EditorGUI.DisabledScope(!sceneNamePlan.Items.Any(i => i.Selected)))
                    if (GUILayout.Button("同步勾选名称", ResourceTheme.Primary)) RunSafe(() =>
                    {
                        if (!EditorUtility.DisplayDialog("确认名称同步", "只修改当前场景中勾选实例的名称，不改源Prefab。可通过Ctrl+Z撤销。", "同步", "取消")) return;
                        int count = SceneNameSync.Apply(sceneNamePlan); sceneNamePlan = null;
                        InvalidateReferences(); InvalidateCleanup();
                        message = "已同步 " + count + " 个名称，场景尚未保存，可通过Undo撤销。";
                    });
            }
        }
    }
}
