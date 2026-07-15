// Yorimashi Write Tool base class — M3-W1-D
//
// 改动 tool 的抽象基类。子类只需实现 BuildPreview() + ApplyChanges()，
// 基类负责：
//   1. 解析 dry_run 参数
//   2. dry_run=true → 只调 BuildPreview，落 oplog(outcome=ok, dry_run=true)
//   3. dry_run=false → 调 BuildPreview 得 changes → (M3-W1-E 起) 触发
//      confirmation gate → approve 则 ApplyChanges → 落 oplog
//      当前阶段 (W1-D)：gate 走客户端 sink（可选注入），未注入则直接执行
//      并在 oplog 里标 confirmed_by="__gate_bypassed__"，便于 forensic 排查
//   4. 抛异常 → 落 oplog(outcome=failed, error=...)
//
// **红线**：任何真改动 tool 都必须继承这个基类，不能绕过。
// M3-W1-E 起 test_m3_tool_schemas.py 会加静态检查（Write tool 必须
// 走 WriteToolBase 通道）。
//
// Sentinel: "M3-W1-D WRITE TOOL BASE"
using System;
using System.Collections.Generic;
using System.Text;

namespace Yorimashi.Modder.Editor
{
    /// <summary>
    /// 改动 tool 的返回结构。ChangeItem 具体字段由子类塞。
    /// </summary>
    public struct WriteToolChanges
    {
        public List<Dictionary<string, object>> Items;
        public string BackupPath;  // 备份目录路径，dry_run 时为 null
        public string Summary;      // 一句话说明这次改动的影响
    }

    /// <summary>
    /// dry_run 预览结果。changes 结构固定，但每个 item 的 kv 由子类决定。
    /// </summary>
    public abstract class YorimashiWriteToolBase
    {
        public const string SentinelTag = "M3-W1-D WRITE TOOL BASE";

        /// <summary>子类必须提供 tool 名字（用于 oplog）。</summary>
        public abstract string ToolName { get; }

        /// <summary>
        /// dry_run 阶段调这个：只算"打算做什么"，不落盘、不改场景。
        /// 抛异常表示"预览就失败了"，会落 oplog(failed) 并直接返回给 agent。
        /// </summary>
        protected abstract WriteToolChanges BuildPreview(Dictionary<string, object> parsedParams);

        /// <summary>
        /// 真做阶段调这个。返回一个 dict 描述实际结果，会塞进 oplog result_summary。
        /// 抛异常 → oplog(failed)。子类负责在这里落备份到 backup_path（如需要）。
        /// </summary>
        protected abstract Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsedParams,
            WriteToolChanges preview);

