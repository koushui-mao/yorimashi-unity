// Yorimashi debug/set_test_value tool — M3-W1-D 假改动 tool
//
// 用来 dogfood 整个改动 tool 链路（dry_run + oplog + 未来 gate）而**不改任何
// Unity 场景 / asset**。副作用是把一个 int value 写进 EditorPrefs 的
// "Yorimashi.Debug.TestValue" key，纯 Editor 状态，删掉 Library 就没了。
//
// 用途：
//   1. 好友一键装包 → 跑一次 dry_run → 验 oplog 里有 outcome=ok/dry_run=true 记录
//   2. 跑一次 dry_run=false → 验 EditorPrefs 值真的变了 + oplog 里两条（新旧）
//   3. 参数缺 value → 走 preview failed 路径 → oplog outcome=failed
//
// **红线**：此 tool 只是模板 + smoke。真改动 tool（blendshape/set 等）走
// 同一个基类但要额外接 gate（M3-W1-E）。
//
// Sentinel: "M3-W1-D DEBUG SET TEST VALUE"
using System.Collections.Generic;
using UnityEditor;

namespace Yorimashi.Modder.Editor
{
    internal class DebugSetTestValueTool : YorimashiWriteToolBase
    {
        public const string SentinelTag = "M3-W1-D DEBUG SET TEST VALUE";
        public const string PrefKey = "Yorimashi.Debug.TestValue";
        public const int DefaultValue = 0;

        public override string ToolName => "debug/set_test_value";

        /// <summary>
        /// 豁免 scope guard：此 tool 只写 EditorPrefs，不动 scene 数据，允许任意 path/无 path。
        /// 真改动 tool（blendshape/set 等）走默认 path 前缀 guard。
        /// </summary>
        protected override bool AllowWriteOnTarget(Dictionary<string, object> parsed, out string reason)
        {
            reason = null;
            return true;
        }

        public static void RegisterInto(System.Action registerCallback)
        {
            // called by YorimashiTools.RegisterAll() — 但也可独立 register
        }

        /// <summary>
        /// 参数：{ "value": int, "dry_run": bool? }
        /// </summary>
        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            if (!parsed.TryGetValue("value", out var vObj))
            {
                throw new System.ArgumentException("missing required 'value' (int)");
            }
            int newVal;
            if (vObj is double d) newVal = (int)d;
            else if (vObj is long l) newVal = (int)l;
            else if (vObj is int i) newVal = i;
            else throw new System.ArgumentException("'value' must be an integer, got " + vObj?.GetType().Name);

            int oldVal = EditorPrefs.GetInt(PrefKey, DefaultValue);

            var item = new Dictionary<string, object>
            {
                { "op", "editor_prefs_set" },
                { "key", PrefKey },
                { "from", oldVal },
                { "to", newVal },
            };
            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>> { item },
                BackupPath = null,  // EditorPrefs 无需备份文件
                Summary = "will change EditorPrefs " + PrefKey + ": " + oldVal + " → " + newVal,
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            int newVal = 0;
            if (parsed.TryGetValue("value", out var vObj))
            {
                if (vObj is double d) newVal = (int)d;
                else if (vObj is long l) newVal = (int)l;
                else if (vObj is int i) newVal = i;
            }
            int oldVal = EditorPrefs.GetInt(PrefKey, DefaultValue);
            EditorPrefs.SetInt(PrefKey, newVal);
            int readback = EditorPrefs.GetInt(PrefKey, DefaultValue);
            return new Dictionary<string, object>
            {
                { "from", oldVal },
                { "to", newVal },
                { "readback", readback },
                { "verified", readback == newVal },
            };
        }
    }
}
