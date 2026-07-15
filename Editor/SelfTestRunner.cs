// Yorimashi Self Test Runner — M3-T2 friend-test edition
//
// 一键跑完 M3-T2 4 个只读 tool，把结果落两个文件 + 一个 zip 到桌面。
// 不需要 wss / 不需要 Python / 不需要网络。全部在 Unity Editor 主线程同步跑。
//
// ChatWindow 里加一个 "Run Self Test" 按钮点一下就完事。
//
// Sentinel: "M3-T2 SELF TEST"
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal static class SelfTestRunner
    {
        public const string SentinelTag = "M3-T2 SELF TEST";

        /// <summary>
        /// 单步测试计划。跟 friend_test_hub.py 的 TEST_PLAN 一一对应。
        /// </summary>
        private struct Step
        {
            public int Index;
            public string Tool;
            public string ParamsJson;
            public string[] MustHaveKeys;
            public string Note;
        }

        private static readonly Step[] Plan = new Step[]
        {
            new Step
            {
                Index = 1,
                Tool = "unity/read_project_context",
                ParamsJson = "{}",
                MustHaveKeys = new[] { "unity", "activeScene", "packagesRelevant", "avatars", "vrchatSdkPresent" },
                Note = "环境快照。不装 VRChat SDK 时 vrchatSdkPresent=false 是正常的。",
            },
            new Step
            {
                Index = 2,
                Tool = "hierarchy/find_by_name",
                ParamsJson = "{\"query\":\"Armature\",\"mode\":\"contains\",\"maxResults\":20}",
                MustHaveKeys = new[] { "query", "mode", "count", "results" },
                Note = "按名字模糊查找 Armature。空场景 count=0 也算 PASS。",
            },
            new Step
            {
                Index = 3,
                Tool = "scene/list_root_gameobjects",
                ParamsJson = "{}",
                MustHaveKeys = new[] { "scene", "count", "names" },
                Note = "列 root GameObject。用第 4 步选一个 root 名字。",
            },
            new Step
            {
                Index = 4,
                Tool = "hierarchy/get_active",
                ParamsJson = null,  // 运行时用 step 3 结果生成
                MustHaveKeys = new[] { "path", "found" },
                Note = "查单个 GO 的 transform + component 列表。",
            },
            new Step
            {
                Index = 5,
                Tool = "component/list",
                ParamsJson = null,  // 运行时用 step 3 结果生成
                MustHaveKeys = new[] { "path", "found" },
                Note = "列 GO 上的 Component。M3-T3 新加。",
            },
            new Step
            {
                Index = 6,
                Tool = "hierarchy/get_transform",
                ParamsJson = null,  // 运行时用 step 3 结果生成
                MustHaveKeys = new[] { "path", "found" },
                Note = "查 GO 的 transform（不含组件）。M3-T3 新加。",
            },
            new Step
            {
                Index = 7,
                Tool = "blendshape/list",
                ParamsJson = null,  // 运行时用 step 3 结果生成
                MustHaveKeys = new[] { "path", "found" },
                Note = "列 SMR 的 blendshape。M3-T3 新加。不是 SMR 也算 PASS (reason=no_smr)。",
            },
            new Step
            {
                Index = 8,
                Tool = "debug/set_test_value",
                ParamsJson = "{\"value\":42,\"dry_run\":true}",
                MustHaveKeys = new[] { "dryRun", "changes", "opId", "summary" },
                Note = "M3-W1-D 假改动 tool dry_run 预览。不改 Unity，只落 oplog。",
            },
        };

        /// <summary>
        /// 主入口：跑全部测试，返回摘要（PASS 数 / FAIL 数 / 输出目录）。
        /// 全程主线程同步执行，不阻塞太久（每个 tool 是纯 Editor API 调用）。
        /// </summary>
        public static Summary Run(Action<string> logSink)
        {
            var results = new List<StepResult>();
            string firstRootName = null;

            logSink?.Invoke("[selftest] ============================================");
            logSink?.Invoke("[selftest]  M3-T2 Self Test 开始 (4 步只读 tool)");
            logSink?.Invoke("[selftest] ============================================");

            foreach (var step in Plan)
            {
                var stepResult = new StepResult
                {
                    Index = step.Index,
                    Tool = step.Tool,
                    Note = step.Note,
                    Ok = false,
                };

                // step 4/5/6/7 用 step 3 的 first root name 填 path
                string paramsJson = step.ParamsJson;
                bool needsFirstRoot = step.Index == 4 || step.Index == 5
                                       || step.Index == 6 || step.Index == 7;
                if (needsFirstRoot)
                {
                    if (!string.IsNullOrEmpty(firstRootName))
                    {
                        paramsJson = "{\"path\":" + YorimashiEnvelope.EncodeString(firstRootName) + "}";
                    }
                    else
                    {
                        paramsJson = "{\"path\":\"no_root_available\"}";
                    }
                }
                stepResult.ParamsJson = paramsJson;

                logSink?.Invoke($"[selftest] Step {step.Index}: {step.Tool}");
                logSink?.Invoke($"[selftest]   note: {step.Note}");

                string rawJson;
                try
                {
                    rawJson = YorimashiToolRegistry.InvokeOnMainThread(step.Tool, paramsJson);
                }
                catch (Exception ex)
                {
                    stepResult.Ok = false;
                    stepResult.Error = ex.GetType().Name + ": " + ex.Message;
                    stepResult.RawResult = null;
                    results.Add(stepResult);
                    logSink?.Invoke($"[selftest]   ❌ FAIL: {stepResult.Error}");
                    continue;
                }

                stepResult.RawResult = rawJson;

                // 解析出 result 顶层对象，看是不是包含所有 MustHaveKeys
                var missing = MissingKeys(rawJson, step.MustHaveKeys);
                if (missing.Count == 0)
                {
                    stepResult.Ok = true;
                    logSink?.Invoke("[selftest]   ✅ PASS");
                }
                else
                {
                    stepResult.Ok = false;
                    stepResult.Error = "missing keys: " + string.Join(",", missing);
                    logSink?.Invoke($"[selftest]   ❌ FAIL: {stepResult.Error}");
                }

                // 从 step 3 抽 first root name（供 step 4 用）
                if (step.Index == 3 && stepResult.Ok)
                {
                    firstRootName = ExtractFirstName(rawJson);
                    if (firstRootName != null)
                    {
                        logSink?.Invoke($"[selftest]   → 用作 step 4 path: {firstRootName}");
                    }
                }

                results.Add(stepResult);
            }

            // 写文件
            var outputDir = ResolveOutputDir();
            Directory.CreateDirectory(outputDir);
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var rawPath = Path.Combine(outputDir, $"raw_{timestamp}.json");
            var txtPath = Path.Combine(outputDir, $"report_{timestamp}.txt");
            var zipPath = Path.Combine(outputDir, $"yorimashi_self_test_{timestamp}.zip");

            var envInfo = CollectEnvInfo();
            WriteRawJson(rawPath, results, envInfo, timestamp);
            WriteTxtReport(txtPath, results, envInfo, timestamp);
            WriteZip(zipPath, rawPath, txtPath);

            int passCount = 0, failCount = 0;
            foreach (var r in results)
            {
                if (r.Ok) passCount++;
                else failCount++;
            }

            logSink?.Invoke("[selftest] ============================================");
            logSink?.Invoke($"[selftest]  完成: {passCount} PASS / {failCount} FAIL / {results.Count} total");
            logSink?.Invoke($"[selftest]  报告: {txtPath}");
            logSink?.Invoke($"[selftest]  zip:  {zipPath}");
            logSink?.Invoke("[selftest] ============================================");
            logSink?.Invoke("[selftest]  ▶ 把 zip 发给依代作者");

            return new Summary
            {
                PassCount = passCount,
                FailCount = failCount,
                Total = results.Count,
                OutputDir = outputDir,
                ZipPath = zipPath,
                TxtPath = txtPath,
            };
        }

        // ---- 结果结构 -----------------------------------------------------

        public struct StepResult
        {
            public int Index;
            public string Tool;
            public string Note;
            public string ParamsJson;
            public string RawResult;   // handler 原样返回的 JSON 字符串
            public bool Ok;
            public string Error;       // 空 = 无错
        }

        public struct Summary
        {
            public int PassCount;
            public int FailCount;
            public int Total;
            public string OutputDir;
            public string ZipPath;
            public string TxtPath;
        }

        // ---- 辅助 ---------------------------------------------------------

        /// <summary>
        /// 用 MiniJsonParser 解顶层对象，返回缺的 key 列表。JSON 不合法 → 全部 key 都算缺。
        /// </summary>
        private static List<string> MissingKeys(string json, string[] keys)
        {
            var missing = new List<string>();
            if (string.IsNullOrEmpty(json))
            {
                missing.AddRange(keys);
                return missing;
            }
            Dictionary<string, object> obj;
            try
            {
                obj = new MiniJsonParser(json).ParseObject();
            }
            catch
            {
                missing.AddRange(keys);
                return missing;
            }
            foreach (var k in keys)
            {
                if (!obj.ContainsKey(k)) missing.Add(k);
            }
            return missing;
        }

        /// <summary>
        /// 从 scene/list_root_gameobjects 的返回里抽出 names[0]。找不到返回 null。
        /// </summary>
        private static string ExtractFirstName(string rawJson)
        {
            try
            {
                var obj = new MiniJsonParser(rawJson).ParseObject();
                if (obj.TryGetValue("names", out var namesObj) && namesObj is List<object> names)
                {
                    foreach (var n in names)
                    {
                        if (n is string s && !string.IsNullOrEmpty(s)) return s;
                    }
                }
            }
            catch { }
            return null;
        }

        private static string ResolveOutputDir()
        {
            // 优先桌面（Windows 是 %USERPROFILE%\Desktop）；不存在退回 UserProfile。
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var baseDir = !string.IsNullOrEmpty(desktop) && Directory.Exists(desktop) ? desktop : userProfile;
            return Path.Combine(baseDir, "yorimashi_self_test");
        }

        private static Dictionary<string, string> CollectEnvInfo()
        {
            var d = new Dictionary<string, string>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "projectPath", Path.GetDirectoryName(Application.dataPath) },
                { "projectName", Application.productName },
                { "systemOS", SystemInfo.operatingSystem },
                { "generatedAt", DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss") },
                { "runnerVersion", "M3-T2-selftest-1" },
            };
            return d;
        }

        private static void WriteRawJson(string path, List<StepResult> results,
            Dictionary<string, string> env, string timestamp)
        {
            var sb = new StringBuilder(4096);
            sb.Append("{\n");
            sb.Append("  \"generated_at\": ").Append(YorimashiEnvelope.EncodeString(env["generatedAt"])).Append(",\n");
            sb.Append("  \"runner_version\": ").Append(YorimashiEnvelope.EncodeString(env["runnerVersion"])).Append(",\n");
            sb.Append("  \"environment\": {\n");
            bool first = true;
            foreach (var kv in env)
            {
                if (!first) sb.Append(",\n");
                sb.Append("    ").Append(YorimashiEnvelope.EncodeString(kv.Key)).Append(": ")
                  .Append(YorimashiEnvelope.EncodeString(kv.Value));
                first = false;
            }
            sb.Append("\n  },\n");
            sb.Append("  \"results\": [\n");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (i > 0) sb.Append(",\n");
                sb.Append("    {\n");
                sb.Append("      \"step\": ").Append(r.Index).Append(",\n");
                sb.Append("      \"tool\": ").Append(YorimashiEnvelope.EncodeString(r.Tool)).Append(",\n");
                sb.Append("      \"note\": ").Append(YorimashiEnvelope.EncodeString(r.Note ?? "")).Append(",\n");
                sb.Append("      \"params_sent\": ").Append(r.ParamsJson ?? "null").Append(",\n");
                sb.Append("      \"ok\": ").Append(r.Ok ? "true" : "false").Append(",\n");
                sb.Append("      \"error\": ").Append(r.Error == null ? "null" : YorimashiEnvelope.EncodeString(r.Error)).Append(",\n");
                sb.Append("      \"result\": ").Append(string.IsNullOrEmpty(r.RawResult) ? "null" : r.RawResult).Append("\n");
                sb.Append("    }");
            }
            sb.Append("\n  ]\n");
            sb.Append("}\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void WriteTxtReport(string path, List<StepResult> results,
            Dictionary<string, string> env, string timestamp)
        {
            var sb = new StringBuilder(2048);
            sb.AppendLine("======================================================================");
            sb.AppendLine(" Yorimashi M3-T2 Self Test Report");
            sb.AppendLine("======================================================================");
            sb.AppendLine($" 时间: {env["generatedAt"]}");
            sb.AppendLine($" Unity: {env["unityVersion"]}");
            sb.AppendLine($" Platform: {env["platform"]}");
            sb.AppendLine($" 项目: {env["projectName"]}  路径: {env["projectPath"]}");
            sb.AppendLine($" 系统: {env["systemOS"]}");
            sb.AppendLine();
            int pass = 0, fail = 0;
            foreach (var r in results) { if (r.Ok) pass++; else fail++; }
            sb.AppendLine($" 结果: {pass} PASS / {fail} FAIL / {results.Count} total");
            sb.AppendLine();
            foreach (var r in results)
            {
                sb.AppendLine("----------------------------------------------------------------------");
                sb.AppendLine($" Step {r.Index}: {r.Tool}  → {(r.Ok ? "✅ PASS" : "❌ FAIL")}");
                sb.AppendLine($"   note: {r.Note}");
                sb.AppendLine($"   params: {r.ParamsJson ?? "null"}");
                if (!r.Ok && !string.IsNullOrEmpty(r.Error))
                {
                    sb.AppendLine($"   error: {r.Error}");
                }
                if (!string.IsNullOrEmpty(r.RawResult))
                {
                    var preview = r.RawResult.Length > 500 ? r.RawResult.Substring(0, 500) + "...[truncated]" : r.RawResult;
                    sb.AppendLine($"   result preview: {preview}");
                }
            }
            sb.AppendLine();
            sb.AppendLine("======================================================================");
            sb.AppendLine(" 完整原始数据在 raw_*.json 里。把这两个文件（或旁边的 zip）");
            sb.AppendLine(" 发给依代作者。");
            sb.AppendLine("======================================================================");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void WriteZip(string zipPath, string rawPath, string txtPath)
        {
            // ZIP 已存在就删（避免 append 失败）
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(rawPath, Path.GetFileName(rawPath));
                zip.CreateEntryFromFile(txtPath, Path.GetFileName(txtPath));
            }
        }
    }
}
