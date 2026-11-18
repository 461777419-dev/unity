using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Suyao.ResourceOrganizer
{
    public sealed partial class ResourceOrganizerWindow : EditorWindow
    {
        [SerializeField] private int tab;
        [SerializeField] private Object source;
        [SerializeField] private DefaultAsset targetFolder;
        [SerializeField] private string targetPath = "Assets/Organized";
        [SerializeField] private ConflictPolicy conflict;
        [SerializeField] private int mergeSort;
        [SerializeField] private int mergeMode;
        [SerializeField] private List<Object> targets = new List<Object>();
        [SerializeField] private List<Object> cleanupTargets = new List<Object>();
        [SerializeField] private List<Object> keep = new List<Object>();
        [SerializeField] private bool allScenes = true;
        [SerializeField] private SceneAsset scene;
        [SerializeField] private TextAsset dynamicManifest;
        [SerializeField] private bool markProject = true;
        private bool dynamicReviewed;
        private bool showKeep;
        private string search = "", message = "", detailPath = "";
        private int filter, sort, page, mergePage;
        private int mergeFilter, mergeType;
        private string mergeSearch = "";
        private static readonly string[] MergeCategories = { "", "Folders", "Prefabs", "Models", "Materials", "Textures", "Animations", "Audio", "Other" };
        private int cleanupPage, cleanupFilter;
        private string cleanupSearch = "";
        private double nextMarkCheck;
        private Vector2 scroll;
        private readonly Vector2[] leftScroll = new Vector2[3];
        private readonly Vector2[] rightScroll = new Vector2[3];
        private bool manualCleanup;
        private MergePlan plan;
        private SceneQuery query;
        private CleanupCandidates cleanupCandidates;
        private CleanupPreview cleanup;
        private OperationReport lastReport;
        private EditorJob job;
        private readonly HashSet<string> markedGuids = new HashSet<string>(StringComparer.Ordinal);
        private const int PageSize = 80;

        [MenuItem("Tools/资源整理工具")]
        public static void Open()
        {
            GetWindow<ResourceOrganizerWindow>("资源整理工具");
        }

        private void OnEnable()
        {
            minSize = new Vector2(940, 620);
            job = new EditorJob();
            EditorApplication.projectWindowItemOnGUI += DrawProjectMark;
            EditorApplication.update += RepaintBusy;
            AssemblyReloadEvents.beforeAssemblyReload += BeforeReload;
            dynamicReviewed = false;
        }

        private void OnDisable()
        {
            if (job != null) job.Dispose();
            EditorApplication.projectWindowItemOnGUI -= DrawProjectMark;
            EditorApplication.update -= RepaintBusy;
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeReload;
            markedGuids.Clear();
            EditorApplication.RepaintProjectWindow();
            ResourceTheme.Dispose();
        }

        private void BeforeReload() { if (job != null) job.Dispose(); }
        private void RepaintBusy()
        {
            if (job != null && job.Running) Repaint();
            if (query != null && markedGuids.Count > 0 && EditorApplication.timeSinceStartup >= nextMarkCheck)
            {
                nextMarkCheck = EditorApplication.timeSinceStartup + 1;
                if (!query.IsCurrent) { markedGuids.Clear(); EditorApplication.RepaintProjectWindow(); Repaint(); }
            }
        }

        private void DrawProjectMark(string guid, Rect rect)
        {
            if (!markProject || query == null || !query.Complete || query.Revision != ProjectState.Revision || !markedGuids.Contains(guid)) return;
            ProjectReferenceMarker.Draw(guid, rect);
        }

        private void OnGUI()
        {
            using (ResourceTheme.Begin(position)) DrawWindow();
        }

        private void DrawWindow()
        {
            if (job == null) job = new EditorJob();
            GUILayout.Space(12);
            using (new EditorGUILayout.HorizontalScope(ResourceTheme.Panel))
            {
                GUILayout.Label("资源整理工具", ResourceTheme.Title);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(job.Running))
                    if (GUILayout.Button("未保存项 / 保存修改", GUILayout.Width(168))) RunSafe(ShowUnsavedItems);
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                string[] tabs = { "移动合并", "引用查询", "资源清理", "命名工具", "场景同步", "资源分析" };
                for (int i = 0; i < tabs.Length; i++)
                {
                    if (GUILayout.Button(tabs[i], i == tab ? ResourceTheme.ActiveTab : ResourceTheme.Tab, GUILayout.ExpandWidth(true)) && !job.Running)
                    { tab = i; scroll = Vector2.zero; }
                    if (i == tab) { Rect rect = GUILayoutUtility.GetLastRect(); EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - 2, rect.width, 2), ResourceTheme.Accent); }
                }
                GUILayout.FlexibleSpace();
            }
            GUILayout.Space(10);
            if (!string.IsNullOrEmpty(message))
            {
                using (new EditorGUILayout.HorizontalScope(ResourceTheme.Notice))
                {
                    GUILayout.Label(new GUIContent(message.Split('\n')[0].TrimEnd('\r'), message), ResourceTheme.PathText, GUILayout.ExpandWidth(true), GUILayout.Height(24));
                    if (GUILayout.Button("查看详情", GUILayout.Width(88))) EditorUtility.DisplayDialog("操作信息", message, "关闭");
                    if (GUILayout.Button("关闭提示", GUILayout.Width(88))) message = "";
                }
                GUILayout.Space(8);
            }
            if (job.Running)
            {
                Rect progress = GUILayoutUtility.GetRect(10, 22, GUILayout.ExpandWidth(true));
                EditorGUI.ProgressBar(progress, job.Progress, job.Message);
                if (GUILayout.Button("取消扫描 / 复核"))
                {
                    job.Dispose();
                    if (tab == 0) plan = null;
                    else if (tab == 1) InvalidateReferences();
                    else if (tab == 2) InvalidateCleanup();
                    else if (tab == 5) analysis = null;
                    message = "任务已取消，不完整结果已作废；没有移动或删除资源。";
                }
            }
            using (new EditorGUI.DisabledScope(job.Running || EditorApplication.isCompiling || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                if (tab == 0) DrawMerge();
                else if (tab == 1) DrawReferences();
                else if (tab == 2) DrawCleanup();
                else
                {
                    scroll = EditorGUILayout.BeginScrollView(scroll, false, false);
                    if (tab == 3) DrawRenamer();
                    else if (tab == 4) DrawSceneSync();
                    else DrawAnalysis();
                    EditorGUILayout.EndScrollView();
                }
                if (tab == 0) DrawMergeActions();
                if (tab == 2) DrawCleanupActions();
            }
            if (lastReport != null)
            {
                ResourceTheme.LabelField(lastReport.summary, ResourceTheme.Wrap);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("查看最近操作日志")) EditorUtility.RevealInFinder(lastReport.SavedPath);
                    if (GUILayout.Button("导出最近操作日志")) RunSafe(() => ExportReport(lastReport));
                    if (GUILayout.Button("查看执行结果")) ShowMergeResult(lastReport);
                }
            }
        }

        private void DrawMerge()
        {
            DrawSplit(0, DrawMergeConfiguration, DrawMergePreview);
        }

        private void DrawSplit(int index, Action configuration, Action results)
        {
            using (new EditorGUILayout.HorizontalScope(GUILayout.ExpandHeight(true)))
            {
                GUILayout.Space(10);
                using (new EditorGUILayout.VerticalScope(ResourceTheme.Panel, GUILayout.Width(ConfigWidth), GUILayout.ExpandHeight(true)))
                {
                    leftScroll[index] = EditorGUILayout.BeginScrollView(leftScroll[index], false, true, GUILayout.ExpandHeight(true));
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(ConfigWidth - 56))) configuration();
                    EditorGUILayout.EndScrollView();
                }
                GUILayout.Space(12);
                using (new EditorGUILayout.VerticalScope(ResourceTheme.Panel, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true)))
                {
                    rightScroll[index] = EditorGUILayout.BeginScrollView(rightScroll[index], false, true, GUILayout.ExpandHeight(true));
                    using (new EditorGUILayout.VerticalScope(GUILayout.Width(Mathf.Max(420, position.width - ConfigWidth - 90)))) results();
                    EditorGUILayout.EndScrollView();
                }
                GUILayout.Space(10);
            }
        }

        private void DrawMergeActions()
        {
            GUILayout.Space(12);
            using (new EditorGUILayout.HorizontalScope(ResourceTheme.Panel))
            {
                GUILayout.Label(plan == null ? "先生成预览，再执行移动合并" : "已选移动 " + plan.SelectedMoves +
                    " 项  ·  跳过 " + plan.Items.Count(i => i.Action == MergeAction.Skip) + " 项  ·  阻断 " + plan.Items.Count(i => i.Action == MergeAction.Blocked) + " 项", ResourceTheme.Muted);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("生成合并预览", GUILayout.Width(145), GUILayout.Height(36))) RunSafe(StartMergePreview);
                using (new EditorGUI.DisabledScope(plan == null || plan.HasErrors || !plan.HasWork || !plan.IsCurrent))
                    if (GUILayout.Button("执行合并", ResourceTheme.Primary, GUILayout.Width(145))) RunSafe(ExecuteMerge);
            }
            if (plan != null) GUILayout.Label(plan.UnavailableReason, ResourceTheme.MiniWrap);
        }

        private float ConfigWidth { get { return Mathf.Clamp(position.width * .32f, 330, 390); } }

        private void DrawMergeConfiguration()
        {
            GUILayout.Label("合并配置", ResourceTheme.Section);
            EditorGUI.BeginChangeCheck();
            mergeMode = ResourceTheme.Popup("整理方式", mergeMode, new[] { "按原目录合并", "按资源类型整理", "预制体及依赖整理" });
            if (mergeMode == 1)
                GUILayout.Label("将源范围内文件按类型放入目标的 Prefabs / Models / Materials / Textures / Animations / Audio / Other 子目录。", ResourceTheme.MiniWrap);
            if (mergeMode == 2)
            {
                dependencyScope = (DefaultAsset)ResourceTheme.AssetSlot("依赖范围（可选文件夹）", dependencyScope, typeof(DefaultAsset));
                GUILayout.Label("留空使用源文件夹或源Prefab所在目录。共享仅统计本次所选Prefab，默认不勾选；不会判断工程其他资源的使用情况。", ResourceTheme.MiniWrap);
            }
            source = ResourceTheme.AssetSlot("需要移动文件", source, typeof(Object));
            if (GUILayout.Button("使用当前选择")) { source = Selection.activeObject; plan = null; }
            GUILayout.Label("↓", ResourceTheme.Arrow);
            DefaultAsset selectedFolder = (DefaultAsset)ResourceTheme.AssetSlot("目标文件", targetFolder, typeof(DefaultAsset));
            if (selectedFolder != targetFolder)
            {
                targetFolder = selectedFolder;
                if (selectedFolder != null) targetPath = AssetDatabase.GetAssetPath(selectedFolder);
            }
            GUILayout.Space(8);
            GUILayout.Label("目标路径（不存在则创建）", ResourceTheme.Muted);
            string typedPath = ResourceTheme.TextField(null, targetPath);
            if (typedPath != targetPath)
            {
                targetPath = typedPath;
                targetFolder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(ResourcePath.Normalize(targetPath));
            }
            GUILayout.Space(8);
            if (GUILayout.Button("交换移动位置"))
            {
                Object old = source;
                source = AssetDatabase.LoadAssetAtPath<DefaultAsset>(targetPath);
                targetPath = AssetDatabase.GetAssetPath(old);
                targetFolder = old as DefaultAsset;
                plan = null;
            }
            GUILayout.Space(16);
            GUILayout.Label("同名文件处理", ResourceTheme.Section);
            conflict = (ConflictPolicy)ResourceTheme.Popup((int)conflict, new[] { "跳过，保留需要移动文件", "自动改名后移动", "发现冲突则取消整批" });
            GUILayout.Space(12);
            GUILayout.Label("仅检查移动范围；空源目录保留，以保留目录 GUID。", ResourceTheme.MiniWrap);
            GUILayout.Space(12);
            GUILayout.Label("移动保留资源 GUID；代码或配置中的字符串路径需另行核对。", ResourceTheme.MiniWrap);
            if (EditorGUI.EndChangeCheck()) plan = null;
        }

        private void DrawMergePreview()
        {
            GUILayout.Label("合并预览", ResourceTheme.Section);
            GUILayout.Space(10);
            if (plan == null)
            {
                GUILayout.Space(56);
                GUILayout.Label("尚未生成合并预览", ResourceTheme.Section);
                GUILayout.Label("选择左侧的需要移动文件与目标文件，点击下方“生成合并预览”。", ResourceTheme.Wrap);
                GUILayout.Space(160);
                return;
            }
            GUILayout.Label(new GUIContent(plan.Source + " → " + plan.Target, plan.Source + " → " + plan.Target), ResourceTheme.MiniWrap);
            if (!plan.IsCurrent) ResourceTheme.Help("工程资源已变化，必须重新生成预览。", MessageType.Warning);
            EditorGUI.BeginChangeCheck();
            mergeSearch = ResourceTheme.TextField("搜索路径", mergeSearch);
            mergeFilter = ResourceTheme.Popup("显示状态", mergeFilter, new[] { "全部", "仅冲突", "可移动", "仅阻断", "未勾选" });
            mergeType = ResourceTheme.Popup("资源类型", mergeType, new[] { "全部", "目录", "预制体", "模型", "材质", "贴图", "动画", "音频", "其他" });
            if (EditorGUI.EndChangeCheck()) mergePage = 0;
            int nextSort = ResourceTheme.Popup("预览排序", mergeSort, new[] { "默认路径顺序", "冲突优先", "按资源类型" });
            if (nextSort != mergeSort) { mergeSort = nextSort; mergePage = 0; }
            GUILayout.Label("冲突 " + plan.Items.Count(i => i.IsConflict) + " 项；排序仅影响显示，不改变执行顺序。", ResourceTheme.Muted);
            IEnumerable<MergeItem> filtered = plan.Items.Where(i =>
                (string.IsNullOrEmpty(mergeSearch) || i.Source.IndexOf(mergeSearch, StringComparison.OrdinalIgnoreCase) >= 0 || i.Target.IndexOf(mergeSearch, StringComparison.OrdinalIgnoreCase) >= 0) &&
                (mergeType == 0 || i.Category == MergeCategories[mergeType]) &&
                (mergeFilter == 0 || mergeFilter == 1 && i.IsConflict || mergeFilter == 2 && i.Action == MergeAction.Move ||
                    mergeFilter == 3 && i.Action == MergeAction.Blocked || mergeFilter == 4 && i.Action == MergeAction.Move && !i.Selected));
            // Materialize before bulk selection so the current filtered set is stable.
            MergeItem[] shown = filtered.ToArray();
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(!plan.IsCurrent))
                {
                    if (GUILayout.Button("全选筛选结果")) foreach (var item in shown.Where(i => i.Action == MergeAction.Move)) item.Selected = true;
                    if (GUILayout.Button("取消筛选结果")) foreach (var item in shown.Where(i => i.Action == MergeAction.Move)) item.Selected = false;
                }
                if (GUILayout.Button("导出预览 CSV")) RunSafe(ExportMergePreview);
            }
            GUILayout.Label("显示 " + shown.Length + " / " + plan.Items.Count + " 项；目录行勾选后整体移动目录。", ResourceTheme.Muted);
            IEnumerable<MergeItem> previewItems = shown
                .OrderBy(i => mergeSort == 1 && i.IsConflict ? 0 : 1)
                .ThenBy(i => mergeSort == 2 ? i.Category : "", StringComparer.Ordinal)
                .ThenBy(i => string.IsNullOrEmpty(i.Source) ? i.Target : i.Source, StringComparer.Ordinal)
                .ThenBy(i => i.Target, StringComparer.Ordinal);
            DrawPages(shown.Length, ref mergePage);
            ResourceTheme.TableHeader(new[] { "", "状态", "来源", "目标", "定位" }, 18, 84, -1, -1, 44);
            foreach (MergeItem item in previewItems.Skip(mergePage * PageSize).Take(PageSize))
            {
                Rect[] cells = ResourceTheme.TableRow(18, 84, -1, -1, 44);
                using (new EditorGUI.DisabledScope(item.Action != MergeAction.Move || !plan.IsCurrent))
                    item.Selected = EditorGUI.Toggle(cells[0], item.Action == MergeAction.Move && item.Selected);
                ResourceTheme.Cell(cells[1], (item.IsConflict ? "冲突·" : "") + ActionName(item.Action), item.Reason);
                string from = string.IsNullOrEmpty(item.Source) ? item.Target : item.Source;
                ResourceTheme.AssetCell(cells[2], from, string.IsNullOrEmpty(item.Source) ? "（新建）" : Path.GetFileName(item.Source), tooltip: item.Source + "\n" + item.Reason);
                ResourceTheme.Cell(cells[3], Path.GetFileName(item.Target), item.Target + "\n" + item.Reason);
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(item.Source)))
                    if (GUI.Button(cells[4], "定位", ResourceTheme.Button)) EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(item.Source));
            }
        }

        private void StartMergePreview()
        {
            // Preview changes no assets. Require saved content only before actual operations.
            AssetUtility.RequireEditMode();
            if (source == null) throw new InvalidOperationException("请先选择“需要移动文件”（支持文件或文件夹）。");
            string sourcePath = AssetDatabase.GetAssetPath(source);
            string destination = targetPath;
            ConflictPolicy policy = conflict;
            plan = null;
            DependencyCollection dependencies = null;
            if (mergeMode == 2) dependencies = DependencyOrganizer.Collect(sourcePath, dependencyScope == null ? null : AssetDatabase.GetAssetPath(dependencyScope));
            plan = MergeService.Plan(sourcePath, destination, policy, mergeMode == 1, dependencies?.Candidates);
            if (dependencies != null)
                message = "依赖范围：" + dependencies.Scope + "；选定Prefab " + dependencies.PrefabCount + "，已排除 " + dependencies.Excluded + " 个范围外或不支持的依赖。共享仅统计选定Prefab。";
            mergePage = 0;
            mergeFilter = mergeType = 0;
            mergeSearch = "";
            if (dependencies == null) message = plan.HasErrors ? "有阻断项，未移动任何资源。请处理后重新预览。" : "局部预览已生成，未扫描全工程引用。请核对目标文件与冲突策略。";
        }

        private void ExecuteMerge()
        {
            try
            {
                if (plan == null || plan.HasErrors || !plan.HasWork || !plan.IsCurrent)
                    throw new InvalidOperationException(plan == null ? "请先生成合并预览。" : plan.UnavailableReason);
                if (!EditorUtility.DisplayDialog("确认移动合并", "即将移动已勾选的 " + plan.SelectedMoves + " 项，并创建所需目录。同名文件不会被覆盖。\n空源目录保留。字符串路径和目录级配置需另行核对。", "执行合并", "取消"))
                { message = "已取消，未移动任何文件。"; return; }
                lastReport = MergeService.Execute(plan);
                plan = null;
                InvalidateReferences();
                InvalidateCleanup();
                message = lastReport.summary;
                ShowMergeResult(lastReport);
            }
            catch (ExitGUIException) { throw; }
            catch (Exception exception)
            {
                message = "合并未完成：" + exception.Message;
                Debug.LogWarning(message);
                EditorUtility.DisplayDialog("合并未完成", message, "关闭");
            }
        }

        private static void ShowMergeResult(OperationReport report)
        {
            string details = report.summary;
            foreach (var entry in report.entries.Where(e => e.status == "失败" || e.status == "未执行").Take(8))
                details += "\n\n" + entry.status + "：" + entry.source + " → " + entry.destination + "\n" + entry.reason;
            EditorUtility.DisplayDialog("执行结果", details + "\n\n完整记录可通过“查看最近操作日志”打开。", "关闭");
        }

        private void ExportMergePreview()
        {
            string path = EditorUtility.SaveFilePanel("导出合并预览", "", "合并预览.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;
            var csv = new StringBuilder("勾选移动,状态,类型,冲突,来源,目标,原因,GUID\r\n");
            foreach (var item in plan.Items)
                csv.AppendLine(string.Join(",", new[] { item.Action == MergeAction.Move && item.Selected ? "是" : "否", ActionName(item.Action), item.Category,
                    item.IsConflict ? "是" : "否", item.Source, item.Target, item.Reason, item.Guid }.Select(Csv)));
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
            message = "已导出完整预览（含当前筛选隐藏的项目）：" + path;
        }

        private void DrawReferences()
        {
            DrawSplit(1, DrawReferenceConfiguration, DrawReferenceResults);
        }

        private void DrawReferenceConfiguration()
        {
            GUILayout.Label("查询配置", ResourceTheme.Section);
            GUILayout.Label("选择范围 → 查询引用 → 未引用资源送入清理", ResourceTheme.MiniWrap);
            GUILayout.Space(14);
            if (DrawObjects("待检查资源 / 文件夹", targets)) InvalidateReferences();
            EditorGUI.BeginChangeCheck();
            allScenes = ResourceTheme.Popup("场景范围", allScenes ? 0 : 1, new[] { "工程全部场景", "指定一个场景" }) == 0;
            if (!allScenes) scene = (SceneAsset)ResourceTheme.ObjectField("指定场景插槽", scene, typeof(SceneAsset), false);
            if (EditorGUI.EndChangeCheck()) InvalidateReferences();
            bool newMark = ResourceTheme.ToggleLeft("在 Project 中标红已引用资源", markProject);
            if (newMark != markProject) { markProject = newMark; EditorApplication.RepaintProjectWindow(); }
            if (GUILayout.Button("扫描场景引用", ResourceTheme.Primary)) RunSafe(StartQuery);
            GUILayout.Space(12);
            GUILayout.Label("红色表示已引用（含间接引用）。未引用候选需要在清理页复核，才能移入回收站。", ResourceTheme.MiniWrap);
        }

        private void DrawReferenceResults()
        {
            GUILayout.Label("引用查询结果", ResourceTheme.Section);
            GUILayout.Space(12);
            if (query == null)
            {
                GUILayout.Label("在左侧选择资源与场景范围，开始扫描。", ResourceTheme.Muted);
                GUILayout.Space(260);
                return;
            }
            bool current = query.IsCurrent;
            ResourceTheme.LabelField("扫描范围", query.Label);
            ResourceTheme.LabelField("统计", "场景 " + query.SceneCount + " · 文件 " + query.Rows.Count +
                " · 已引用 " + query.Rows.Count(r => r.Scenes.Count > 0) + " · 失败 " + query.Errors.Count);
            if (!current)
            {
                ResourceTheme.Help("结果不完整或已过期，请重新扫描。过期结果不用于标红。", MessageType.Warning);
                if (query.Errors.Count > 0) ResourceTheme.Help(string.Join("\n", query.Errors.Take(8)), MessageType.Error);
            }
            if (ProjectState.HasDirtyScenes()) ResourceTheme.Help("存在未保存或无路径场景：查询结果仅反映磁盘版本。", MessageType.Warning);
            EditorGUI.BeginChangeCheck();
            search = ResourceTheme.TextField("搜索名称 / 路径 / 类型", search);
            using (new EditorGUILayout.HorizontalScope())
            {
                filter = ResourceTheme.Popup(filter, new[] { "全部", "已引用", "本范围未引用" }, GUILayout.Width(140));
                sort = ResourceTheme.Popup(sort, new[] { "按路径", "按类型" }, GUILayout.Width(100));
                GUILayout.FlexibleSpace();
            }
            if (EditorGUI.EndChangeCheck()) page = 0;
            using (new EditorGUI.DisabledScope(!current))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("导出查询 CSV")) RunSafe(ExportQuery);
            }
            int unreferenced = query.Rows.Count(r => r.Scenes.Count == 0 && ResourcePath.IsAsset(r.Path));
            using (new EditorGUI.DisabledScope(!current || unreferenced == 0))
                if (GUILayout.Button("送入清理（" + unreferenced + " 项）", ResourceTheme.Primary)) RunSafe(SendQueryToCleanup);
            GUILayout.Label("送入当前查询的全部未引用Assets文件，不受列表筛选影响。", ResourceTheme.MiniWrap);
            IEnumerable<ReferenceRow> visible = query.Rows;
            if (!string.IsNullOrWhiteSpace(search)) visible = visible.Where(r =>
                r.Path.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || r.Type.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
            if (filter == 1) visible = visible.Where(r => r.Scenes.Count > 0);
            if (filter == 2) visible = visible.Where(r => r.Scenes.Count == 0);
            ReferenceRow[] displayed = (sort == 0 ? visible.OrderBy(r => r.Path, StringComparer.Ordinal)
                : visible.OrderBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Path, StringComparer.Ordinal)).ToArray();
            DrawPages(displayed.Length, ref page);
            ResourceTheme.TableHeader(new[] { "文件", "引用状态", "定位", "详情" }, -1, 126, 44, 44);
            foreach (ReferenceRow row in displayed.Skip(page * PageSize).Take(PageSize)) DrawRow(row, current);
            DrawDetails(current);
        }

        private void DrawCleanup()
        {
            DrawSplit(2, DrawCleanupConfiguration, DrawCleanupResults);
        }

        private void DrawCleanupConfiguration()
        {
            GUILayout.Label("清理配置", ResourceTheme.Section);
            GUILayout.Label("载入查询未引用结果 → 勾选 → 移入回收站", ResourceTheme.MiniWrap);
            GUILayout.Space(14);
            using (new EditorGUI.DisabledScope(query == null || !query.IsCurrent))
                if (GUILayout.Button("载入未引用资源", ResourceTheme.Primary)) RunSafe(SendQueryToCleanup);
            if (cleanupCandidates != null) GUILayout.Label("候选来源\n" + cleanupCandidates.Origin + "\n候选 " + cleanupCandidates.Rows.Count +
                " 项 · 当前勾选 " + cleanupCandidates.Rows.Count(r => r.Selected) + " 项", ResourceTheme.MiniWrap);
            GUILayout.Space(14);
            manualCleanup = ResourceTheme.Foldout(manualCleanup, "手动添加范围", true);
            if (manualCleanup)
            {
                if (DrawObjects("待清理资源 / 文件夹", cleanupTargets))
                { cleanupCandidates = null; InvalidateCleanup(); }
                if (GUILayout.Button("载入手动范围")) RunSafe(StartLoadCleanup);
            }
            if (cleanupCandidates == null || !cleanupCandidates.FromReferenceQuery)
            showKeep = ResourceTheme.Foldout(showKeep, "保留与加载设置", true);
            if (showKeep && (cleanupCandidates == null || !cleanupCandidates.FromReferenceQuery))
            {
                if (DrawObjects("额外保留资源 / 文件夹", keep)) InvalidateCleanup();
                EditorGUI.BeginChangeCheck();
                dynamicManifest = (TextAsset)ResourceTheme.ObjectField("动态加载清单（.txt）", dynamicManifest, typeof(TextAsset), false);
                if (EditorGUI.EndChangeCheck()) InvalidateCleanup();
                ResourceTheme.LabelField("清单每行一个 Assets 路径或 GUID；# 开头为注释。目录包含子资源。", ResourceTheme.MiniWrap);
            }
            GUILayout.Space(14);
            GUILayout.Label(cleanupCandidates != null && cleanupCandidates.FromReferenceQuery
                ? "按上方查询范围的未引用结果直接清理，不再扫描全工程。"
                : "手动范围尚未查询引用，需要先进行全工程复核。", ResourceTheme.MiniWrap);
        }

        private void DrawCleanupResults()
        {
            GUILayout.Label("候选资源与清理预览", ResourceTheme.Section);
            GUILayout.Space(12);
            if (cleanupCandidates == null)
            {
                GUILayout.Label("在引用查询中将未引用资源送入本页，或展开左侧手动添加范围。", ResourceTheme.Wrap);
                GUILayout.Space(260);
                return;
            }
            bool current = cleanupCandidates.IsCurrent;
            if (!current) ResourceTheme.Help("资源列表已过期，请重新加载。", MessageType.Warning);
            cleanupSearch = ResourceTheme.TextField("搜索名称 / 路径 / 类型", cleanupSearch);
            using (new EditorGUILayout.HorizontalScope())
            {
                cleanupFilter = ResourceTheme.Popup(cleanupFilter, new[] { "全部", "已选中", "已复核可清理", "已复核受保护" }, GUILayout.Width(150));
            }
            using (new EditorGUI.DisabledScope(!current))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("选择全部美术候选"))
                {
                    foreach (var row in cleanupCandidates.Rows) row.Selected = ResourcePath.CanCleanType(row.Path);
                    InvalidateCleanup();
                }
                if (GUILayout.Button("仅选已复核可清理"))
                {
                    if (cleanup != null && cleanup.Matches(cleanupCandidates.Rows))
                    {
                        foreach (var row in cleanupCandidates.Rows) row.Selected = row.Cleanable;
                        cleanup.SelectionKey = CleanupPreview.Key(cleanupCandidates.Rows);
                    }
                }
                if (GUILayout.Button("取消选择"))
                {
                    foreach (var row in cleanupCandidates.Rows) row.Selected = false;
                    InvalidateCleanup();
                }
            }
            bool reviewed = cleanup != null && current && cleanup.Matches(cleanupCandidates.Rows);
            IEnumerable<ReferenceRow> visible = cleanupCandidates.Rows;
            if (!string.IsNullOrWhiteSpace(cleanupSearch)) visible = visible.Where(r =>
                r.Path.IndexOf(cleanupSearch, StringComparison.OrdinalIgnoreCase) >= 0 || r.Type.IndexOf(cleanupSearch, StringComparison.OrdinalIgnoreCase) >= 0);
            if (cleanupFilter == 1) visible = visible.Where(r => r.Selected);
            if (cleanupFilter == 2) visible = visible.Where(r => reviewed && r.Cleanable);
            if (cleanupFilter == 3) visible = visible.Where(r => reviewed && cleanup.Decision.Blocked.ContainsKey(r.Path));
            ReferenceRow[] displayed = visible.OrderBy(r => r.Path, StringComparer.Ordinal).ToArray();
            ResourceTheme.LabelField("文件 " + cleanupCandidates.Rows.Count + " · 已选 " + cleanupCandidates.Rows.Count(r => r.Selected));
            DrawPages(displayed.Length, ref cleanupPage);
            ResourceTheme.TableHeader(new[] { "", "文件", "清理状态", "定位" }, 18, -1, -1, 44);
            foreach (var row in displayed.Skip(cleanupPage * PageSize).Take(PageSize))
            {
                Rect[] cells = ResourceTheme.TableRow(18, -1, -1, 44);
                using (new EditorGUI.DisabledScope(!current))
                {
                    bool selected = EditorGUI.Toggle(cells[0], row.Selected);
                    if (selected != row.Selected) { row.Selected = selected; InvalidateCleanup(); reviewed = false; }
                }
                ResourceTheme.AssetCell(cells[1], row.Path, tooltip: row.Path + "\n" + row.Type);
                string status = !current ? "列表已过期" : cleanupCandidates.FromReferenceQuery ? "未引用 · 可直接清理" : reviewed && cleanup != null ? row.CleanupReason : "尚未复核";
                ResourceTheme.Cell(cells[2], status);
                if (GUI.Button(cells[3], "定位", ResourceTheme.Button)) EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(row.Path));
            }
            if (reviewed)
            {
                GUILayout.Label("通过复核的实际清理文件", ResourceTheme.Section);
                foreach (string path in cleanup.Decision.Allowed.OrderBy(p => p, StringComparer.Ordinal))
                    GUILayout.Label(path, ResourceTheme.MiniWrap);
            }
        }

        private void DrawCleanupActions()
        {
            if (cleanupCandidates == null) return;
            bool current = cleanupCandidates.IsCurrent;
            if (cleanupCandidates.FromReferenceQuery)
            {
                int selected = cleanupCandidates.Rows.Count(r => r.Selected);
                using (new EditorGUILayout.VerticalScope(ResourceTheme.Notice))
                {
                    GUILayout.Label("已选 " + selected + " 项 · 使用已有引用查询结果，无需重复复核", ResourceTheme.Muted);
                    using (new EditorGUI.DisabledScope(!current || selected == 0))
                        if (GUILayout.Button("删除所选（移入回收站）", ResourceTheme.Primary)) RunSafe(TrashSelected);
                    GUILayout.Label(current ? "查询范围：" + cleanupCandidates.Origin : "查询结果已过期，请重新查询并载入。", ResourceTheme.MiniWrap);
                }
                return;
            }
            using (new EditorGUILayout.VerticalScope(ResourceTheme.Notice))
            {
            bool previewValid = cleanup != null && current && cleanup.Matches(cleanupCandidates.Rows);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(previewValid ? "可清理 " + cleanup.Decision.Allowed.Count + " 项 · " + EditorUtility.FormatBytes(cleanup.Bytes) +
                    " · 受保护 " + cleanup.Decision.Blocked.Count + " 项" : "已选 " + cleanupCandidates.Rows.Count(r => r.Selected) + " 项 · 等待全工程复核", ResourceTheme.Muted);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(!current || !cleanupCandidates.Rows.Any(r => r.Selected)))
                    if (GUILayout.Button("全工程复核", GUILayout.Width(140), GUILayout.Height(36))) RunSafe(StartCleanup);
                using (new EditorGUI.DisabledScope(!previewValid || !dynamicReviewed || cleanup.Decision.Allowed.Count == 0 || ProjectState.HasDirtyScenes()))
                    if (GUILayout.Button("移入回收站（" + (previewValid ? cleanup.Decision.Allowed.Count : 0) + " 项）", ResourceTheme.Primary, GUILayout.Width(180))) RunSafe(TrashSelected);
            }
            dynamicReviewed = ResourceTheme.ToggleLeft("已核对代码/配置中的动态加载，所需资源已加入保留列表或清单", dynamicReviewed);
            if (!previewValid) GUILayout.Label("尚无有效复核结果，不能清理；修改勾选或保护设置后须重新复核。", ResourceTheme.MiniWrap);
            else if (cleanup.Decision.Allowed.Count == 0) GUILayout.Label("复核完成：所选文件均受保护。请查看每项的保护原因，不会强制删除。", ResourceTheme.MiniWrap);
            else if (ProjectState.HasDirtyScenes()) GUILayout.Label("存在未保存场景，请先保存并重新复核。", ResourceTheme.MiniWrap);
            else if (!dynamicReviewed) GUILayout.Label("复核已通过：核对上方动态加载确认项并勾选后，即可移入回收站。", ResourceTheme.MiniWrap);
            if (previewValid && cleanup.Index.IgnoredIndexRecords.Count > 0)
                GUILayout.Label("已排除FR2的GUID索引字符串；真实对象引用仍受保护。", ResourceTheme.MiniWrap);
            }
        }

        private void DrawRow(ReferenceRow row, bool current)
        {
            Rect[] cells = ResourceTheme.TableRow(-1, 126, 44, 44);
            ResourceTheme.AssetCell(cells[0], row.Path, textColor: !current ? Color.gray : row.Scenes.Count > 0 ? new Color(.78f, .13f, .18f) : ResourceTheme.Ink, tooltip: row.Path + "\n" + row.Type);
            ResourceTheme.Cell(cells[1], !current ? "过期 / 未知" : row.Scenes.Count > 0 ? "已引用 · " + row.Scenes.Count + " 场景" : "本范围未引用");
            if (GUI.Button(cells[2], "定位", ResourceTheme.Button)) EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(row.Path));
            if (GUI.Button(cells[3], "详情", ResourceTheme.Button)) detailPath = detailPath == row.Path ? "" : row.Path;
        }

        private void DrawDetails(bool current)
        {
            ReferenceRow row = query.Rows.FirstOrDefault(r => r.Path == detailPath);
            if (row == null) return;
            ResourceTheme.LabelField("引用来源：" + row.Path, ResourceTheme.Section);
            if (row.Scenes.Count == 0) ResourceTheme.LabelField("本次场景范围未检出引用。", ResourceTheme.Wrap);
            foreach (string scenePath in row.Scenes.Take(30))
                using (new EditorGUILayout.HorizontalScope())
                {
                    ResourceTheme.SelectableLabel(scenePath, GUILayout.Height(18));
                    using (new EditorGUI.DisabledScope(!current))
                        if (GUILayout.Button("引用链", GUILayout.Width(60))) RunSafe(() =>
                        {
                            try { EditorUtility.DisplayDialog("文件级引用链", SceneQuery.FindChain(scenePath, row.Path), "关闭"); }
                            finally { EditorUtility.ClearProgressBar(); }
                        });
                }
            if (row.Scenes.Count > 30) ResourceTheme.LabelField("其余来源请查看导出的 CSV。", ResourceTheme.Muted);
        }

        private void StartQuery()
        {
            if (targets.Count == 0) throw new InvalidOperationException("请添加待检查资源或文件夹。");
            if (!allScenes && scene == null) throw new InvalidOperationException("请拖入指定场景。");
            bool diskOnly = ProjectState.HasDirtyScenes();
            if (diskOnly && !EditorUtility.DisplayDialog("场景尚未保存", "只查询磁盘上已保存的场景版本？未保存内容不计入结果，清理会被禁用。", "仅查询磁盘版本", "取消")) return;
            InvalidateReferences();
            var next = new SceneQuery();
            StartJob(next.Scan(targets.Where(o => o != null).ToArray(), scene, allScenes, job), () =>
            {
                query = next;
                page = 0;
                foreach (var row in query.Rows.Where(r => r.Scenes.Count > 0)) markedGuids.Add(row.Guid);
                markProject = true;
                EditorApplication.RepaintProjectWindow();
                message = next.Complete ? "场景引用扫描完成。" : "扫描有失败，结果不完整。";
            });
        }

        private void StartLoadCleanup()
        {
            AssetUtility.RequireEditMode();
            if (!cleanupTargets.Any(o => o != null)) throw new InvalidOperationException("请添加待清理资源或文件夹。");
            cleanupCandidates = null;
            InvalidateCleanup();
            var next = new CleanupCandidates();
            StartJob(next.Load(cleanupTargets.ToArray(), job), () =>
            {
                cleanupCandidates = next;
                cleanupPage = 0;
                message = "待清理资源已加载。请选择候选文件，再进行全工程复核。";
            });
        }

        private void SendQueryToCleanup()
        {
            AssetUtility.RequireEditMode();
            var next = CleanupCandidates.FromQuery(query);
            if (cleanupCandidates != null && cleanupCandidates.Rows.Count > 0 &&
                !EditorUtility.DisplayDialog("替换清理候选", "将用当前引用查询的未引用资源替换清理候选及其复核结果。", "替换", "取消")) return;
            cleanupCandidates = next;
            InvalidateCleanup();
            cleanupPage = cleanupFilter = 0;
            cleanupSearch = "";
            leftScroll[2] = rightScroll[2] = Vector2.zero;
            tab = 2;
            message = "已载入 " + next.Rows.Count + " 项查询范围内未引用资源，可勾选后直接移入回收站，无需重复复核。";
        }

        private void StartCleanup()
        {
            AssetUtility.RequireClean();
            if (cleanupCandidates == null || !cleanupCandidates.IsCurrent) throw new InvalidOperationException("请先加载待清理资源列表。");
            if (!cleanupCandidates.Rows.Any(r => r.Selected)) throw new InvalidOperationException("请先勾选需要复核的文件。");
            InvalidateCleanup();
            var next = new CleanupPreview();
            StartJob(next.Review(cleanupCandidates.Rows, keep.Where(o => o != null).ToArray(), dynamicManifest, job), () =>
            {
                cleanup = next;
                message = "清理复核完成；请查看实际文件与保护原因，并确认动态加载清单。";
            });
        }

        private void TrashSelected()
        {
            bool fromQuery = cleanupCandidates != null && cleanupCandidates.FromReferenceQuery;
            if (fromQuery) cleanup = CleanupPreview.FromQueryResult(cleanupCandidates);
            else if (!dynamicReviewed || cleanup == null || cleanupCandidates == null) throw new InvalidOperationException("尚未完成清理确认。");
            if (cleanup.Decision.Allowed.Count == 0) throw new InvalidOperationException("请勾选需要删除的未引用资源。");
            string confirmation = fromQuery
                ? "按已有查询结果删除 " + cleanup.Decision.Allowed.Count + " 个所选未引用文件。\n" + cleanupCandidates.Origin + "\n本次不进行全工程复核（包含其他独立资源及动态加载用途的检查）。\n删除后可从系统回收站恢复，Unity Undo 不用于恢复文件。"
                : "即将移入回收站 " + cleanup.Decision.Allowed.Count + " 个文件。\n本窗口已列出实际文件。任意字符串动态加载不能由静态分析完全证明。\n建议先提交版本控制；Unity Undo 不用于恢复这些文件。";
            if (!EditorUtility.DisplayDialog("确认移入系统回收站", confirmation, "移入回收站", "取消")) return;
            lastReport = cleanup.Trash(cleanupCandidates.Rows);
            InvalidateReferences();
            InvalidateCleanup();
            message = lastReport.summary;
        }

        private void InvalidateReferences()
        {
            query = null;
            detailPath = "";
            markedGuids.Clear();
            EditorApplication.RepaintProjectWindow();
        }

        private void InvalidateCleanup()
        {
            cleanup = null;
            dynamicReviewed = false;
            if (cleanupCandidates != null) foreach (ReferenceRow row in cleanupCandidates.Rows) { row.Cleanable = false; row.CleanupReason = "尚未清理复核"; }
        }

        private void StartJob(IEnumerator routine, Action completed)
        {
            message = "正在扫描，请等待；可以取消。";
            job.Start(routine, () => RunSafe(completed), error =>
            {
                if (tab == 0) plan = null;
                else if (tab == 2) InvalidateCleanup();
                message = "任务未完成：" + error;
                Repaint();
            });
        }

        private void RunSafe(Action action)
        {
            try { action(); }
            catch (ExitGUIException) { throw; }
            catch (InvalidOperationException exception) { message = exception.Message; Debug.LogWarning(message); }
            catch (Exception exception) { message = exception.Message; Debug.LogException(exception); }
            Repaint();
        }

        private void ShowUnsavedItems()
        {
            AssetUtility.RequireEditMode();
            var scenes = ProjectState.UnsavedScenes();
            var assets = ProjectState.UnsavedAssets();
            if (scenes.Count == 0 && assets.Count == 0)
            {
                message = "未发现需要保存的工程场景或资源。Unity 内部缓存与预览场景不会计入。";
                return;
            }
            string details = "场景（" + scenes.Count + "）：\n" + string.Join("\n", scenes) +
                "\n\n资源（" + assets.Count + "）：\n" + string.Join("\n", assets);
            Debug.Log("资源整理工具 · 未保存项\n" + details);
            var prefabStage = PrefabStageUtility.GetCurrentPrefabStage();
            if (prefabStage != null && prefabStage.scene.isDirty)
            {
                message = "请先保存或退出 Prefab 编辑模式，再使用保存入口。\n" + details;
                return;
            }
            if (!EditorUtility.DisplayDialog("未保存的工程内容", details +
                "\n\n是否保存以上场景和工程资源？完整列表也已输出到 Console。保存后需要重新生成预览或扫描。", "保存修改", "暂不保存")) return;
            // New scenes may require a path. Never continue an operation when saving is cancelled.
            if (!EditorSceneManager.SaveOpenScenes())
            {
                plan = null;
                InvalidateReferences();
                InvalidateCleanup();
                cleanupCandidates = null;
                message = "场景保存已取消或失败；没有移动或清理资源。请保存后重新生成预览或扫描。";
                return;
            }
            AssetDatabase.SaveAssets();
            plan = null;
            InvalidateReferences();
            InvalidateCleanup();
            cleanupCandidates = null;
            AssetUtility.RequireClean();
            message = "保存完成。请重新生成合并预览或扫描场景引用，再执行操作。";
        }

        private static bool DrawObjects(string label, List<Object> objects)
        {
            bool changed = false;
            ResourceTheme.LabelField(label, ResourceTheme.Section);
            for (int i = 0; i < objects.Count; i++)
                using (new EditorGUILayout.VerticalScope(ResourceTheme.Row))
                {
                    Object value = ResourceTheme.ObjectField(null, objects[i], typeof(Object), false);
                    if (value != objects[i]) { objects[i] = value; changed = true; }
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(objects[i] == null ? "尚未选择" : AssetDatabase.GetAssetPath(objects[i]), ResourceTheme.MiniWrap);
                        if (GUILayout.Button("移除", GUILayout.Width(50))) { objects.RemoveAt(i--); changed = true; }
                    }
                }
            Rect drop = GUILayoutUtility.GetRect(10, 32, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "拖入资源 / 文件夹（支持多选）");
            Event evt = Event.current;
            if (GUI.enabled && drop.Contains(evt.mousePosition) && (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform))
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (Object asset in DragAndDrop.objectReferences)
                        if (EditorUtility.IsPersistent(asset) && !objects.Contains(asset)) { objects.Add(asset); changed = true; }
                }
                evt.Use();
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("添加当前选择"))
                    foreach (Object asset in Selection.objects)
                        if (EditorUtility.IsPersistent(asset) && !objects.Contains(asset)) { objects.Add(asset); changed = true; }
                if (GUILayout.Button("清空", GUILayout.Width(56))) { objects.Clear(); changed = true; }
            }
            if (objects.RemoveAll(o => o == null) > 0) changed = true;
            return changed;
        }

        private static void DrawPages(int count, ref int index)
        {
            int pages = Math.Max(1, (count + PageSize - 1) / PageSize);
            index = Math.Max(0, Math.Min(index, pages - 1));
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(index == 0)) if (GUILayout.Button("上一页", GUILayout.Width(65))) index--;
                ResourceTheme.LabelField("第 " + (index + 1) + "/" + pages + " 页 · " + count + " 项");
                using (new EditorGUI.DisabledScope(index == pages - 1)) if (GUILayout.Button("下一页", GUILayout.Width(65))) index++;
            }
        }

        private void ExportQuery()
        {
            string path = EditorUtility.SaveFilePanel("导出引用查询", "", "场景引用查询.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;
            var csv = new StringBuilder("路径,GUID,类型,查询范围,结果有效,引用场景数,引用场景\r\n");
            bool current = query.IsCurrent;
            foreach (ReferenceRow row in query.Rows)
                csv.AppendLine(string.Join(",", new[] { row.Path, row.Guid, row.Type, query.Label, current ? "是" : "否",
                    row.Scenes.Count.ToString(), string.Join(" | ", row.Scenes) }.Select(Csv)));
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        }

        private static string Csv(string value)
        {
            // Prevent exported cells from being interpreted as spreadsheet formulas.
            if (!string.IsNullOrEmpty(value) && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";
        }

        private static void ExportReport(OperationReport report)
        {
            string path = EditorUtility.SaveFilePanel("导出操作日志", "", "资源整理操作日志.json", "json");
            if (!string.IsNullOrEmpty(path)) File.WriteAllText(path, JsonUtility.ToJson(report, true), new UTF8Encoding(false));
        }

        private static string ActionName(MergeAction action)
        {
            switch (action)
            {
                case MergeAction.CreateFolder: return "创建目录";
                case MergeAction.MergeFolder: return "合并目录";
                case MergeAction.Move: return "移动";
                case MergeAction.Skip: return "跳过";
                default: return "阻断";
            }
        }
    }
}