        /// <summary>
        /// Registry 直接 register 的入口。签名对齐 ToolHandler：(paramsJson) -> resultJson。
        /// </summary>
        public string Execute(string paramsJson)
        {
            Dictionary<string, object> parsed;
            try
            {
                parsed = string.IsNullOrEmpty(paramsJson) || paramsJson == "null"
                    ? new Dictionary<string, object>()
                    : new MiniJsonParser(paramsJson).ParseObject();
            }
            catch (Exception e)
            {
                var err = "param parse failed: " + e.Message;
                YorimashiOpLog.Append(new YorimashiOpLog.Entry
                {
                    Tool = ToolName, ParamsJson = paramsJson, DryRun = true,
                    Outcome = "failed", Error = err,
                });
                return "{\"error\":" + YorimashiEnvelope.EncodeString(err) + "}";
            }

            bool dryRun = true;  // W1-D 铁律：**默认 dry_run**
            if (parsed.TryGetValue("dry_run", out var dv) && dv is bool db) dryRun = db;

            // Build preview
            WriteToolChanges preview;
            try
            {
                preview = BuildPreview(parsed);
            }
            catch (Exception e)
            {
                YorimashiOpLog.Append(new YorimashiOpLog.Entry
                {
                    Tool = ToolName, ParamsJson = paramsJson, DryRun = dryRun,
                    Outcome = "failed", Error = "preview: " + e.Message,
                });
                return "{\"error\":" + YorimashiEnvelope.EncodeString("preview failed: " + e.Message) + "}";
            }

            // dry_run 分支：只 log 不 apply
            if (dryRun)
            {
                var opId = YorimashiOpLog.Append(new YorimashiOpLog.Entry
                {
                    Tool = ToolName, ParamsJson = paramsJson, DryRun = true,
                    Outcome = "ok", ResultSummary = preview.Summary,
                });
                return BuildResultJson(true, preview, appliedResult: null, opId: opId);
            }

            // 真做：**Scope Guard**（M3-W1-E 新增，dry_run 不受此限制）
            // Apply 只允许对白名单路径下的 GameObject 生效，防手误命中主素体。
            // 白名单前缀：Ramune_test/, WriteTest/, Yorimashi_WriteTest/
            //   （shinano 项目里 Ramune_test 是用户专门复制的 R05 副本作沙盒，
            //    scene 里独立 root，改 blendshape weight 等 instance 属性不回写资产。
            //    ！！材质/mesh asset 类改动仍需在子类里额外拦截。）
            // 子类若参数里没 path（例如未来纯 asset 类 tool），可 override 换策略。
            if (!AllowWriteOnTarget(parsed, out var scopeReason))
            {
                var err = "scope guard denied: " + scopeReason;
                YorimashiOpLog.Append(new YorimashiOpLog.Entry
                {
                    Tool = ToolName, ParamsJson = paramsJson, DryRun = false,
                    Outcome = "denied", Error = err,
                    BackupPath = preview.BackupPath,
                });
                return "{\"error\":" + YorimashiEnvelope.EncodeString(err) + "}";
            }

            // **Asset Mutation Guard**（M3-C 新增，2026-07-14）
            //   Scope guard 只管 GameObject path 白名单。但有些 tool 的写操作会
            //   落到 shared asset（material、mesh、animatorController、prefab base 等），
            //   这些 asset 被多个副本共享 —— 写它 = 跨副本污染主素体。
            //   Path prefix 白名单挡不住这类污染。
            //   策略（fail-close）：
            //     - 子类 override MutatesAsset => true 才允许接触 asset
            //     - MutatesAsset=true 的子类必须实现 AllowAssetMutation(parsed, out reason)
            //     - 默认基类实现返回 false → 未 override 就自动拒绝
            //   即"漏做 = 安全默认"。将来某 tool 想改材质：显式声明 + 显式说明为什么安全。
            if (MutatesAsset)
            {
                if (!AllowAssetMutation(parsed, out var assetReason))
                {
                    var err = "asset mutation guard denied: " + assetReason;
                    YorimashiOpLog.Append(new YorimashiOpLog.Entry
                    {
                        Tool = ToolName, ParamsJson = paramsJson, DryRun = false,
                        Outcome = "denied", Error = err,
                        BackupPath = preview.BackupPath,
                    });
                    return "{\"error\":" + YorimashiEnvelope.EncodeString(err) + "}";
                }
            }

            // 真做：执行 + log
            Dictionary<string, object> applied;
            try
            {
                applied = ApplyChanges(parsed, preview);
            }
            catch (Exception e)
            {
                YorimashiOpLog.Append(new YorimashiOpLog.Entry
                {
                    Tool = ToolName, ParamsJson = paramsJson, DryRun = false,
                    Outcome = "failed", Error = "apply: " + e.Message,
                    BackupPath = preview.BackupPath,
                });
                return "{\"error\":" + YorimashiEnvelope.EncodeString("apply failed: " + e.Message) + "}";
            }

            var okId = YorimashiOpLog.Append(new YorimashiOpLog.Entry
            {
                Tool = ToolName, ParamsJson = paramsJson, DryRun = false,
                Outcome = "ok", ResultSummary = preview.Summary,
                BackupPath = preview.BackupPath,
                ConfirmedBy = "__gate_bypassed__",  // M3-W1-E 起换成真 gate approve subject
            });
            return BuildResultJson(false, preview, appliedResult: applied, opId: okId);
        }

