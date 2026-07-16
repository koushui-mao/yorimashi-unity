// Yorimashi UpdateChecker — v0.5.0 起简化为纯版本检查
//
// 变更历史:
//   v0.3.x - v0.4.x: 运行时下载 tgz + 解压覆盖 Packages/, 触发 AssetDatabase.Refresh
//     → 严重 bug: Windows 上 Unity 会卡在 "Importing assets" 死循环 (2026-07-16 实测 15 分钟)
//     根因: Unity 运行时热替换自己 embedded package 的 .cs, 触发文件锁 + reimport 死循环
//   v0.5.0 起: **完全放弃运行时 OTA**, 改走 VPM 生态 (ALCOM / VCC)
//     - Editor 启动时拉一次 latest.json 比对版本
//     - 有新版 → ChatWindow 顶部橙色红条 + 弹窗告知"请在 ALCOM 里更新"
//     - 从此不再修改 Packages/ 里的任何文件
//     - ALCOM 作为外部进程管理 Packages/ 目录, Unity 走正常 domain reload, 零卡死
//
// Sentinel: "M3-E UPDATE CHECKER v2 (VPM)"
using System;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Yorimashi.Modder.Editor
{
    public static class UpdateChecker
    {
        public const string SentinelTag = "M3-E UPDATE CHECKER v2 (VPM)";
        private const string LatestJsonUrl = "https://yorimashi.koushui.online/dist/latest.json";
        private const string VpmRepoUrl    = "https://yorimashi.koushui.online/vpm.json";

        // SessionState 跨 domain reload 保持
        private const string SessionKey_LatestVersion = "Yorimashi.UpdateChecker.LatestVersion";
        private const string SessionKey_LatestNotes   = "Yorimashi.UpdateChecker.LatestNotes";
        private const string SessionKey_Checked       = "Yorimashi.UpdateChecker.CheckedThisSession";
        private const string SessionKey_Error         = "Yorimashi.UpdateChecker.LastError";

        private static UnityWebRequest _fetchReq;

        static UpdateChecker()
        {
            EditorApplication.delayCall += TriggerBackgroundCheck;
        }

        public static bool HasCheckedThisSession
            => SessionState.GetBool(SessionKey_Checked, false);

        public static string LatestVersion
            => SessionState.GetString(SessionKey_LatestVersion, "");

        public static string LatestNotes
            => SessionState.GetString(SessionKey_LatestNotes, "");

        public static string LastError
            => SessionState.GetString(SessionKey_Error, "");

        public static bool HasUpdate
        {
            get
            {
                var latest = LatestVersion;
                if (string.IsNullOrEmpty(latest)) return false;
                var cur = ChatWindow.Version.Text;
                if (string.IsNullOrEmpty(cur)) return false;
                return string.Compare(latest, cur, StringComparison.Ordinal) > 0;
            }
        }

        /// <summary>
        /// 手动触发一次检查（ChatWindow 按钮调）。
        /// </summary>
        public static void CheckNow()
        {
            SessionState.SetBool(SessionKey_Checked, false);
            SessionState.SetString(SessionKey_Error, "");
            TriggerBackgroundCheck();
        }

        /// <summary>
        /// 提示用户去 ALCOM 更新。**不再自动下载/解压**（v0.4.x 的自研 OTA 会导致 Unity 卡死）。
        /// </summary>
        public static void ShowUpdateInstructions()
        {
            var latest = LatestVersion;
            var cur = ChatWindow.Version.Text;
            var msg = new StringBuilder();
            msg.AppendLine($"发现新版本 v{latest}（当前 v{cur}）");
            msg.AppendLine();
            msg.AppendLine("更新方式（在 ALCOM / VCC 中）:");
            msg.AppendLine();
            msg.AppendLine("1. 打开 ALCOM");
            msg.AppendLine("2. 选择当前项目 → Manage Project");
            msg.AppendLine("3. 找到 'Yorimashi Modder' 一行, 点 Update");
            msg.AppendLine("4. ALCOM 会自动替换 Packages/, Unity 端 domain reload 几秒钟即可");
            msg.AppendLine();
            msg.AppendLine("如果 ALCOM 里看不到 Yorimashi Modder:");
            msg.AppendLine("- Settings → Packages → Add Repository");
            msg.AppendLine($"- 粘贴: {VpmRepoUrl}");
            msg.AppendLine();
            msg.AppendLine("**注意**: 请勿手动删除或替换 Packages/com.yorimashi.modder/ 目录。");
            msg.AppendLine("ALCOM 会正确处理所有文件替换和 Unity 的编译流程。");

            EditorUtility.DisplayDialog("Yorimashi Modder 有更新", msg.ToString(), "知道了");
        }

        private static void TriggerBackgroundCheck()
        {
            if (SessionState.GetBool(SessionKey_Checked, false)) return;
            if (_fetchReq != null) return;

            try
            {
                _fetchReq = UnityWebRequest.Get(LatestJsonUrl);
                _fetchReq.timeout = 10;
                var op = _fetchReq.SendWebRequest();
                op.completed += _ => OnFetchDone();
            }
            catch (Exception e)
            {
                SessionState.SetString(SessionKey_Error, "check start failed: " + e.Message);
                SessionState.SetBool(SessionKey_Checked, true);
            }
        }

        private static void OnFetchDone()
        {
            try
            {
                if (_fetchReq == null) return;
                if (_fetchReq.result != UnityWebRequest.Result.Success)
                {
                    SessionState.SetString(SessionKey_Error, "check failed: " + _fetchReq.error);
                    SessionState.SetBool(SessionKey_Checked, true);
                    return;
                }
                var json = _fetchReq.downloadHandler.text;
                var ver = ExtractJsonString(json, "version") ?? "";
                var notes = ExtractJsonString(json, "notes") ?? "";
                SessionState.SetString(SessionKey_LatestVersion, ver);
                SessionState.SetString(SessionKey_LatestNotes, notes);
                SessionState.SetBool(SessionKey_Checked, true);
                SessionState.SetString(SessionKey_Error, "");
            }
            catch (Exception e)
            {
                SessionState.SetString(SessionKey_Error, "parse failed: " + e.Message);
                SessionState.SetBool(SessionKey_Checked, true);
            }
            finally
            {
                if (_fetchReq != null) { _fetchReq.Dispose(); _fetchReq = null; }
            }
        }

        // 极简 JSON 字符串字段提取（不引依赖）
        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(key)) return null;
            var needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i += needle.Length;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == ':')) i++;
            if (i >= json.Length || json[i] != '"') return null;
            i++;
            var sb = new StringBuilder();
            while (i < json.Length)
            {
                char c = json[i];
                if (c == '\\' && i + 1 < json.Length)
                {
                    char n = json[i + 1];
                    if (n == 'n') sb.Append('\n');
                    else if (n == 't') sb.Append('\t');
                    else if (n == 'r') sb.Append('\r');
                    else sb.Append(n);
                    i += 2;
                    continue;
                }
                if (c == '"') return sb.ToString();
                sb.Append(c);
                i++;
            }
            return null;
        }
    }
}
