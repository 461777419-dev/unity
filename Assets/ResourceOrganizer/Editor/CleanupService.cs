using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Suyao.ResourceOrganizer
{
    internal sealed class CleanupCandidates
    {
        public readonly List<ReferenceRow> Rows = new List<ReferenceRow>();
        public int Revision;
        public string Settings;
        public bool Complete;
        public string Origin = "手动选择的资源";
        public bool FromReferenceQuery;
        public bool IsCurrent { get { return Complete && Revision == ProjectState.Revision && Settings == ProjectState.SettingsStamp(); } }

        public static CleanupCandidates FromQuery(SceneQuery query)
        {
            if (query == null || !query.IsCurrent) throw new InvalidOperationException("请先完成有效的引用查询，过期或失败结果不能送入清理。");
            var result = new CleanupCandidates { Revision = query.Revision, Settings = query.Settings,
                Origin = "引用查询：" + query.Label, Complete = true, FromReferenceQuery = true };
            foreach (var row in query.Rows.Where(r => r.Scenes.Count == 0 && ResourcePath.IsAsset(r.Path)))
                result.Rows.Add(new ReferenceRow { Path = row.Path, Guid = row.Guid, Type = row.Type,
                    Selected = ResourcePath.CanCleanType(row.Path) });
            if (result.Rows.Count == 0) throw new InvalidOperationException("当前查询范围没有可送入清理的未引用Assets资源。");
            return result;
        }

        public IEnumerator Load(IEnumerable<UnityEngine.Object> targets, EditorJob job)
        {
            Revision = ProjectState.Revision;
            Settings = ProjectState.SettingsStamp();
            foreach (string path in AssetUtility.TargetPaths(targets))
            {
                job.Message = "加载待清理资源：" + path;
                Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
                Rows.Add(new ReferenceRow { Path = path, Guid = AssetDatabase.AssetPathToGUID(path),
                    Type = type == null ? "Unknown" : type.Name });
                yield return null;
            }
            if (Rows.Count == 0) throw new InvalidOperationException("所选范围中没有可加载的资源文件。");
            if (Revision != ProjectState.Revision || Settings != ProjectState.SettingsStamp())
                throw new InvalidOperationException("加载期间工程发生变化，请重新加载。");
            Complete = true;
        }
    }

    internal sealed class CleanupPreview
    {
        public ProjectIndex Index;
        public CleanupDecision Decision;
        public string SelectionKey;
        public string ReviewedAt;
        public long Bytes;
        private CleanupCandidates queryCandidates;
        public readonly Dictionary<string, string> Guids = new Dictionary<string, string>(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Stamps = new Dictionary<string, string>(StringComparer.Ordinal);

        public static string Key(IEnumerable<ReferenceRow> rows)
        {
            return string.Join("\n", rows.Where(r => r.Selected).Select(r => r.Guid).OrderBy(s => s, StringComparer.Ordinal));
        }

        private static string Stamp(string path)
        {
            var file = new FileInfo(path);
            var meta = new FileInfo(path + ".meta");
            return (file.Exists ? file.Length + ":" + file.LastWriteTimeUtc.Ticks : "missing") + "/" +
                (meta.Exists ? meta.Length + ":" + meta.LastWriteTimeUtc.Ticks : "missing");
        }

        // Use the completed scene query explicitly requested by the user; no project index build.
        public static CleanupPreview FromQueryResult(CleanupCandidates candidates)
        {
            if (candidates == null || !candidates.FromReferenceQuery || !candidates.IsCurrent)
                throw new InvalidOperationException("引用查询结果已过期，请重新查询并载入清理。");
            var result = new CleanupPreview { queryCandidates = candidates,
                Decision = new CleanupDecision(), SelectionKey = Key(candidates.Rows),
                ReviewedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };
            foreach (var row in candidates.Rows.Where(r => r.Selected))
            {
                if (!ResourcePath.IsAsset(row.Path) || AssetDatabase.IsValidFolder(row.Path) ||
                    !ResourcePath.CanCleanType(row.Path) || row.Scenes.Count != 0)
                    throw new InvalidOperationException("不是可清理的未引用资源：" + row.Path);
                if (AssetDatabase.AssetPathToGUID(row.Path) != row.Guid || !AssetUtility.IsEditable(row.Path))
                    throw new InvalidOperationException("资源已变化或只读，请重新查询：" + row.Path);
                result.Decision.Allowed.Add(row.Path);
                result.Guids[row.Path] = row.Guid;
                result.Stamps[row.Path] = Stamp(row.Path);
                result.Bytes += new FileInfo(row.Path).Length;
            }
            return result;
        }

        public IEnumerator Review(List<ReferenceRow> rows, IEnumerable<UnityEngine.Object> keep, TextAsset manifest, EditorJob job)
        {
            AssetUtility.RequireClean();
            SelectionKey = Key(rows);
            Index = new ProjectIndex();
            IEnumerator build = Index.Build(keep, manifest, job);
            while (build.MoveNext()) yield return build.Current;
            if (!Index.Complete) throw new InvalidOperationException("全工程复核不完整：\n" + string.Join("\n", Index.Errors.Take(12)));
            var candidates = rows.Where(r => r.Selected).ToArray();
            if (candidates.Length == 0) throw new InvalidOperationException("请先选择待复核资源。");
            foreach (ReferenceRow row in candidates)
            {
                string reason = "";
                if (!ResourcePath.IsAsset(row.Path) || AssetDatabase.IsValidFolder(row.Path) || !ResourcePath.CanCleanType(row.Path)) reason = "默认保护的文件类型或目录";
                else if (!Index.Dependencies.ContainsKey(row.Path)) reason = "资源不在当前完整索引中";
                else if (AssetDatabase.AssetPathToGUID(row.Path) != row.Guid) reason = "资源 GUID 已变化";
                else if (!AssetUtility.IsEditable(row.Path)) reason = "文件或 .meta 只读/未签出";
                if (!string.IsNullOrEmpty(reason)) Index.Protected[row.Path] = reason;
                yield return null;
            }
            Decision = CleanupGraph.Evaluate(candidates.Select(r => r.Path), Index.Dependencies, Index.Protected);
            Bytes = 0;
            foreach (ReferenceRow row in rows)
            {
                row.Cleanable = Decision.Allowed.Contains(row.Path);
                string reason;
                row.CleanupReason = row.Cleanable ? "已通过复核（仍需确认动态加载范围）" :
                    Decision.Blocked.TryGetValue(row.Path, out reason) ? reason : "本次未选择复核";
                if (row.Cleanable)
                {
                    Guids[row.Path] = row.Guid;
                    Stamps[row.Path] = Stamp(row.Path);
                    Bytes += new FileInfo(row.Path).Length;
                }
                yield return null;
            }
            AssetUtility.RequireClean();
            if (!Index.IsCurrent) throw new InvalidOperationException("工程发生变化，复核结果失效。");
            ReviewedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        public bool Matches(List<ReferenceRow> rows)
        {
            return Decision != null && (queryCandidates != null ? queryCandidates.IsCurrent : Index != null && Index.IsCurrent) && SelectionKey == Key(rows);
        }

        public OperationReport Trash(List<ReferenceRow> rows)
        {
            AssetUtility.RequireClean();
            if (!Matches(rows) || Decision.Allowed.Count == 0) throw new InvalidOperationException("清理预览为空或已过期，请重新复核。");
            foreach (string path in Decision.Allowed)
                if (AssetDatabase.AssetPathToGUID(path) != Guids[path] || Stamp(path) != Stamps[path] || !AssetUtility.IsEditable(path))
                    throw new InvalidOperationException("文件状态发生变化，请重新复核：" + path);
            var report = new OperationReport { operation = "移入回收站", summary = "正在执行" };
            foreach (string path in Decision.Allowed.OrderBy(p => p, StringComparer.Ordinal))
                report.entries.Add(new OperationEntry { action = "移入回收站", source = path,
                    guid = Guids[path], reason = (queryCandidates != null ? "按引用查询结果清理：" + queryCandidates.Origin : "全工程复核于 " + ReviewedAt), status = "未执行" });
            foreach (var blocked in Decision.Blocked)
                report.entries.Add(new OperationEntry { action = "保留", source = blocked.Key,
                    guid = AssetDatabase.AssetPathToGUID(blocked.Key), reason = blocked.Value, status = "已排除" });
            report.Save();
            int success = 0;
            try
            {
                // Keep the confirmed dependency group intact: no user cancellation between deletions.
                // API failures may still leave a partial group, which is explicitly logged.
                foreach (OperationEntry entry in report.entries)
                {
                    if (entry.action != "移入回收站") continue;
                    EditorUtility.DisplayProgressBar("移入回收站", entry.source, (float)success / Decision.Allowed.Count);
                    try
                    {
                        if (AssetDatabase.AssetPathToGUID(entry.source) != entry.guid || Stamp(entry.source) != Stamps[entry.source])
                            throw new IOException("执行期间文件状态变化");
                        entry.status = "执行中";
                        report.Save();
                        if (!AssetDatabase.MoveAssetToTrash(entry.source)) throw new IOException("回收站 API 返回失败，未改用永久删除");
                        entry.status = "成功";
                        success++;
                        report.Save();
                    }
                    catch (Exception exception)
                    {
                        entry.status = "失败";
                        entry.reason = exception.Message;
                        report.summary = "发生失败后停止；候选资源组可能只处理了一部分，请按日志检查或恢复";
                        break;
                    }
                }
                if (report.summary == "正在执行") report.summary = "清理完成";
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                ProjectState.Changed();
                report.summary += "；成功 " + success + "，失败 " + report.entries.Count(e => e.status == "失败") +
                    "，未执行 " + report.entries.Count(e => e.status == "未执行");
                report.Save();
            }
            return report;
        }
    }
}
