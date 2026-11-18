using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using Object = UnityEngine.Object;

namespace Suyao.ResourceOrganizer
{
    [Serializable]
    internal sealed class RenameOptions
    {
        public string BaseName = "", Prefix = "", Suffix = "", Find = "", Replace = "";
        public int Numbering, Start = 1, Digits = 3;
    }

    internal sealed class RenameItem
    {
        public string Source, Target, Guid, Error;
        public string DraftName, Suffix = "";
        public string CompletedPath;
        public bool Selected;
        public string Relation = "", TypeName = "";
        public UnityEngine.Texture Icon;
    }

    internal sealed class RenameEmbedded
    {
        public string OwnerGuid, Relation;
        public Object Asset;
    }

    internal sealed class RenamePlan
    {
        public readonly List<RenameItem> Items = new List<RenameItem>();
        public readonly List<RenameEmbedded> Embedded = new List<RenameEmbedded>();
        public int Revision;
        public bool IsCurrent { get { return Revision == ProjectState.Revision; } }
    }

    internal static class RenameService
    {
        public static string Status(RenameItem row, bool current)
        {
            if (!current) return "已过期";
            if (!string.IsNullOrEmpty(row.CompletedPath) && row.CompletedPath == row.Target && row.Target == row.Source) return "已完成";
            if (string.IsNullOrWhiteSpace(row.DraftName)) return "待填写";
            if (string.IsNullOrEmpty(row.Error)) return "可执行";
            if (row.Error == "名称未变化") return "未变化";
            if (row.Error.Contains("存在") || row.Error.Contains("相同")) return "重名";
            if (row.Error.Contains("保护")) return "受保护";
            if (row.Error.Contains("只读")) return "只读";
            return "需调整";
        }

        public static string Category(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".unity") return "场景";
            if (ext == ".prefab") return "预制体";
            if (ext == ".mat") return "材质";
            if (new[] { ".fbx", ".obj", ".blend", ".dae", ".3ds", ".dxf" }.Contains(ext)) return "模型";
            if (new[] { ".png", ".jpg", ".jpeg", ".tga", ".psd", ".tif", ".tiff", ".exr", ".hdr", ".bmp", ".dds" }.Contains(ext)) return "贴图";
            return "其他";
        }

        public static IEnumerable<RenameItem> Filter(RenamePlan plan, string relation, string type, string search)
        {
            return plan.Items.Where(row => (relation == "全部关系" || (row.Relation ?? "").Contains(relation)) &&
                (type == "全部类型" || Category(row.Source) == type) &&
                (string.IsNullOrWhiteSpace(search) || row.Source.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 ||
                 ((row.DraftName ?? "") + (row.Suffix ?? "")).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0));
        }

        public static IEnumerator FindRelated(Object seed, bool dependencies, RenamePlan result, EditorJob job)
        {
            return FindRelated(seed, dependencies ? 1 : 2, result, job);
        }

