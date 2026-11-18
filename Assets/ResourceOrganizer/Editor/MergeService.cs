using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;

namespace Suyao.ResourceOrganizer
{
    internal enum ConflictPolicy { Skip, Rename, Abort }
    internal enum MergeAction { CreateFolder, MergeFolder, Move, Skip, Blocked }

    internal sealed class MergeItem
    {
        public MergeAction Action;
        public bool IsConflict;
        public bool Selected = true;
        public string Source, Target, Guid, Reason;
        public string Category { get { return Action == MergeAction.CreateFolder || Action == MergeAction.MergeFolder || Directory.Exists(Source) ? "Folders" : AssetCategory.Folder(Source); } }
    }

    internal sealed class MergePlan
    {
        public readonly List<MergeItem> Items = new List<MergeItem>();
        public string Source, Target, Snapshot;
        public string[] DependencyPaths = new string[0];
        public int Revision;
        public bool IsCurrent { get { return Revision == ProjectState.Revision; } }
        public bool HasErrors { get { return Items.Any(i => i.Action == MergeAction.Blocked); } }
        public int SelectedMoves { get { return Items.Count(i => i.Action == MergeAction.Move && i.Selected); } }
        public bool HasWork { get { return SelectedMoves > 0; } }
        public string UnavailableReason
        {
            get
            {
                if (!IsCurrent) return "预览已过期，请重新生成预览。";
                if (HasErrors) return "有阻断项，请切换“冲突优先”或“仅阻断”查看原因。";
                if (!HasWork) return "没有勾选可移动项；同名跳过不会移动文件，可调整冲突策略后重新预览。";
                return "已勾选 " + SelectedMoves + " 项，点击执行后按预览移动。";
            }
        }
    }