        /// <summary>
        /// Scope Guard：只对白名单前缀下的 GameObject 允许真 apply。dry_run 不受限。
        ///
        /// 白名单前缀（大小写不敏感）：
        ///   - "Ramune_test/"       (用户在 shinano 里的隔离沙盒 root)
        ///   - "WriteTest/"         (通用测试 root)
        ///   - "Yorimashi_WriteTest/"
        ///
        /// 规则：
        ///   1. 从 parsed params 里抽 "path" 字段。
        ///   2. path 为空/缺失 → 拒绝（子类如果不用 path，请 override）。
        ///   3. path 必须以白名单前缀之一开头，忽略大小写。
        ///
        /// 子类可 override 放宽（如 DebugSetTestValueTool 走 EditorPrefs 不动 scene）。
        /// </summary>
        protected virtual bool AllowWriteOnTarget(Dictionary<string, object> parsedParams, out string reason)
        {
            string path = null;
            if (parsedParams != null
                && parsedParams.TryGetValue("path", out var pObj)
                && pObj is string ps)
            {
                path = ps;
            }
            if (string.IsNullOrEmpty(path))
            {
                reason = "missing required 'path' param — write tools must specify a target path under a whitelisted root (Ramune_test/, WriteTest/, Yorimashi_WriteTest/).";
                return false;
            }

            string[] allowlist = { "Ramune_test/", "WriteTest/", "Yorimashi_WriteTest/" };
            foreach (var prefix in allowlist)
            {
                if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    reason = null;
                    return true;
                }
            }
            reason = "target path '" + path + "' not under a whitelisted write root. "
                   + "Allowed prefixes: Ramune_test/, WriteTest/, Yorimashi_WriteTest/. "
                   + "dry_run allowed anywhere; apply blocked to protect the main avatar.";
            return false;
        }

        /// <summary>
        /// M3-C：声明该 tool 是否会引起 shared asset（material / mesh /
        /// animatorController / prefab base 等）状态被写入。默认 false。
        /// 默认 false 的 tool 只会改 scene 内 GameObject/Component 的
        /// instance state（如 SetActive、SkinnedMeshRenderer.SetBlendShapeWeight），
        /// 不回写 asset，副本级别隔离由 scope guard 保证。
        ///
        /// 子类如果实际会：
        ///   - EditorUtility.SetDirty(assetObj)
        ///   - Material.SetFloat/SetTexture 到 SharedMaterial
        ///   - AnimatorController 层写入
        ///   - Mesh 数据修改
        /// 就必须 override 返回 true，同时 override AllowAssetMutation 给出显式理由。
        /// 未 override = 走 fail-close 分支自动拒绝任何 apply。
        /// </summary>
        protected virtual bool MutatesAsset => false;

        /// <summary>
        /// M3-C：只有 MutatesAsset=true 的子类才会被基类调用。
        /// 默认实现直接拒绝 —— 强制子类显式 override 说明为什么这次 asset 写入是安全的
        /// （例如：只改临时 asset、已经复制过 material、asset 路径在允许目录下等）。
        /// </summary>
        protected virtual bool AllowAssetMutation(Dictionary<string, object> parsedParams, out string reason)
        {
            reason = "tool '" + ToolName + "' has MutatesAsset=true but has not overridden AllowAssetMutation. "
                   + "This is fail-close by design: any tool that touches shared assets must justify itself "
                   + "and constrain the target asset path/type. Refusing.";
            return false;
        }

        /// <summary>
        /// 结果 JSON 格式：{ dryRun, changes: [...], backupPath, summary, opId, applied?: {...} }
        /// </summary>
        private static string BuildResultJson(
            bool dryRun,
            WriteToolChanges preview,
            Dictionary<string, object> appliedResult,
            string opId)
        {
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"dryRun\":").Append(dryRun ? "true" : "false");
            sb.Append(",\"opId\":").Append(YorimashiEnvelope.EncodeString(opId ?? ""));
            sb.Append(",\"summary\":").Append(YorimashiEnvelope.EncodeString(preview.Summary ?? ""));
            sb.Append(",\"backupPath\":").Append(preview.BackupPath == null ? "null" : YorimashiEnvelope.EncodeString(preview.BackupPath));
            sb.Append(",\"changes\":[");
            if (preview.Items != null)
            {
                for (int i = 0; i < preview.Items.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    AppendDict(sb, preview.Items[i]);
                }
            }
            sb.Append(']');
            if (appliedResult != null)
            {
                sb.Append(",\"applied\":");
                AppendDict(sb, appliedResult);
            }
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 简易 dict → JSON，只支持 string/bool/int/long/float/double/null。
        /// 复杂对象请子类自己 pre-serialize 成 string 再塞。
        /// </summary>
        private static void AppendDict(StringBuilder sb, Dictionary<string, object> d)
        {
            sb.Append('{');
            bool first = true;
            foreach (var kv in d)
            {
                if (!first) sb.Append(',');
                first = false;
                sb.Append(YorimashiEnvelope.EncodeString(kv.Key)).Append(':');
                AppendValue(sb, kv.Value);
            }
            sb.Append('}');
        }

        private static void AppendValue(StringBuilder sb, object v)
        {
            if (v == null) { sb.Append("null"); return; }
            if (v is string s) { sb.Append(YorimashiEnvelope.EncodeString(s)); return; }
            if (v is bool b) { sb.Append(b ? "true" : "false"); return; }
            if (v is int || v is long) { sb.Append(v.ToString()); return; }
            if (v is float f)
            {
                if (float.IsNaN(f) || float.IsInfinity(f)) { sb.Append("null"); return; }
                sb.Append(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            if (v is double d)
            {
                if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
                sb.Append(d.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            if (v is Dictionary<string, object> nested)
            {
                AppendDict(sb, nested);
                return;
            }
            if (v is System.Collections.IEnumerable enumerable && !(v is string))
            {
                sb.Append('[');
                bool first = true;
                foreach (var item in enumerable)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    AppendValue(sb, item);
                }
                sb.Append(']');
                return;
            }
            // fallback: string 化
            sb.Append(YorimashiEnvelope.EncodeString(v.ToString()));
        }
    }
}