        // 0 = both, 1 = dependencies, 2 = referring assets.
        public static IEnumerator FindRelated(Object seed, int mode, RenamePlan result, EditorJob job)
        {
            AssetUtility.RequireEditMode();
            string path = AssetDatabase.GetAssetPath(seed);
            if (string.IsNullOrEmpty(path) || AssetDatabase.IsValidFolder(path))
                throw new InvalidOperationException("请拖入一个资源文件，不能是场景对象或文件夹。");
            result.Revision = ProjectState.Revision;
            var paths = new HashSet<string>(StringComparer.Ordinal) { path };
            var dependencies = new HashSet<string>(StringComparer.Ordinal);
            var references = new HashSet<string>(StringComparer.Ordinal);
            if (mode != 2)
            {
                foreach (string dependency in AssetDatabase.GetDependencies(path, true))
                    if (dependency != path) { paths.Add(dependency); dependencies.Add(dependency); }
                yield return null;
            }
            if (mode != 1)
            {
                string[] all = AssetUtility.AllPaths();
                for (int i = 0; i < all.Length; i++)
                {
                    job.Message = "查找引用 " + (i + 1) + "/" + all.Length + "：" + all[i];
                    job.Progress = (float)i / all.Length;
                    if (all[i] != path && AssetDatabase.GetDependencies(all[i], true).Contains(path))
                    { paths.Add(all[i]); references.Add(all[i]); }
                    yield return null;
                }
            }
            if (!result.IsCurrent) throw new InvalidOperationException("查询期间工程发生变化，请重新查找。");
            foreach (string source in paths.Where(ResourcePath.IsAsset).OrderBy(p => p, StringComparer.Ordinal))
            {
                string relation = source == path ? "本体" : dependencies.Contains(source) && references.Contains(source) ? "依赖 / 引用" : dependencies.Contains(source) ? "依赖" : "引用";
                string guid = AssetDatabase.AssetPathToGUID(source);
                Type type = AssetDatabase.GetMainAssetTypeAtPath(source);
                string mainName, suffix;
                ReadName(Path.GetFileNameWithoutExtension(source), out mainName, out suffix);
                result.Items.Add(new RenameItem { Source = source, Target = source,
                    Guid = guid, DraftName = mainName, Suffix = suffix, Selected = false,
                    Relation = relation, TypeName = type == null ? Path.GetExtension(source) : type.Name,
                    Icon = AssetDatabase.GetCachedIcon(source) });
                foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(source))
                    if (asset != null && AssetDatabase.IsSubAsset(asset) && !(asset is UnityEngine.Component) && !(asset is UnityEngine.GameObject))
                        result.Embedded.Add(new RenameEmbedded { OwnerGuid = guid, Relation = relation, Asset = asset });
                job.Message = "整理资源类型：" + source;
                yield return null;
            }
            if (!result.IsCurrent) throw new InvalidOperationException("查询期间工程发生变化，请重新查找。");
            Validate(result);
        }

        public static void ReadName(string name, out string mainName, out string suffix)
        {
            int split = name.LastIndexOf('_');
            mainName = split > 0 ? name.Substring(0, split) : name;
            suffix = split > 0 ? name.Substring(split) : "";
        }

        public static void ApplyMainName(RenamePlan plan, string name)
        {
            foreach (var item in plan.Items) item.DraftName = name;
            Validate(plan);
            foreach (var item in plan.Items) item.Selected = string.IsNullOrEmpty(item.Error);
        }

        public static RenamePlan SingleRow(RenamePlan plan, RenameItem row)
        {
            if (plan == null || !plan.IsCurrent || !plan.Items.Contains(row))
                throw new InvalidOperationException("命名列表已过期或文件不属于当前列表。");
            var single = new RenamePlan { Revision = plan.Revision };
            single.Items.Add(new RenameItem { Source = row.Source, Target = row.Target, Guid = row.Guid,
                DraftName = row.DraftName, Suffix = row.Suffix, Selected = true, Error = row.Error });
            Validate(single);
            return single;
        }

        public static void ValidatePreview(RenamePlan plan)
        {
            var selected = plan.Items.Where(i => i.Selected).ToArray();
            Validate(plan);
            foreach (var row in selected) row.Selected = true;
        }

        public static void SyncRowPreview(RenamePlan plan, RenameItem row, string mainName)
        {
            if (plan == null || !plan.IsCurrent || !plan.Items.Contains(row))
                throw new InvalidOperationException("命名列表已过期，请重新查找。");
            row.DraftName = mainName;
            ValidatePreview(plan);
        }

        public static RenamePlan SelectedPreview(RenamePlan plan)
        {
            if (plan == null || !plan.IsCurrent) throw new InvalidOperationException("命名列表已过期，请重新查找。");
            ValidatePreview(plan);
            var result = new RenamePlan { Revision = plan.Revision };
            foreach (var row in plan.Items.Where(i => i.Selected))
            {
                if (row.Error == "名称未变化") continue;
                if (!string.IsNullOrEmpty(row.Error))
                    throw new InvalidOperationException(row.Source + "：" + row.Error + "。请修改预览或取消勾选。");
                result.Items.Add(new RenameItem { Source = row.Source, Target = row.Target, Guid = row.Guid,
                    DraftName = row.DraftName, Suffix = row.Suffix, Selected = true, Error = "" });
            }
            return result;
        }

        public static void Validate(RenamePlan plan)
        {
            foreach (var item in plan.Items)
            {
                string name = (item.DraftName ?? "") + (item.Suffix ?? "");
                item.Target = ResourcePath.Parent(item.Source) + "/" + name + Path.GetExtension(item.Source);
                item.Error = "";
                if (string.IsNullOrWhiteSpace(item.DraftName)) item.Error = "新主名为空，请点击本行同步或手动填写";
                else if (!AssetNaming.ValidName(name)) item.Error = "名称为空、含非法字符或为系统保留名";
                else if (!ResourcePath.IsAsset(item.Source) || !string.IsNullOrEmpty(ResourcePath.ProtectedLocation(item.Source))) item.Error = "特殊目录或包资源受保护";
                else if (new[] { ".cs", ".asmdef", ".asmref", ".dll" }.Contains(Path.GetExtension(item.Source).ToLowerInvariant())) item.Error = "脚本与程序集名称受保护";
                else if (item.Source == item.Target) item.Error = "名称未变化";
                else if (!AssetUtility.IsEditable(item.Source)) item.Error = "文件或meta只读/未签出";
                else if (item.Source.Equals(item.Target, StringComparison.OrdinalIgnoreCase)) item.Error = "仅大小写变更，请先使用临时名称";
                else if (Occupied(item.Target)) item.Error = "目标名称已存在，不覆盖或交换名称";
            }
            foreach (var group in plan.Items.GroupBy(i => i.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var item in group) item.Error = "本批次多个文件使用相同目标名称";
            foreach (var item in plan.Items.Where(i => !string.IsNullOrEmpty(i.Error))) item.Selected = false;
        }

        public static void RefreshAfterExecute(RenamePlan plan, OperationReport report)
        {
            foreach (var item in plan.Items)
            {
                var entry = report.entries.FirstOrDefault(e => e.guid == item.Guid && e.status == "成功");
                if (entry == null) continue;
                item.Source = entry.destination;
                item.CompletedPath = entry.destination;
                ReadName(Path.GetFileNameWithoutExtension(item.Source), out item.DraftName, out item.Suffix);
                item.Selected = false;
            }
            // Rebase only this tool's own renames, never unrelated external changes.
            plan.Revision = ProjectState.Revision;
            Validate(plan);
        }

        public static RenamePlan Preview(IEnumerable<Object> targets, RenameOptions options)
        {
            AssetUtility.RequireEditMode();
            if (options.Start < 1 || options.Digits < 1 || options.Digits > 9)
                throw new InvalidOperationException("起始编号需大于0，数字位数需为1～9。");
            var plan = new RenamePlan { Revision = ProjectState.Revision };
            int index = 0;
            foreach (string source in AssetUtility.TargetPaths(targets).OrderBy(p => p, StringComparer.Ordinal))
            {
                string name = AssetNaming.Compose(Path.GetFileNameWithoutExtension(source), options.BaseName, options.Prefix,
                    options.Suffix, options.Find, options.Replace, checked(options.Start + index++), options.Numbering, options.Digits);
                var item = new RenameItem { Source = source, Target = ResourcePath.Parent(source) + "/" + name + Path.GetExtension(source),
                    Guid = AssetDatabase.AssetPathToGUID(source), DraftName = name, Error = "" };
                if (!AssetNaming.ValidName(name)) item.Error = "名称为空、含非法字符或为系统保留名";
                else if (!string.IsNullOrEmpty(ResourcePath.ProtectedLocation(source))) item.Error = "特殊目录受保护";
                else if (new[] { ".cs", ".asmdef", ".asmref", ".dll" }.Contains(Path.GetExtension(source).ToLowerInvariant())) item.Error = "脚本与程序集名称受保护";
                else if (item.Source == item.Target) item.Error = "名称未变化";
                else if (!AssetUtility.IsEditable(source)) item.Error = "文件或meta只读/未签出";
                else if (source.Equals(item.Target, StringComparison.OrdinalIgnoreCase)) item.Error = "仅大小写变更，请先使用临时名称";
                else if (Occupied(item.Target)) item.Error = "目标名称已存在，不覆盖或交换名称";
                item.Selected = string.IsNullOrEmpty(item.Error);
                plan.Items.Add(item);
            }
            foreach (var group in plan.Items.GroupBy(i => i.Target, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                foreach (var item in group) { item.Error = "本批次多个文件使用相同目标名称"; item.Selected = false; }
            if (plan.Items.Count == 0) throw new InvalidOperationException("没有可命名的文件。");
            return plan;
        }

        private static bool Occupied(string path)
        {
            // Enumerate only this parent, also detecting case collisions on Unix.
            string parent = ResourcePath.Parent(path);
            return Directory.Exists(parent) && Directory.GetFileSystemEntries(parent).Any(p =>
                ResourcePath.Normalize(p).Equals(path, StringComparison.OrdinalIgnoreCase) ||
                ResourcePath.Normalize(p).Equals(path + ".meta", StringComparison.OrdinalIgnoreCase));
        }

        public static OperationReport Execute(RenamePlan plan)
        {
            AssetUtility.RequireClean(true);
            if (plan == null || !plan.IsCurrent) throw new InvalidOperationException("命名预览已过期，请重新生成。");
            // Validate editable drafts again at the execution boundary.
            var intended = plan.Items.Where(i => i.Selected).ToArray();
            Validate(plan);
            if (intended.Any(i => !string.IsNullOrEmpty(i.Error))) throw new InvalidOperationException("勾选项存在命名冲突，请检查列表。");
            var selected = plan.Items.Where(i => i.Selected).ToArray();
            if (selected.Length == 0) throw new InvalidOperationException("没有勾选有效命名项。");
            foreach (var item in selected)
                if (!string.IsNullOrEmpty(item.Error) || AssetDatabase.AssetPathToGUID(item.Source) != item.Guid ||
                    !AssetUtility.IsEditable(item.Source) || Occupied(item.Target))
                    throw new InvalidOperationException("命名条件已变化：" + item.Source);
            var report = new OperationReport { operation = "批量命名", summary = "正在命名" };
            foreach (var item in plan.Items)
                report.entries.Add(new OperationEntry { action = "重命名", source = item.Source, destination = item.Target,
                    guid = item.Guid, status = item.Selected ? "未执行" : "未勾选", reason = item.Error });
            report.Save();
            try
            {
                for (int i = 0; i < plan.Items.Count; i++)
                {
                    var item = plan.Items[i];
                    var log = report.entries[i];
                    if (!item.Selected) continue;
                    if (EditorUtility.DisplayCancelableProgressBar("批量命名", item.Source, (float)i / plan.Items.Count))
                    { report.summary = "已取消，已完成的改名保留"; break; }
                    try
                    {
                        if (Occupied(item.Target)) throw new IOException("目标名称已被占用");
                        log.status = "执行中"; report.Save();
                        string error = AssetDatabase.RenameAsset(item.Source, Path.GetFileNameWithoutExtension(item.Target));
                        if (!string.IsNullOrEmpty(error)) throw new IOException(error);
                        if (AssetDatabase.AssetPathToGUID(item.Target) != item.Guid) throw new IOException("改名后GUID校验失败");
                        log.status = "成功"; report.Save();
                    }
                    catch (Exception exception) { log.status = "失败"; log.reason = exception.Message; report.summary = "失败后已停止"; break; }
                }
                if (report.summary == "正在命名") report.summary = "命名完成";
            }
            finally
            {
                EditorUtility.ClearProgressBar(); ProjectState.Changed();
                report.summary += "；成功 " + report.entries.Count(e => e.status == "成功") + "，失败 " + report.entries.Count(e => e.status == "失败") +
                    "，未执行 " + report.entries.Count(e => e.status == "未执行") + "，未勾选 " + report.entries.Count(e => e.status == "未勾选");
                report.Save();
            }
            return report;
        }
    }
}
