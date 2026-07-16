// Yorimashi UpdateChecker — v0.5.0 冷启动更新（背景下载 + 冷启动应用）
//
// 变更历史:
//   v0.3.x - v0.4.x: 运行时下载 + 覆盖 Packages/, Windows 上卡 15 min (Unity 文件锁)
//   v0.4.7 (放弃 VPM/ALCOM 路线, 因 vrchat.com 网络问题)
//   v0.5.0 (this): **冷启动更新** — 后台下载 zip 到 Library/, 写 flag,
//                  下次 Unity 启动时 YorimashiColdStartInstaller 在 [InitializeOnLoad]
//                  里应用 (Unity assembly 未加载, 磁盘无锁, 无死循环风险)
//
// 用户体验:
//   1. 你正常工作, 后台静默下载新版 → 存 Library/YorimashiPendingUpdate/pending.zip + apply.flag
//   2. ChatWindow 顶部灰色小提示: "已下载 vX — 下次开 Unity 生效"
//   3. 你什么时候关 Unity 都可以 (正常关, 不用杀进程)
//   4. 下次开 Unity, 20-30 秒解压完成, 新版就位, 无卡死
//
// 关键: 本类**只下载**, 不解压, 不覆盖 Packages/, 不调 AssetDatabase.Refresh
//       所有磁盘写入只发生在 Library/YorimashiPendingUpdate/ (Unity 不扫)
//
// Sentinel: "M3-E UPDATE CHECKER v3 (cold-start)"
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Yorimashi.Modder.Editor
{
    public static class UpdateChecker
    {
        public const string SentinelTag = "M3-E UPDATE CHECKER v3 (cold-start)";
        private const string LatestJsonUrl = "https://yorimashi.koushui.online/dist/latest.json";

        // SessionState 跨 domain reload 保持 (Unity 关闭后清空 - 这正是我们想要的)
        private const string SessionKey_LatestVersion = "Yorimashi.UpdateChecker.LatestVersion";
        private const string SessionKey_LatestUrl     = "Yorimashi.UpdateChecker.LatestUrl";
        private const string SessionKey_LatestSha     = "Yorimashi.UpdateChecker.LatestSha";
        private const string SessionKey_LatestNotes   = "Yorimashi.UpdateChecker.LatestNotes";
        private const string SessionKey_Checked       = "Yorimashi.UpdateChecker.CheckedThisSession";
        private const string SessionKey_Error         = "Yorimashi.UpdateChecker.LastError";
        private const string SessionKey_Downloading   = "Yorimashi.UpdateChecker.Downloading";
        private const string SessionKey_Pending       = "Yorimashi.UpdateChecker.PendingVersion";

        private static UnityWebRequest _fetchReq;
        private static UnityWebRequest _downloadReq;

        static UpdateChecker()
        {
            // Editor 加载完后跑一次检查
            EditorApplication.delayCall += TriggerBackgroundCheck;
        }

        // -------- 公共状态 --------

        public static bool HasCheckedThisSession
            => SessionState.GetBool(SessionKey_Checked, false);

        public static string LatestVersion
            => SessionState.GetString(SessionKey_LatestVersion, "");

        public static string LatestNotes
            => SessionState.GetString(SessionKey_LatestNotes, "");

        public static string LastError
            => SessionState.GetString(SessionKey_Error, "");

        public static bool IsDownloading
            => SessionState.GetBool(SessionKey_Downloading, false);

        /// <summary>已下载完成、等待冷启动应用的版本号 (空 = 无 pending)</summary>
        public static string PendingVersion
            => SessionState.GetString(SessionKey_Pending, "");

        public static bool HasUpdate
        {
            get
            {
                var latest = LatestVersion;
                if (string.IsNullOrEmpty(latest)) return false;
                var cur = ChatWindow.Version.Text;
                if (string.IsNullOrEmpty(cur)) return false;
                return CompareSemver(latest, cur) > 0;
            }
        }

        /// <summary>手动触发一次检查（ChatWindow 按钮调）</summary>
        public static void CheckNow()
        {
            SessionState.SetBool(SessionKey_Checked, false);
            SessionState.SetString(SessionKey_Error, "");
            TriggerBackgroundCheck();
        }

        /// <summary>手动触发下载 (ChatWindow "立即下载" 按钮调, 后台跑不阻塞)</summary>
        public static void StartDownload()
        {
            if (IsDownloading) return;
            if (!HasUpdate) return;
            var url = SessionState.GetString(SessionKey_LatestUrl, null);
            var sha = SessionState.GetString(SessionKey_LatestSha, null);
            var ver = LatestVersion;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha))
            {
                SessionState.SetString(SessionKey_Error, "cannot start download: missing url or sha");
                return;
            }

            SessionState.SetBool(SessionKey_Downloading, true);
            SessionState.SetString(SessionKey_Error, "");

            _downloadReq = UnityWebRequest.Get(url);
            _downloadReq.timeout = 60;
            var op = _downloadReq.SendWebRequest();
            op.completed += _ => OnDownloadDone(ver, sha);
        }

        // -------- 后台版本检查 --------

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
                var tgzUrl = ExtractJsonString(json, "url") ?? "";
                var sha = ExtractJsonString(json, "sha256") ?? "";
                var notes = ExtractJsonString(json, "notes") ?? "";

                // latest.json 里是 tgz URL, 转成 zip URL (v0.5.0 冷启动装机器只支持 zip)
                var zipUrl = tgzUrl;
                var zipSha = sha;
                if (tgzUrl.EndsWith(".tgz"))
                {
                    zipUrl = tgzUrl.Substring(0, tgzUrl.Length - 4) + ".zip";
                    // zip 的 sha 跟 tgz 不同, 需要另外拉一次以获取正确 sha
                    // 我们从 vpm.json 里拿 zip 的 sha (release.sh 已保证同步)
                    FetchZipShaFromVpm(ver);
                }
                else
                {
                    // latest.json 已经给的是 zip URL (未来路径), 直接用
                    SessionState.SetString(SessionKey_LatestVersion, ver);
                    SessionState.SetString(SessionKey_LatestUrl, zipUrl);
                    SessionState.SetString(SessionKey_LatestSha, zipSha);
                    SessionState.SetString(SessionKey_LatestNotes, notes);
                    SessionState.SetBool(SessionKey_Checked, true);
                    SessionState.SetString(SessionKey_Error, "");
                }
                // 先临时存, 若 FetchZipShaFromVpm 成功会覆盖
                SessionState.SetString(SessionKey_LatestVersion, ver);
                SessionState.SetString(SessionKey_LatestUrl, zipUrl);
                SessionState.SetString(SessionKey_LatestNotes, notes);
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

        // 从 vpm.json 拿 zip 的 sha256 (release.sh 保证两个 json 版本同步)
        private static void FetchZipShaFromVpm(string version)
        {
            try
            {
                var req = UnityWebRequest.Get("https://yorimashi.koushui.online/vpm.json");
                req.timeout = 10;
                var op = req.SendWebRequest();
                op.completed += _ =>
                {
                    try
                    {
                        if (req.result == UnityWebRequest.Result.Success)
                        {
                            var body = req.downloadHandler.text;
                            // 简单定位到 versions[<version>] 段, 取该段里第一个 zipSHA256
                            var marker = "\"" + version + "\"";
                            int i = body.IndexOf(marker, StringComparison.Ordinal);
                            if (i >= 0)
                            {
                                var zipSha = ExtractJsonStringAfter(body, "zipSHA256", i);
                                if (!string.IsNullOrEmpty(zipSha))
                                {
                                    SessionState.SetString(SessionKey_LatestSha, zipSha);
                                }
                            }
                        }
                    }
                    finally
                    {
                        req.Dispose();
                        SessionState.SetBool(SessionKey_Checked, true);
                    }
                };
            }
            catch
            {
                SessionState.SetBool(SessionKey_Checked, true);
            }
        }

        // -------- 后台下载 --------

        private static void OnDownloadDone(string version, string expectedSha)
        {
            SessionState.SetBool(SessionKey_Downloading, false);
            try
            {
                if (_downloadReq == null) return;
                if (_downloadReq.result != UnityWebRequest.Result.Success)
                {
                    SessionState.SetString(SessionKey_Error, "download failed: " + _downloadReq.error);
                    return;
                }
                var bytes = _downloadReq.downloadHandler.data;
                var actualSha = Sha256Hex(bytes);
                if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    SessionState.SetString(SessionKey_Error,
                        "sha256 mismatch: expected " + expectedSha + " got " + actualSha);
                    return;
                }

                // 写到 Library/YorimashiPendingUpdate/ — Unity 不扫这个目录
                var projRoot = Directory.GetParent(Application.dataPath).FullName;
                var pendingDir = Path.Combine(projRoot, "Library", "YorimashiPendingUpdate");
                Directory.CreateDirectory(pendingDir);

                // 清掉旧的 pending (若存在)
                foreach (var f in Directory.GetFiles(pendingDir))
                {
                    try { File.Delete(f); } catch { /* 忽略 */ }
                }

                var zipPath = Path.Combine(pendingDir, "pending.zip");
                File.WriteAllBytes(zipPath, bytes);

                // apply.flag 内容 = 目标版本号 (供 ColdStartInstaller 读)
                var flagPath = Path.Combine(pendingDir, "apply.flag");
                File.WriteAllText(flagPath, version + "\n" + expectedSha);

                SessionState.SetString(SessionKey_Pending, version);
                SessionState.SetString(SessionKey_Error, "");
            }
            catch (Exception e)
            {
                SessionState.SetString(SessionKey_Error, "download apply failed: " + e.Message);
            }
            finally
            {
                if (_downloadReq != null) { _downloadReq.Dispose(); _downloadReq = null; }
            }
        }

        // -------- Utilities --------

        internal static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                var sb = new StringBuilder(64);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string ExtractJsonString(string json, string key)
            => ExtractJsonStringAfter(json, key, 0);

        private static string ExtractJsonStringAfter(string json, string key, int startIdx)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, startIdx, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return null;
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
            if (i >= json.Length || json[i] != '"') return null;
            int start = i + 1;
            int end = json.IndexOf('"', start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        internal static int CompareSemver(string a, string b)
        {
            if (a == b) return 0;
            if (string.IsNullOrEmpty(a)) return -1;
            if (string.IsNullOrEmpty(b)) return 1;

            // 主版本 X.Y.Z 逐段整数比
            var aMain = a.Split('-')[0].Split('+')[0];
            var bMain = b.Split('-')[0].Split('+')[0];
            var aParts = aMain.Split('.');
            var bParts = bMain.Split('.');
            for (int i = 0; i < Math.Max(aParts.Length, bParts.Length); i++)
            {
                int an = i < aParts.Length && int.TryParse(aParts[i], out var av) ? av : 0;
                int bn = i < bParts.Length && int.TryParse(bParts[i], out var bv) ? bv : 0;
                if (an != bn) return an.CompareTo(bn);
            }

            // 主版本相同: pre-release 段比较
            bool aPre = a.Contains("-");
            bool bPre = b.Contains("-");
            if (aPre && !bPre) return -1;
            if (!aPre && bPre) return 1;
            if (!aPre && !bPre) return 0;

            // 双方都有 pre-release: 按 SemVer 2.0.0 规则逐段比
            // 每段用 '.' 分隔; 纯数字段按整数比 (m3e.10 > m3e.9), 其他按 Ordinal 字符串比
            var aPreStr = a.Substring(a.IndexOf('-') + 1);
            var bPreStr = b.Substring(b.IndexOf('-') + 1);
            var aPreParts = aPreStr.Split('.');
            var bPreParts = bPreStr.Split('.');
            for (int i = 0; i < Math.Min(aPreParts.Length, bPreParts.Length); i++)
            {
                var ap = aPreParts[i];
                var bp = bPreParts[i];
                bool aIsNum = int.TryParse(ap, out var aNum);
                bool bIsNum = int.TryParse(bp, out var bNum);
                if (aIsNum && bIsNum)
                {
                    if (aNum != bNum) return aNum.CompareTo(bNum);
                }
                else if (aIsNum) return -1; // 数字段 < 字符串段 (SemVer 规则)
                else if (bIsNum) return 1;
                else
                {
                    int cmp = string.Compare(ap, bp, StringComparison.Ordinal);
                    if (cmp != 0) return cmp;
                }
            }
            // 前缀相同, 段数多的更大 (e.g. "m3e.1.2" > "m3e.1")
            return aPreParts.Length.CompareTo(bPreParts.Length);
        }
    }
}
