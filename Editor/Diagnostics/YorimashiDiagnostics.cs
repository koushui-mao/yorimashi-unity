// Yorimashi Diagnostics — M4-A0.1
//
// 目的：让每个 tool 抛出的错误自动携带"失败时我在哪一步 / 我以为什么 / 实际什么 /
// 怎么改"的上下文。客户 dogfood 时看到日志能直接定位问题，agent 排错也不需要瞎猜。
//
// API 四件套：
//   using (Diag.Step("apply.write_root_asset")) {
//       Diag.Expect("AssetDatabase.CreateAsset 应能写入");
//       try {
//           AssetDatabase.CreateAsset(root, path);
//       } catch (Exception ex) {
//           Diag.Actual(ex);
//           Diag.Hint(0.95, "父目录不存在", "OutputWriter.cs:47 前加 CreateAssetFolderRecursive");
//           Diag.Hint(0.03, "avatar 目录含非法字符");
//           throw;   // 让 WriteToolBase catch 并落 oplog + envelope
//       }
//   }
//
// 语义：
//   Diag.Step(name)     开一步作用域。using-scope 结束自动收步（unwind）。嵌套支持。
//   Diag.Expect(text)   记本步的期望语义（"我以为 X"）
//   Diag.Actual(v)      记本步的实际结果（string / Exception / object）
//   Diag.Hint(prob, cause, [fix])  提示可能原因 + 修复建议，可多次调用
//
// 数据模型：AsyncLocal 存 stack + hints，跨异步安全。异常上抛后 base class 从
// Diag.Snapshot() 拿到完整上下文塞进 oplog + wss error envelope。
//
// Sentinel: "M4-A0.1 DIAGNOSTICS BASELINE"
//
// 兼容性：老 tool 不用 Diag 也照跑（Diag.Snapshot 返回空 payload），
// WriteToolBase envelope 只在有内容时附加 "diag" 字段。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace Yorimashi.Modder.Editor.Diagnostics
{
    /// <summary>
    /// 单步作用域记录。using-scope 结束时自动 pop。
    /// </summary>
    public sealed class DiagStep : IDisposable
    {
        public readonly string Name;
        public readonly DateTime StartUtc;
        public readonly int Depth;
        public string Expected;
        public string Actual;
        public string ActualKind;    // "exception" / "value" / "message"
        public readonly List<DiagHint> Hints = new List<DiagHint>();
        public double ElapsedMs => (DateTime.UtcNow - StartUtc).TotalMilliseconds;

        private bool _disposed;
        internal DiagStep(string name, int depth)
        {
            Name = name;
            StartUtc = DateTime.UtcNow;
            Depth = depth;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Diag.PopStep(this);
        }
    }

    public struct DiagHint
    {
        public double Probability;   // 0..1
        public string Cause;
        public string Fix;           // 可选，null 表示只提示成因不给修复
    }

    /// <summary>
    /// 一次 tool 执行的完整诊断快照。序列化到 wss error envelope + oplog。
    /// </summary>
    public sealed class DiagSnapshot
    {
        public string RequestId;
        public string ToolName;
        public string FailedStepPath;   // 例如 "apply.write_root_asset"
        public int FailedStepIndex;     // 从 root 走到失败步是第几步（1-based）
        public int TotalSteps;
        public double ElapsedMs;
        public List<DiagStep> Steps = new List<DiagStep>();

        public bool HasContent => Steps.Count > 0;

        /// <summary>
        /// 序列化为 envelope 用的 JSON string。空快照返回 null（envelope 就不带 diag 字段）。
        /// </summary>
        public string ToJson()
        {
            if (!HasContent) return null;
            var sb = new StringBuilder(512);
            sb.Append('{');
            sb.Append("\"requestId\":").Append(JsonQ(RequestId));
            sb.Append(",\"toolName\":").Append(JsonQ(ToolName));
            sb.Append(",\"failedStep\":").Append(JsonQ(FailedStepPath));
            sb.Append(",\"failedStepIndex\":").Append(FailedStepIndex);
            sb.Append(",\"totalSteps\":").Append(TotalSteps);
            sb.Append(",\"elapsedMs\":").Append(FormatMs(ElapsedMs));
            sb.Append(",\"steps\":[");
            for (int i = 0; i < Steps.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var s = Steps[i];
                sb.Append('{');
                sb.Append("\"name\":").Append(JsonQ(s.Name));
                sb.Append(",\"depth\":").Append(s.Depth);
                sb.Append(",\"elapsedMs\":").Append(FormatMs(s.ElapsedMs));
                if (s.Expected != null)
                    sb.Append(",\"expected\":").Append(JsonQ(s.Expected));
                if (s.Actual != null)
                {
                    sb.Append(",\"actualKind\":").Append(JsonQ(s.ActualKind ?? "value"));
                    sb.Append(",\"actual\":").Append(JsonQ(s.Actual));
                }
                if (s.Hints.Count > 0)
                {
                    sb.Append(",\"hints\":[");
                    for (int h = 0; h < s.Hints.Count; h++)
                    {
                        if (h > 0) sb.Append(',');
                        var hint = s.Hints[h];
                        sb.Append('{');
                        sb.Append("\"probability\":").Append(FormatMs(hint.Probability));
                        sb.Append(",\"cause\":").Append(JsonQ(hint.Cause));
                        if (hint.Fix != null)
                            sb.Append(",\"fix\":").Append(JsonQ(hint.Fix));
                        sb.Append('}');
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }
            sb.Append(']');
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// 生成"人看得懂"的多行诊断报告。用于 Unity Console 打印 + gateway 转发 log。
        /// </summary>
        public string ToHumanReport()
        {
            if (!HasContent) return string.Empty;
            var sb = new StringBuilder(1024);
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.Append("[FAIL] ").Append(ToolName ?? "?").Append("  request_id=").AppendLine(RequestId ?? "?");
            sb.AppendLine("════════════════════════════════════════════════════════════");
            sb.Append("step:      ").Append(FailedStepPath ?? "?")
              .Append("  (").Append(FailedStepIndex).Append('/').Append(TotalSteps).AppendLine(")");
            sb.Append("elapsed:   ").Append(FormatMs(ElapsedMs)).AppendLine("ms total");

            // 找最深最后一个 step（失败步）
            DiagStep failed = null;
            for (int i = Steps.Count - 1; i >= 0; i--)
            {
                if (Steps[i].Actual != null || Steps[i].Hints.Count > 0)
                {
                    failed = Steps[i];
                    break;
                }
            }
            if (failed == null && Steps.Count > 0) failed = Steps[Steps.Count - 1];

            if (failed != null)
            {
                sb.AppendLine();
                if (failed.Expected != null)
                {
                    sb.AppendLine("── expected ──");
                    sb.AppendLine(failed.Expected);
                    sb.AppendLine();
                }
                if (failed.Actual != null)
                {
                    sb.AppendLine("── actual ──");
                    sb.AppendLine(failed.Actual);
                    sb.AppendLine();
                }
                if (failed.Hints.Count > 0)
                {
                    sb.AppendLine("── fix candidates (ranked) ──");
                    // sort by probability desc
                    var sorted = new List<DiagHint>(failed.Hints);
                    sorted.Sort((a, b) => b.Probability.CompareTo(a.Probability));
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        var h = sorted[i];
                        sb.Append(i + 1).Append(". [")
                          .Append((int)Math.Round(h.Probability * 100)).Append("%] ")
                          .AppendLine(h.Cause);
                        if (h.Fix != null)
                        {
                            sb.Append("     → ").AppendLine(h.Fix);
                        }
                    }
                }
            }
            sb.Append("════════════════════════════════════════════════════════════");
            return sb.ToString();
        }

        private static string JsonQ(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        private static string FormatMs(double v)
        {
            if (double.IsNaN(v) || double.IsInfinity(v)) return "null";
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// Diagnostics 入口。AsyncLocal 存栈，跨异步/协程安全（Unity Editor 单线程主线程为主，
    /// 但 Task.Run / async 场景也要能撑住）。
    /// </summary>
    public static class Diag
    {
        public const string SentinelTag = "M4-A0.1 DIAGNOSTICS BASELINE";

        private sealed class DiagContext
        {
            public string RequestId;
            public string ToolName;
            public DateTime StartUtc = DateTime.UtcNow;
            public readonly List<DiagStep> Steps = new List<DiagStep>();
            public readonly Stack<DiagStep> Active = new Stack<DiagStep>();
            public DiagStep FailedStep;   // 首次进入 Actual 的步骤
        }

        private static readonly AsyncLocal<DiagContext> _ctx = new AsyncLocal<DiagContext>();

        /// <summary>
        /// 每个 tool 入口调一次。Reset 上下文 + 设置 requestId + toolName。
        /// 若中间没跑 Reset，Snapshot 也能拿到最后一次上下文（老兼容）。
        /// </summary>
        public static void Begin(string toolName, string requestId)
        {
            _ctx.Value = new DiagContext
            {
                RequestId = requestId ?? Guid.NewGuid().ToString("N").Substring(0, 12),
                ToolName = toolName ?? "?",
            };
        }

        /// <summary>
        /// tool 出口调一次。Snapshot 拿到最终结构。Begin/End 也可以省略——
        /// 只有 Diag.Step 用了才有内容，Snapshot 空快照 envelope 就不带 diag 字段。
        /// </summary>
        public static DiagSnapshot End()
        {
            var snap = Snapshot();
            _ctx.Value = null;
            return snap;
        }

        /// <summary>
        /// 开一步。using-scope 结束自动 pop。
        /// </summary>
        public static DiagStep Step(string name)
        {
            var c = EnsureContext();
            var step = new DiagStep(name ?? "?", c.Active.Count);
            c.Active.Push(step);
            c.Steps.Add(step);
            return step;
        }

        /// <summary>
        /// 声明当前 step 的期望语义（"我以为 X 应该发生"）。
        /// </summary>
        public static void Expect(string text)
        {
            var top = TryPeek();
            if (top == null) return;
            top.Expected = text;
        }

        /// <summary>
        /// 记录当前 step 的实际结果。可传 Exception / string / 任意 object（ToString）。
        /// 第一个 Actual 调用会锁定 FailedStep。
        /// </summary>
        public static void Actual(object value)
        {
            var top = TryPeek();
            if (top == null) return;
            if (value == null)
            {
                top.Actual = "null";
                top.ActualKind = "value";
            }
            else if (value is Exception ex)
            {
                top.Actual = ex.GetType().Name + ": " + ex.Message
                             + (ex.StackTrace != null ? "\n" + ex.StackTrace : "");
                top.ActualKind = "exception";
            }
            else if (value is string s)
            {
                top.Actual = s;
                top.ActualKind = "message";
            }
            else
            {
                top.Actual = value.ToString();
                top.ActualKind = "value";
            }
            var c = _ctx.Value;
            if (c != null && c.FailedStep == null) c.FailedStep = top;
        }

        /// <summary>
        /// 提示可能原因 + 修复建议。probability 0..1。可多次调用，会按概率降序展示。
        /// </summary>
        public static void Hint(double probability, string cause, string fix = null)
        {
            var top = TryPeek();
            if (top == null) return;
            top.Hints.Add(new DiagHint
            {
                Probability = Math.Max(0, Math.Min(1, probability)),
                Cause = cause,
                Fix = fix,
            });
        }

        /// <summary>
        /// 任何时候都能拿快照。Step 已 pop 或未 pop 都算数。
        /// </summary>
        public static DiagSnapshot Snapshot()
        {
            var c = _ctx.Value;
            if (c == null) return new DiagSnapshot();

            var snap = new DiagSnapshot
            {
                RequestId = c.RequestId,
                ToolName = c.ToolName,
                ElapsedMs = (DateTime.UtcNow - c.StartUtc).TotalMilliseconds,
                TotalSteps = c.Steps.Count,
                Steps = new List<DiagStep>(c.Steps),
            };
            if (c.FailedStep != null)
            {
                snap.FailedStepPath = BuildStepPath(c.FailedStep, c.Steps);
                snap.FailedStepIndex = c.Steps.IndexOf(c.FailedStep) + 1;
            }
            else if (c.Steps.Count > 0)
            {
                var last = c.Steps[c.Steps.Count - 1];
                snap.FailedStepPath = BuildStepPath(last, c.Steps);
                snap.FailedStepIndex = c.Steps.Count;
            }
            return snap;
        }

        // WriteToolBase 调用：unwind 用
        internal static void PopStep(DiagStep step)
        {
            var c = _ctx.Value;
            if (c == null) return;
            // 只 pop 栈顶的（防误用嵌套错位）
            if (c.Active.Count > 0 && ReferenceEquals(c.Active.Peek(), step))
            {
                c.Active.Pop();
            }
        }

        private static DiagContext EnsureContext()
        {
            var c = _ctx.Value;
            if (c == null)
            {
                c = new DiagContext
                {
                    RequestId = Guid.NewGuid().ToString("N").Substring(0, 12),
                    ToolName = "?",
                };
                _ctx.Value = c;
            }
            return c;
        }

        private static DiagStep TryPeek()
        {
            var c = _ctx.Value;
            if (c == null || c.Active.Count == 0) return null;
            return c.Active.Peek();
        }

        /// <summary>
        /// 根据 step 在 Steps 里的位置 + Depth，重建"父.子.孙"路径。
        /// </summary>
        private static string BuildStepPath(DiagStep target, List<DiagStep> allSteps)
        {
            var path = new List<string>();
            int targetIdx = allSteps.IndexOf(target);
            if (targetIdx < 0) return target.Name;
            path.Add(target.Name);
            int curDepth = target.Depth;
            // 向前找 depth 递减的祖先
            for (int i = targetIdx - 1; i >= 0 && curDepth > 0; i--)
            {
                if (allSteps[i].Depth == curDepth - 1)
                {
                    path.Insert(0, allSteps[i].Name);
                    curDepth--;
                }
            }
            return string.Join(".", path.ToArray());
        }
    }
}
