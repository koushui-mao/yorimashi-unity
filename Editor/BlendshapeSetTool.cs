// Yorimashi blendshape/set — M3-W1-E 第一个真改动 tool
//
// 改 SkinnedMeshRenderer 上的 blendshape weight (0-100)。
// 走 YorimashiWriteToolBase：默认 dry_run=true，只有显式 dry_run=false 才真改。
// Scope Guard 强制 path 必须在白名单前缀下（Ramune_test/ 等）—— 主素体天然免疫。
//
// 参数：
//   path          (string, required) — GameObject 路径，必须能挂到 SMR
//   blendshape    (string, required) — blendshape 名字（在 mesh 上必须存在）
//   value         (number, required) — 目标 weight (0-100，会 clamp)
//   dry_run       (bool, optional, default true) — 默认预览
//
// 返回：走 base 的 result JSON:
//   { dryRun, opId, summary, backupPath, changes:[...], applied?:{...} }
//   changes item: { op:"blendshape_set", path, blendshape, index, from, to }
//   applied:      { from, to, readback, verified }
//
// 备份：apply 前把 SMR 上所有 blendshape 的 (name → weight) 快照存 JSON 到
//   %APPDATA%\..\LocalLow\<company>\<project>\yorimashi_oplog\backups\op_<id>\<path>.json
// 只落 SMR 上原有的 weight，不覆盖 mesh asset。回滚：读 JSON，逐个 SetBlendShapeWeight。
//
// Sentinel: "M3-W1-E BLENDSHAPE SET"
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal class BlendshapeSetTool : YorimashiWriteToolBase
    {
        public const string SentinelTag = "M3-W1-E BLENDSHAPE SET";

        public override string ToolName => "blendshape/set";

        // 存 preview 时算出的东西，避免 Apply 重复 resolve。
        // 每次 Execute 都新建实例（handler 是 static 方法，Registry 用 lambda 包实例）—
        // 实际上 base.Execute() 一次调用里 BuildPreview 和 ApplyChanges 都用同一份 parsed。
        // 我们把 SMR 引用塞进 preview 里通过 changes item[0] 的额外字段带过去。
        private SkinnedMeshRenderer _resolvedSmr;
        private int _resolvedIndex;
        private float _oldWeight;
        private float _newWeight;
        private string _blendshapeName;
        private string _targetPath;

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            // ---- 参数校验 ----
            if (!parsed.TryGetValue("path", out var pObj) || !(pObj is string path) || string.IsNullOrEmpty(path))
                throw new ArgumentException("missing required 'path' (string)");
            if (!parsed.TryGetValue("blendshape", out var bObj) || !(bObj is string bname) || string.IsNullOrEmpty(bname))
                throw new ArgumentException("missing required 'blendshape' (string)");
            if (!parsed.TryGetValue("value", out var vObj))
                throw new ArgumentException("missing required 'value' (0-100)");

            float newVal;
            if (vObj is double dd) newVal = (float)dd;
            else if (vObj is long ll) newVal = (float)ll;
            else if (vObj is int ii) newVal = (float)ii;
            else if (vObj is float ff) newVal = ff;
            else throw new ArgumentException("'value' must be numeric, got " + vObj?.GetType().Name);

            // clamp 0-100
            if (newVal < 0f) newVal = 0f;
            if (newVal > 100f) newVal = 100f;

            // ---- 解析 GO ----
            var go = ResolveGameObjectPath(path);
            if (go == null)
                throw new InvalidOperationException("GameObject not found at path: " + path);

            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null)
                throw new InvalidOperationException("GameObject '" + path + "' has no SkinnedMeshRenderer");
            if (smr.sharedMesh == null)
                throw new InvalidOperationException("SkinnedMeshRenderer at '" + path + "' has null sharedMesh");

            // ---- 找 blendshape index ----
            var mesh = smr.sharedMesh;
            int idx = mesh.GetBlendShapeIndex(bname);
            if (idx < 0)
                throw new InvalidOperationException("blendshape '" + bname + "' not found on mesh at '" + path + "' (total=" + mesh.blendShapeCount + ")");

            float oldWeight = smr.GetBlendShapeWeight(idx);

            _resolvedSmr = smr;
            _resolvedIndex = idx;
            _oldWeight = oldWeight;
            _newWeight = newVal;
            _blendshapeName = bname;
            _targetPath = path;

            var item = new Dictionary<string, object>
            {
                { "op", "blendshape_set" },
                { "path", path },
                { "blendshape", bname },
                { "index", idx },
                { "from", oldWeight },
                { "to", newVal },
            };

            // 备份路径：dry_run 时不建目录，apply 时才落地（避免 dry_run 污染磁盘）
            // 这里给一个"预期路径"给 preview 展示，实际 Directory.CreateDirectory 在 Apply 里
            string backupDir = Path.Combine(
                Application.persistentDataPath,
                "yorimashi_oplog",
                "backups",
                "op_pending");

            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>> { item },
                BackupPath = backupDir,
                Summary = "will set blendshape '" + bname + "' on '" + path + "': " + oldWeight + " → " + newVal,
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            if (_resolvedSmr == null)
                throw new InvalidOperationException("preview state missing (BuildPreview not run?)");

            // ---- 落备份：Apply 阶段建实际目录（op id 化到 oplog 后再改名不划算，直接用 timestamp）----
            var actualBackupDir = Path.Combine(
                Application.persistentDataPath,
                "yorimashi_oplog",
                "backups",
                "blendshape_set_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            Directory.CreateDirectory(actualBackupDir);

            var mesh = _resolvedSmr.sharedMesh;
            var sb = new StringBuilder();
            sb.Append("{\"path\":").Append(YorimashiEnvelope.EncodeString(_targetPath));
            sb.Append(",\"smrName\":").Append(YorimashiEnvelope.EncodeString(_resolvedSmr.name));
            sb.Append(",\"meshName\":").Append(YorimashiEnvelope.EncodeString(mesh.name));
            sb.Append(",\"snapshotAt\":").Append(YorimashiEnvelope.EncodeString(DateTime.UtcNow.ToString("o")));
            sb.Append(",\"blendshapes\":[");
            int total = mesh.blendShapeCount;
            for (int i = 0; i < total; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append("{\"index\":").Append(i);
                sb.Append(",\"name\":").Append(YorimashiEnvelope.EncodeString(mesh.GetBlendShapeName(i)));
                sb.Append(",\"weight\":").Append(_resolvedSmr.GetBlendShapeWeight(i));
                sb.Append('}');
            }
            sb.Append("]}");

            var safeName = _targetPath.Replace('/', '_').Replace('\\', '_') + ".json";
            var backupFile = Path.Combine(actualBackupDir, safeName);
            File.WriteAllText(backupFile, sb.ToString());

            // ---- Undo 注册 + 真改 ----
            Undo.RecordObject(_resolvedSmr, "Yorimashi blendshape/set " + _blendshapeName);
            _resolvedSmr.SetBlendShapeWeight(_resolvedIndex, _newWeight);
            EditorUtility.SetDirty(_resolvedSmr);

            // 读回来验证
            float readback = _resolvedSmr.GetBlendShapeWeight(_resolvedIndex);
            bool verified = Mathf.Approximately(readback, _newWeight);

            return new Dictionary<string, object>
            {
                { "from", _oldWeight },
                { "to", _newWeight },
                { "readback", readback },
                { "verified", verified },
                { "backupDir", actualBackupDir },
                { "backupFile", backupFile },
            };
        }

        // ---- 路径解析（复用 YorimashiTools 的逻辑，但那边是 private，这里独立实现）----
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
