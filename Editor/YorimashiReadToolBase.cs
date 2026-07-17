// Yorimashi Read Tool base class — M4-B (只读 tool 通用基类)
//
// 用途: 只读 tool (menu_organizer/read_tree, ma_menu/list 等) 的统一入口.
// 相比 YorimashiWriteToolBase:
//   - 不管 scope guard (只读安全)
//   - 不管 backup / dry_run / apply
//   - 但复用 Diag.Begin/End 自动集成 + 结构化 error envelope + oplog
//
// 子类只需实现 BuildResult(parsedParams) 返回 Dictionary<string, object>.
// 抛异常 → 落 oplog(failed) + envelope 带 Diag snapshot.
//
// Sentinel: "M4-B READ TOOL BASE"

using System;
using System.Collections.Generic;
using System.Text;
using Yorimashi.Modder.Editor.Diagnostics;

namespace Yorimashi.Modder.Editor
{
    public abstract class YorimashiReadToolBase
    {
        public const string SentinelTag = "M4-B READ TOOL BASE";

        public abstract string ToolName { get; }

        /// <summary>
        /// 子类实现: 根据 parsedParams 计算结果 dict.
        /// 抛异常 → 基类接住, 落 oplog + envelope 带 Diag snapshot.
        /// </summary>
        protected abstract Dictionary<string, object> BuildResult(
            Dictionary<string, object> parsedParams);

        public string Execute(string paramsJson)
        {
            // 抽 requestId
            string requestId = null;
            try
            {
                if (!string.IsNullOrEmpty(paramsJson) && paramsJson != "null")
                {
                    var probe = new MiniJsonParser(paramsJson).ParseObject();
                    if (probe != null && probe.TryGetValue("_requestId", out var rid) && rid is string rs)
                        requestId = rs;
                }
            }
            catch { }
            Diag.Begin(ToolName, requestId);

            try
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
                    return BuildErrorJson(err);
                }

                Dictionary<string, object> result;
                try
                {
                    result = BuildResult(parsed);
                }
                catch (Exception e)
                {
                    YorimashiOpLog.Append(new YorimashiOpLog.Entry
                    {
                        Tool = ToolName, ParamsJson = paramsJson, DryRun = true,
                        Outcome = "failed", Error = e.Message,
                    });
                    return BuildErrorJson(e.Message);
                }

                YorimashiOpLog.Append(new YorimashiOpLog.Entry
                {
                    Tool = ToolName, ParamsJson = paramsJson, DryRun = true,
                    Outcome = "ok",
                });
                return BuildResultJson(result);
            }
            finally
            {
                Diag.End();
            }
        }

        static string BuildErrorJson(string err)
        {
            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"error\":").Append(YorimashiEnvelope.EncodeString(err ?? ""));
            var snap = Diag.Snapshot();
            if (snap != null && snap.HasContent)
            {
                var diagJson = snap.ToJson();
                if (!string.IsNullOrEmpty(diagJson))
                    sb.Append(",\"diag\":").Append(diagJson);
            }
            sb.Append('}');
            return sb.ToString();
        }

        static string BuildResultJson(Dictionary<string, object> result)
        {
            var sb = new StringBuilder(512);
            AppendDict(sb, result ?? new Dictionary<string, object>());
            return sb.ToString();
        }

        // Dict → JSON. 支持 string/bool/int/long/float/double/Dict/IEnumerable.
        static void AppendDict(StringBuilder sb, Dictionary<string, object> d)
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

        static void AppendValue(StringBuilder sb, object v)
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
            if (v is double dd)
            {
                if (double.IsNaN(dd) || double.IsInfinity(dd)) { sb.Append("null"); return; }
                sb.Append(dd.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                return;
            }
            if (v is Dictionary<string, object> nested) { AppendDict(sb, nested); return; }
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
            // fallback: 直接放已序列化的 string (子类可以 pre-serialize 复杂对象)
            sb.Append(v.ToString());
        }
    }
}
