// Yorimashi blendshape/set_batch — M3-B3 真改动 tool
//
// 一次改多个 blendshape (可跨多个 SMR)。相比 blendshape/set 逐个调用:
//   - 减 wss 往返 (改 20 个 blendshape 从 20 次 tool call 缩到 1 次)
//   - 单个 Undo group (Ctrl+Z 一次撤销整批)
//   - 批量备份 (一份 JSON 快照包含所有涉及的 SMR)
//
// 参数：
//   changes  (array, required) — 每 item {path, blendshape, value}
//   dry_run  (bool, optional, default true)
//
// 返回：
//   changes: 完整 preview 数组
//   applied.items[]: 每个成功改动的 {path, blendshape, from, to, verified}
//   applied.summary: {total, verified, failed}
//
// **Scope Guard 特殊处理**: 批量 tool 需要 override AllowWriteOnTarget,
// 因为参数里没有单个 'path' 字段，而是 changes 数组里每 item 各自的 path。
// 每个 item 都必须过白名单，任何一个不过就整批拒。
//
// Sentinel: "M3-B3 SET BATCH"
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal class BlendshapeSetBatchTool : YorimashiWriteToolBase
    {
        public const string SentinelTag = "M3-B3 SET BATCH";

        public override string ToolName => "blendshape/set_batch";

        // 每 item 的 resolved 状态
        private class BatchItem
        {
            public string Path;
            public string BlendshapeName;
            public int Index;
            public float OldWeight;
            public float NewWeight;
            public SkinnedMeshRenderer Smr;
        }

        private List<BatchItem> _items;

        /// <summary>
        /// 批量 tool 的 scope guard: changes 数组里每个 item 的 path 都必须过白名单。
        /// </summary>
        protected override bool AllowWriteOnTarget(Dictionary<string, object> parsed, out string reason)
        {
            if (!parsed.TryGetValue("changes", out var cObj) || !(cObj is List<object> changesList))
            {
                reason = "missing required 'changes' array";
                return false;
            }
            string[] allowlist = { "Ramune_test/", "WriteTest/", "Yorimashi_WriteTest/" };
            for (int i = 0; i < changesList.Count; i++)
            {
                if (!(changesList[i] is Dictionary<string, object> item))
                {
                    reason = "changes[" + i + "] is not an object";
                    return false;
                }
                if (!item.TryGetValue("path", out var pObj) || !(pObj is string p) || string.IsNullOrEmpty(p))
                {
                    reason = "changes[" + i + "].path is missing";
                    return false;
                }
                bool matched = false;
                foreach (var pfx in allowlist)
                {
                    if (p.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) { matched = true; break; }
                }
                if (!matched)
                {
                    reason = "changes[" + i + "].path '" + p + "' not in write whitelist. "
                           + "All items must be under Ramune_test/, WriteTest/, or Yorimashi_WriteTest/. "
                           + "Batch is rejected as a whole.";
                    return false;
                }
            }
            reason = null;
            return true;
        }

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            if (!parsed.TryGetValue("changes", out var cObj) || !(cObj is List<object> changesList))
                throw new ArgumentException("missing required 'changes' (array)");
            if (changesList.Count == 0)
                throw new ArgumentException("'changes' array is empty");
            if (changesList.Count > 100)
                throw new ArgumentException("'changes' array too large (>100 items); split into smaller batches");

            _items = new List<BatchItem>();
            var previewItems = new List<Dictionary<string, object>>();

            for (int i = 0; i < changesList.Count; i++)
            {
                if (!(changesList[i] is Dictionary<string, object> raw))
                    throw new ArgumentException("changes[" + i + "] not an object");
                if (!raw.TryGetValue("path", out var pObj) || !(pObj is string path))
                    throw new ArgumentException("changes[" + i + "].path missing");
                if (!raw.TryGetValue("blendshape", out var bObj) || !(bObj is string bname))
                    throw new ArgumentException("changes[" + i + "].blendshape missing");
                if (!raw.TryGetValue("value", out var vObj))
                    throw new ArgumentException("changes[" + i + "].value missing");

                float newVal;
                if (vObj is double dd) newVal = (float)dd;
                else if (vObj is long ll) newVal = (float)ll;
                else if (vObj is int ii) newVal = (float)ii;
                else throw new ArgumentException("changes[" + i + "].value must be numeric");
                if (newVal < 0f) newVal = 0f;
                if (newVal > 100f) newVal = 100f;

                var go = ResolveGameObjectPath(path);
                if (go == null) throw new InvalidOperationException("changes[" + i + "] GO not found: " + path);
                var smr = go.GetComponent<SkinnedMeshRenderer>();
                if (smr == null || smr.sharedMesh == null)
                    throw new InvalidOperationException("changes[" + i + "] no SMR at: " + path);
                int idx = smr.sharedMesh.GetBlendShapeIndex(bname);
                if (idx < 0)
                    throw new InvalidOperationException("changes[" + i + "] blendshape '" + bname + "' not found at: " + path);

                var oldWeight = smr.GetBlendShapeWeight(idx);
                _items.Add(new BatchItem
                {
                    Path = path, BlendshapeName = bname, Index = idx,
                    OldWeight = oldWeight, NewWeight = newVal, Smr = smr,
                });
                previewItems.Add(new Dictionary<string, object>
                {
                    { "op", "blendshape_set" },
                    { "path", path },
                    { "blendshape", bname },
                    { "index", idx },
                    { "from", oldWeight },
                    { "to", newVal },
                });
            }

            var backupDir = Path.Combine(
                Application.persistentDataPath,
                "yorimashi_oplog",
                "backups",
                "batch_pending");

            return new WriteToolChanges
            {
                Items = previewItems,
                BackupPath = backupDir,
                Summary = "batch: " + _items.Count + " blendshape change(s) across "
                        + CountDistinctPaths() + " SMR(s)",
            };
        }

        private int CountDistinctPaths()
        {
            var set = new HashSet<string>();
            foreach (var it in _items) set.Add(it.Path);
            return set.Count;
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            if (_items == null || _items.Count == 0)
                throw new InvalidOperationException("preview state missing");

            // 单 Undo group：所有改动一次 Ctrl+Z 撤销
            var undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Yorimashi blendshape/set_batch (" + _items.Count + " items)");

            // 批量备份：一份 JSON 包含所有涉及的 SMR 的所有 blendshape 快照
            var actualBackupDir = Path.Combine(
                Application.persistentDataPath,
                "yorimashi_oplog",
                "backups",
                "batch_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            Directory.CreateDirectory(actualBackupDir);

            // 按 SMR path 去重存备份
            var seenSmrs = new HashSet<string>();
            foreach (var it in _items)
            {
                if (seenSmrs.Contains(it.Path)) continue;
                seenSmrs.Add(it.Path);
                WriteBackupSnapshot(actualBackupDir, it.Path, it.Smr);
            }

            // 应用改动
            var resultItems = new List<Dictionary<string, object>>();
            int verifiedCount = 0;
            int failedCount = 0;
            foreach (var it in _items)
            {
                try
                {
                    Undo.RecordObject(it.Smr, "batch blendshape " + it.BlendshapeName);
                    it.Smr.SetBlendShapeWeight(it.Index, it.NewWeight);
                    EditorUtility.SetDirty(it.Smr);
                    float readback = it.Smr.GetBlendShapeWeight(it.Index);
                    bool verified = Mathf.Approximately(readback, it.NewWeight);
                    if (verified) verifiedCount++; else failedCount++;
                    resultItems.Add(new Dictionary<string, object>
                    {
                        { "path", it.Path },
                        { "blendshape", it.BlendshapeName },
                        { "from", it.OldWeight },
                        { "to", it.NewWeight },
                        { "readback", readback },
                        { "verified", verified },
                    });
                }
                catch (Exception e)
                {
                    failedCount++;
                    resultItems.Add(new Dictionary<string, object>
                    {
                        { "path", it.Path },
                        { "blendshape", it.BlendshapeName },
                        { "error", e.Message },
                    });
                }
            }

            Undo.CollapseUndoOperations(undoGroup);

            return new Dictionary<string, object>
            {
                { "total", _items.Count },
                { "verified", verifiedCount },
                { "failed", failedCount },
                { "backupDir", actualBackupDir },
            };
        }

        private static void WriteBackupSnapshot(string dir, string path, SkinnedMeshRenderer smr)
        {
            var mesh = smr.sharedMesh;
            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(YorimashiEnvelope.EncodeString(path));
            sb.Append(",\"smrName\":").Append(YorimashiEnvelope.EncodeString(smr.name));
            sb.Append(",\"meshName\":").Append(YorimashiEnvelope.EncodeString(mesh.name));
            sb.Append(",\"snapshotAt\":").Append(YorimashiEnvelope.EncodeString(DateTime.UtcNow.ToString("o")));
            sb.Append(",\"blendshapes\":[");
            int total = mesh.blendShapeCount;
            for (int i = 0; i < total; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"index\":").Append(i);
                sb.Append(",\"name\":").Append(YorimashiEnvelope.EncodeString(mesh.GetBlendShapeName(i)));
                sb.Append(",\"weight\":").Append(smr.GetBlendShapeWeight(i));
                sb.Append('}');
            }
            sb.Append("]}");
            var safeName = path.Replace('/', '_').Replace('\\', '_') + ".json";
            File.WriteAllText(Path.Combine(dir, safeName), sb.ToString());
        }

        private static GameObject ResolveGameObjectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var parts = path.Split('/');
            if (parts.Length == 0) return null;

            GameObject current = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == parts[0]) { current = root; break; }
            }
            if (current == null) return null;

            for (int i = 1; i < parts.Length; i++)
            {
                var childTf = current.transform.Find(parts[i]);
                if (childTf == null) return null;
                current = childTf.gameObject;
            }
            return current;
        }
    }
}
