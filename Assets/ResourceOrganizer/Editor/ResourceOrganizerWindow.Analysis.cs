using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Suyao.ResourceOrganizer
{
    public sealed partial class ResourceOrganizerWindow
    {
        [SerializeField] private int analysisMode;
        [SerializeField] private SceneAsset analysisScene;
        [SerializeField] private List<Object> analysisTargets = new List<Object>();
        private ResourceAnalysis analysis;
        private int analysisSort, analysisType, analysisPage;
        private string analysisSearch = "";
        private static readonly string[] AnalysisTypes = { "全部", "贴图", "材质", "网格", "预制体", "模型", "其他", "缺失引用" };

        private void DrawAnalysis()
        {
            using (new EditorGUILayout.VerticalScope(ResourceTheme.Panel))
            {
                GUILayout.Label("资源分析", ResourceTheme.Section);
                GUILayout.Label("只读快照：已加载对象内存估算不等于显存、磁盘大小或构建体积。缺失引用检测不把未赋值槽当作错误。", ResourceTheme.MiniWrap);
                EditorGUI.BeginChangeCheck();
                analysisMode = ResourceTheme.Popup("分析范围", analysisMode, new[] { "当前活动场景（含未保存）", "指定场景（磁盘版本）", "所选文件 / 文件夹" });
                if (analysisMode == 1) analysisScene = (SceneAsset)ResourceTheme.AssetSlot("需要分析的场景", analysisScene, typeof(SceneAsset));
                if (analysisMode == 2 && DrawObjects("分析资源 / 文件夹", analysisTargets)) analysis = null;
                if (EditorGUI.EndChangeCheck()) analysis = null;
                if (GUILayout.Button("开始分析", ResourceTheme.Primary)) RunSafe(() =>
                {
                    analysis = null;
                    var next = new ResourceAnalysis();
                    StartJob(next.Scan(analysisMode, analysisTargets.Where(o => o != null).ToArray(), analysisScene, job), () =>
                    {
                        analysis = next; analysisPage = 0;
                        message = next.Complete ? "资源分析完成。" : "部分分析失败：\n" + string.Join("\n", next.Errors.Take(10));
                    });
                });
            }
            if (analysis == null) return;
            GUILayout.Space(12);
            using (new EditorGUILayout.VerticalScope(ResourceTheme.Panel))
            {
                GUILayout.Label(analysis.Scope + "\n快照时间 " + analysis.Time, ResourceTheme.MiniWrap);
                GUILayout.Label("对象 " + analysis.Rows.Count + "；已加载内存估算 " + EditorUtility.FormatBytes(analysis.Rows.Sum(r => r.Memory)) +
                    "；检测到缺失引用 " + analysis.Rows.Sum(r => r.Missing), ResourceTheme.Section);
                if (!analysis.Complete || analysis.Revision != ProjectState.Revision)
                    ResourceTheme.Help("结果不完整或工程已变化，请重新分析后再判断。", MessageType.Warning);
                EditorGUI.BeginChangeCheck();
                analysisSearch = ResourceTheme.TextField("搜索名称 / 路径", analysisSearch);
                using (new EditorGUILayout.HorizontalScope())
                {
                    analysisType = ResourceTheme.Popup("类型", analysisType, AnalysisTypes);
                    analysisSort = ResourceTheme.Popup("排序", analysisSort, new[] { "内存从大到小", "按名称", "缺失引用优先" });
                }
                if (EditorGUI.EndChangeCheck()) analysisPage = 0;
                var rows = analysis.Rows.Where(r => (analysisType == 0 || r.Type == AnalysisTypes[analysisType]) &&
                    (string.IsNullOrEmpty(analysisSearch) || (r.Name + r.Path).IndexOf(analysisSearch, System.StringComparison.OrdinalIgnoreCase) >= 0));
                rows = analysisSort == 1 ? rows.OrderBy(r => r.Name) : analysisSort == 2 ? rows.OrderByDescending(r => r.Missing).ThenBy(r => r.Path) : rows.OrderByDescending(r => r.Memory).ThenBy(r => r.Path);
                var display = rows.ToArray();
                if (GUILayout.Button("导出完整分析 CSV")) RunSafe(ExportAnalysis);
                DrawPages(display.Length, ref analysisPage);
                ResourceTheme.TableHeader(new[] { "文件", "类型", "内存估算", "缺失", "定位" }, -1, 80, 110, 54, 48);
                foreach (var row in display.Skip(analysisPage * PageSize).Take(PageSize))
                {
                    Rect[] cells = ResourceTheme.TableRow(-1, 80, 110, 54, 48);
                    ResourceTheme.AssetCell(cells[0], row.Path, row.Name, row.Asset != null ? AssetPreview.GetMiniThumbnail(row.Asset) : null, tooltip: row.Path + "\n" + row.Details);
                    ResourceTheme.Cell(cells[1], row.Type);
                    ResourceTheme.Cell(cells[2], EditorUtility.FormatBytes(row.Memory));
                    ResourceTheme.Cell(cells[3], row.Missing.ToString(), row.Details);
                    if (GUI.Button(cells[4], "定位", ResourceTheme.Button)) EditorGUIUtility.PingObject(row.Asset != null ? row.Asset : AssetDatabase.LoadMainAssetAtPath(row.Path));
                }
            }
        }

        private void ExportAnalysis()
        {
            string path = EditorUtility.SaveFilePanel("导出资源分析", "", "资源分析.csv", "csv");
            if (string.IsNullOrEmpty(path)) return;
            var csv = new StringBuilder("名称,路径,类型,已加载内存估算字节,缺失引用数,详情,分析范围,快照时间,完整,工程版本仍匹配\r\n");
            foreach (var row in analysis.Rows)
                csv.AppendLine(string.Join(",", new[] { row.Name, row.Path, row.Type, row.Memory.ToString(), row.Missing.ToString(), row.Details,
                    analysis.Scope, analysis.Time, analysis.Complete ? "是" : "否", analysis.Revision == ProjectState.Revision ? "是" : "否" }.Select(Csv)));
            File.WriteAllText(path, csv.ToString(), new UTF8Encoding(true));
        }
    }
}