    internal static class MergeService
    {
        public static MergePlan Plan(string source, string target, ConflictPolicy policy, bool byType = false,
            IEnumerable<DependencyCandidate> dependencies = null)
        {
            source = ResourcePath.Normalize(source);
            target = ResourcePath.Normalize(target);
            if (!ResourcePath.IsAsset(source) || !ResourcePath.IsAsset(target))
                throw new InvalidOperationException("源与目标必须位于 Assets/ 下，且不能是 Assets 根目录或含无效路径。");
            if (ResourcePath.Related(source, target)) throw new InvalidOperationException("源与目标不能相同，也不能互为父子目录。");
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(source))) throw new InvalidOperationException("源资源未导入或不存在。");
            if (File.Exists(target)) throw new InvalidOperationException("目标必须是文件夹路径。");
            var plan = new MergePlan { Source = source, Target = target, Revision = ProjectState.Revision };
            var localPaths = TargetPaths(target);
            var occupied = new HashSet<string>(localPaths, StringComparer.OrdinalIgnoreCase);
            var exact = new HashSet<string>(localPaths, StringComparer.Ordinal);
            EnsureParents(target, plan, occupied, exact);
            if (dependencies != null)
            {
                var candidates = dependencies.ToArray();
                plan.DependencyPaths = candidates.Select(c => c.Path).ToArray();
                foreach (var candidate in candidates.OrderBy(c => c.Path, StringComparer.Ordinal))
                {
                    string destination = target + "/" + AssetCategory.Folder(candidate.Path);
                    EnsureParents(destination, plan, occupied, exact);
                    AddFile(candidate.Path, destination + "/" + Path.GetFileName(candidate.Path), policy, plan, occupied, exact);
                    var item = plan.Items[plan.Items.Count - 1];
                    if (candidate.Owners > 1)
                    {
                        item.Selected = false;
                        item.Reason += "；被本次选定的 " + candidate.Owners + " 个Prefab共享，默认不勾选（非全工程统计）";
                    }
                }
            }
            else if (byType)
            {
                IEnumerable<string> files = Directory.Exists(source)
                    ? Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(ResourcePath.Normalize)
                    : new[] { source };
                foreach (string file in files.Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p, StringComparer.Ordinal))
                {
                    string destination = target + "/" + AssetCategory.Folder(file);
                    EnsureParents(destination, plan, occupied, exact);
                    AddFile(file, destination + "/" + Path.GetFileName(file), policy, plan, occupied, exact);
                }
            }
            else if (AssetDatabase.IsValidFolder(source)) MergeDirectory(source, target, policy, plan, occupied, exact);
            else AddFile(source, target + "/" + Path.GetFileName(source), policy, plan, occupied, exact);
            plan.Snapshot = Snapshot(plan);
            return plan;
        }

        // Only enumerate the destination subtree and immediate entries on its parent
        // chain (for casing/type collisions). Never build a project dependency index.
        private static HashSet<string> TargetPaths(string target)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal) { "Assets" };
            if (Directory.Exists(target))
                foreach (string entry in Directory.GetFileSystemEntries(target, "*", SearchOption.AllDirectories))
                    paths.Add(ResourcePath.Normalize(entry));
            string parent = ResourcePath.Parent(target);
            while (!string.IsNullOrEmpty(parent) && ResourcePath.IsWithin(parent, "Assets"))
            {
                if (Directory.Exists(parent))
                    foreach (string entry in Directory.GetFileSystemEntries(parent))
                        paths.Add(ResourcePath.Normalize(entry));
                parent = ResourcePath.Parent(parent);
            }
            return paths;
        }

        private static void EnsureParents(string folder, MergePlan plan, HashSet<string> occupied, HashSet<string> exact)
        {
            if (folder == "Assets") return;
            if (occupied.Contains(folder))
            {
                    if (!exact.Contains(folder) || (!AssetDatabase.IsValidFolder(folder) &&
                        !plan.Items.Any(i => i.Action == MergeAction.CreateFolder && i.Target == folder)))
                    Add(plan, MergeAction.Blocked, "", folder, "目标目录存在大小写或文件类型冲突", true);
                return;
            }
            EnsureParents(ResourcePath.Parent(folder), plan, occupied, exact);
            Add(plan, MergeAction.CreateFolder, "", folder, "创建缺失目录");
            occupied.Add(folder);
            exact.Add(folder);
        }

        private static void MergeDirectory(string source, string target, ConflictPolicy policy,
            MergePlan plan, HashSet<string> occupied, HashSet<string> exact)
        {
            string reason = MoveRisk(source, target);
            if (!string.IsNullOrEmpty(reason)) { Add(plan, MergeAction.Blocked, source, target, reason); return; }
            Add(plan, MergeAction.MergeFolder, source, target, "合并内容，保留源与目标目录及各自 GUID");
            foreach (string child in Directory.GetFileSystemEntries(source).OrderBy(p => p, StringComparer.Ordinal))
            {
                string path = ResourcePath.Normalize(child);
                if (path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                string destination = target + "/" + Path.GetFileName(path);
                if (Directory.Exists(path))
                {
                    if (!AssetDatabase.IsValidFolder(path)) { Add(plan, MergeAction.Blocked, path, destination, "目录未被 Unity 导入"); continue; }
                    if (occupied.Contains(destination))
                    {
                        if (!exact.Contains(destination) || !AssetDatabase.IsValidFolder(destination))
                            Add(plan, MergeAction.Blocked, path, destination, "目录大小写冲突或目标为文件", true);
                        else MergeDirectory(path, destination, policy, plan, occupied, exact);
                    }
                    else
                    {
                        string risk = MoveRisk(path, destination);
                        Add(plan, string.IsNullOrEmpty(risk) ? MergeAction.Move : MergeAction.Blocked,
                            path, destination, string.IsNullOrEmpty(risk) ? "整体移动目录，保留 GUID" : risk);
                        occupied.Add(destination);
                        exact.Add(destination);
                    }
                }
                else AddFile(path, destination, policy, plan, occupied, exact);
            }
        }

        private static void AddFile(string source, string target, ConflictPolicy policy,
            MergePlan plan, HashSet<string> occupied, HashSet<string> exact)
        {
            if (source == target) { Add(plan, MergeAction.Skip, source, target, "已在目标位置"); return; }
            string note = "保留 GUID 移动";
            bool isConflict = occupied.Contains(target);
            if (occupied.Contains(target))
            {
                if (!exact.Contains(target) || AssetDatabase.IsValidFolder(target))
                { Add(plan, MergeAction.Blocked, source, target, "大小写或文件/目录类型冲突", true); return; }
                if (policy == ConflictPolicy.Skip) { Add(plan, MergeAction.Skip, source, target, "目标有同名文件，源文件保留", true); return; }
                if (policy == ConflictPolicy.Abort) { Add(plan, MergeAction.Blocked, source, target, "同名文件冲突", true); return; }
                target = ResourcePath.Unique(target, occupied);
                note = "同名文件自动改名，保留 GUID";
            }
            string risk = MoveRisk(source, target);
            Add(plan, string.IsNullOrEmpty(risk) ? MergeAction.Move : MergeAction.Blocked, source, target,
                string.IsNullOrEmpty(risk) ? note : risk, isConflict);
            occupied.Add(target);
            exact.Add(target);
        }

        private static void Add(MergePlan plan, MergeAction action, string source, string target, string reason, bool isConflict = false)
        {
            plan.Items.Add(new MergeItem { Action = action, Source = source, Target = target,
                Guid = string.IsNullOrEmpty(source) ? "" : AssetDatabase.AssetPathToGUID(source), Reason = reason, IsConflict = isConflict });
        }

        private static string AssemblyScope(string path)
        {
            string folder = AssetDatabase.IsValidFolder(path) ? path : ResourcePath.Parent(path);
            while (!string.IsNullOrEmpty(folder) && ResourcePath.IsWithin(folder, "Assets"))
            {
                if (Directory.Exists(folder))
                {
                    string[] definitions = Directory.GetFiles(folder, "*.asmdef").Concat(Directory.GetFiles(folder, "*.asmref")).OrderBy(p => p, StringComparer.Ordinal).ToArray();
                    if (definitions.Length > 0) return string.Join("|", definitions.Select(ResourcePath.Normalize));
                }
                folder = ResourcePath.Parent(folder);
            }
            return "默认程序集";
        }

        private static string MoveRisk(string source, string target)
        {
            if (!string.IsNullOrEmpty(ResourcePath.ProtectedLocation(source)) || !string.IsNullOrEmpty(ResourcePath.ProtectedLocation(target)))
                return "涉及特殊目录或工具自身目录，默认禁止移动";
            if (AssemblyScope(source) != AssemblyScope(target)) return "跨程序集定义边界，默认禁止移动";
            string[] files = Directory.Exists(source)
                ? Directory.GetFiles(source, "*", SearchOption.AllDirectories).Where(p => !p.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)).Select(ResourcePath.Normalize).ToArray()
                : new[] { source };
            foreach (string path in files.Concat(new[] { source }))
            {
                if (!string.IsNullOrEmpty(ResourcePath.ProtectedLocation(path))) return "子资源涉及特殊目录：" + path;
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (new[] { ".asmdef", ".asmref", ".dll", ".so", ".dylib", ".aar", ".jar", ".bundle", ".framework", ".androidlib" }.Contains(ext))
                    return "包含程序集或平台插件：" + path;
                if (!AssetUtility.IsEditable(path)) return "资源或 .meta 只读/未签出：" + path;
                if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path))) return "资源未导入：" + path;
            }
            string parent = ResourcePath.Parent(target);
            while (!Directory.Exists(parent) && !string.IsNullOrEmpty(parent)) parent = ResourcePath.Parent(parent);
            if (string.IsNullOrEmpty(parent) || !AssetUtility.IsEditable(parent)) return "目标父目录不可写";
            return "";
        }

        private static string Snapshot(MergePlan plan)
        {
            var builder = new StringBuilder();
            foreach (string root in new[] { plan.Source, plan.Target }.Concat(plan.DependencyPaths).Distinct(StringComparer.Ordinal))
            {
                var paths = new List<string> { root, root + ".meta" };
                if (Directory.Exists(root)) paths.AddRange(Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories));
                foreach (string path in paths.OrderBy(p => p, StringComparer.Ordinal))
                {
                    builder.Append(path).Append(':');
                    if (File.Exists(path))
                    {
                        var info = new FileInfo(path);
                        builder.Append(info.Length).Append(':').Append(info.LastWriteTimeUtc.Ticks);
                    }
                    else builder.Append(Directory.Exists(path) ? "folder" : "missing");
                    builder.Append('\n');
                }
            }
            return builder.ToString();
        }

        public static OperationReport Execute(MergePlan plan)
        {
            AssetUtility.RequireClean(true);
            if (plan == null) throw new InvalidOperationException("请先生成合并预览。");
            if (plan.HasErrors || !plan.HasWork || !plan.IsCurrent || plan.Snapshot != Snapshot(plan))
                throw new InvalidOperationException("合并计划已过期或存在阻断项，请重新预览。");
            var report = new OperationReport { operation = "移动合并" };
            // Selection only affects moves. Include their planned parent directories
            // automatically, preserving the original topological execution order.
            var selected = plan.Items.Where(i => i.Action == MergeAction.Move && i.Selected).ToArray();
            var requiredFolders = new HashSet<string>(plan.Items.Where(i => i.Action == MergeAction.CreateFolder &&
                selected.Any(move => ResourcePath.IsWithin(move.Target, i.Target))).Select(i => i.Target), StringComparer.Ordinal);
            foreach (var item in plan.Items)
                report.entries.Add(new OperationEntry { action = item.Action.ToString(), source = item.Source,
                    destination = item.Target, guid = item.Guid, reason = item.Reason,
                    status = item.Action == MergeAction.Skip || item.Action == MergeAction.MergeFolder ? "无需执行" :
                        item.Action == MergeAction.Move && !item.Selected ? "未勾选" :
                        item.Action == MergeAction.CreateFolder && !requiredFolders.Contains(item.Target) ? "无需执行" : "未执行" });
            report.summary = "正在执行";
            report.Save(); // Fail before modifying any assets if the operation journal is unavailable.
            try
            {
                for (int i = 0; i < plan.Items.Count; i++)
                {
                    var item = plan.Items[i];
                    var log = report.entries[i];
                    if (log.status != "未执行") continue;
                    if (EditorUtility.DisplayCancelableProgressBar("移动合并", item.Target, (float)i / plan.Items.Count))
                    { report.summary = "已取消；已完成操作保留"; break; }
                    try
                    {
                        log.status = "执行中";
                        report.Save();
                        if (item.Action == MergeAction.CreateFolder)
                        {
                            if (Directory.Exists(item.Target) || File.Exists(item.Target)) throw new IOException("目标路径已被占用");
                            string guid = AssetDatabase.CreateFolder(ResourcePath.Parent(item.Target), Path.GetFileName(item.Target));
                            if (string.IsNullOrEmpty(guid) || AssetDatabase.GUIDToAssetPath(guid) != item.Target)
                                throw new IOException("创建目录失败或 Unity 自动更名");
                            log.guid = guid;
                        }
                        else
                        {
                            if (AssetDatabase.AssetPathToGUID(item.Source) != item.Guid) throw new IOException("源 GUID 发生变化");
                            if (File.Exists(item.Target) || Directory.Exists(item.Target)) throw new IOException("目标路径已被占用");
                            // Validate now: planned parents did not necessarily exist at preview time.
                            string error = AssetDatabase.ValidateMoveAsset(item.Source, item.Target);
                            if (!string.IsNullOrEmpty(error)) throw new IOException(error);
                            string result = AssetDatabase.MoveAsset(item.Source, item.Target);
                            if (!string.IsNullOrEmpty(result)) throw new IOException(result);
                            if (AssetDatabase.AssetPathToGUID(item.Target) != item.Guid) throw new IOException("移动后 GUID 校验失败，请检查日志");
                        }
                        log.status = "成功";
                        report.Save();
                    }
                    catch (Exception exception)
                    {
                        log.status = "失败";
                        log.reason = exception.Message;
                        report.summary = "失败后已停止：" + exception.Message;
                        break;
                    }
                }
                if (report.summary == "正在执行")
                {
                    report.summary = "合并完成";
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                ProjectState.Changed();
                report.summary += "；移动成功 " + report.entries.Count(e => e.action == MergeAction.Move.ToString() && e.status == "成功") +
                    " 项，创建目录 " + report.entries.Count(e => e.action == MergeAction.CreateFolder.ToString() && e.status == "成功") +
                    " 项，跳过 " + report.entries.Count(e => e.action == MergeAction.Skip.ToString()) +
                    " 项，未勾选 " + report.entries.Count(e => e.status == "未勾选") +
                    " 项，失败 " + report.entries.Count(e => e.status == "失败") +
                    " 项，未执行 " + report.entries.Count(e => e.status == "未执行") + " 项";
                report.Save();
            }
            return report;
        }
    }
}
