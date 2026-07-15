// Yorimashi UpdateChecker — M3-D-v2 (2026-07-15 重构)
// 客户零配置 OTA：直连 HTTPS 拉 tarball，绕开 UPM registry / manifest.json / scopedRegistry。
//
// 设计：
//   1. [InitializeOnLoad] Editor 启动时触发一次（SessionState 保证不重复）
//   2. GET https://yorimashi.koushui.online/dist/latest.json → 拿 version + tarball URL + sha256
//   3. 与 ChatWindow.Version.Text 比较 semver，有更新落 SessionState
//   4. ChatWindow.OnGUI 渲染红条 "有新版本"，用户点"一键更新"
//   5. InstallLatest:
//      a. GET tarball 到临时文件
//      b. 校验 sha256
//      c. 解压到 <Project>/Packages/com.yorimashi.modder/ （原地替换）
//      d. AssetDatabase.Refresh() → Unity 自动 domain reload
//   6. 无网/端点挂了 → 静默 Warn，不打扰用户
//
// 好处：
//   - 客户不配 scoped registry，不动 manifest.json，装完 zip 就有全自动 OTA
//   - 独立于 verdaccio，服务端只需 nginx/caddy 静态托管 tarball + latest.json
//   - sha256 校验防中间人 + 部分下载 corruption

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Yorimashi.Modder.Editor
{
    [InitializeOnLoad]
    public static class UpdateChecker
    {
        // 分发端点。改主机时改这里。
        private const string LatestJsonUrl = "https://yorimashi.koushui.online/dist/latest.json";

        // SessionState 键：Editor 运行期共享，domain reload 保留，Unity 退出才清
        private const string SessionKey_LatestVersion = "Yorimashi.UpdateChecker.LatestVersion";
        private const string SessionKey_LatestUrl     = "Yorimashi.UpdateChecker.LatestUrl";
        private const string SessionKey_LatestSha     = "Yorimashi.UpdateChecker.LatestSha";
        private const string SessionKey_LatestNotes   = "Yorimashi.UpdateChecker.LatestNotes";
        private const string SessionKey_Checked       = "Yorimashi.UpdateChecker.CheckedThisSession";
        private const string SessionKey_Error         = "Yorimashi.UpdateChecker.LastError";
        private const string SessionKey_Installing    = "Yorimashi.UpdateChecker.Installing";
        private const string SessionKey_InstallStage  = "Yorimashi.UpdateChecker.InstallStage";

        private static UnityWebRequest _fetchReq;
        private static UnityWebRequest _downloadReq;

        static UpdateChecker()
        {
            // 只在启动时查一次。domain reload 之后 SessionState 保留，不重复。
            if (!SessionState.GetBool(SessionKey_Checked, false))
            {
                EditorApplication.delayCall += TriggerBackgroundCheck;
            }
        }

        // -------- Public API (被 ChatWindow 调用) --------

        public static bool HasCheckedThisSession
            => SessionState.GetBool(SessionKey_Checked, false);

        public static string LatestVersion
            => SessionState.GetString(SessionKey_LatestVersion, null);

        public static string LatestNotes
            => SessionState.GetString(SessionKey_LatestNotes, null);

        public static string LastError
            => SessionState.GetString(SessionKey_Error, null);

        public static bool HasUpdate
        {
            get
            {
                var latest = LatestVersion;
                if (string.IsNullOrEmpty(latest)) return false;
                return CompareSemver(ChatWindow.Version.Text, latest) < 0;
            }
        }

        public static bool IsInstalling
            => SessionState.GetBool(SessionKey_Installing, false);

        public static string InstallStage
            => SessionState.GetString(SessionKey_InstallStage, "");

        /// <summary>手动重新检查（ChatWindow 按钮触发）。</summary>
        public static void CheckNow()
        {
            SessionState.EraseString(SessionKey_LatestVersion);
            SessionState.EraseString(SessionKey_Error);
            SessionState.SetBool(SessionKey_Checked, false);
            TriggerBackgroundCheck();
        }

        /// <summary>下载 tarball + 校验 sha256 + 解压到 Packages/。</summary>
        public static void InstallLatest()
        {
            var url = SessionState.GetString(SessionKey_LatestUrl, null);
            var sha = SessionState.GetString(SessionKey_LatestSha, null);
            var latest = LatestVersion;
            if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(latest))
            {
                Debug.LogWarning("[Yorimashi] InstallLatest called with no update cached; run CheckNow first.");
                return;
            }
            if (IsInstalling)
            {
                Debug.Log("[Yorimashi] Install already in progress.");
                return;
            }
            SessionState.SetBool(SessionKey_Installing, true);
            SessionState.SetString(SessionKey_InstallStage, "downloading");
            Debug.Log($"[Yorimashi] Installing {latest} from {url} ...");
            _downloadReq = UnityWebRequest.Get(url);
            _downloadReq.SendWebRequest();
            EditorApplication.update += DownloadProgress;
        }

        // -------- Internal check flow --------

        private static void TriggerBackgroundCheck()
        {
            EditorApplication.delayCall -= TriggerBackgroundCheck;
            SessionState.SetBool(SessionKey_Checked, true);
            try
            {
                _fetchReq = UnityWebRequest.Get(LatestJsonUrl);
                _fetchReq.timeout = 10;
                _fetchReq.SendWebRequest();
                EditorApplication.update += FetchProgress;
            }
            catch (Exception ex)
            {
                SessionState.SetString(SessionKey_Error, "fetch threw: " + ex.Message);
                Debug.LogWarning("[Yorimashi] UpdateChecker fetch: " + ex.Message);
            }
        }

        private static void FetchProgress()
        {
            if (_fetchReq == null || !_fetchReq.isDone) return;
            EditorApplication.update -= FetchProgress;

            try
            {
                if (_fetchReq.result != UnityWebRequest.Result.Success)
                {
                    var err = _fetchReq.error ?? "unknown";
                    SessionState.SetString(SessionKey_Error, err);
                    Debug.LogWarning($"[Yorimashi] Update check failed ({LatestJsonUrl}): {err}");
                    return;
                }

                var body = _fetchReq.downloadHandler.text;
                // 极简 JSON 提取，避免依赖 Newtonsoft
                var version = ExtractJsonString(body, "version");
                var url = ExtractJsonString(body, "url");
                var sha = ExtractJsonString(body, "sha256");
                var notes = ExtractJsonString(body, "notes");

                if (string.IsNullOrEmpty(version) || string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sha))
                {
                    SessionState.SetString(SessionKey_Error, "latest.json missing fields");
                    Debug.LogWarning("[Yorimashi] Update check: latest.json malformed, body=" + body);
                    return;
                }

                SessionState.SetString(SessionKey_LatestVersion, version);
                SessionState.SetString(SessionKey_LatestUrl, url);
                SessionState.SetString(SessionKey_LatestSha, sha);
                SessionState.SetString(SessionKey_LatestNotes, notes ?? "");
                SessionState.EraseString(SessionKey_Error);

                var current = ChatWindow.Version.Text;
                var cmp = CompareSemver(current, version);
                Debug.Log($"[Yorimashi] Update check: current={current}  latest={version}  hasUpdate={cmp < 0}");
            }
            finally
            {
                _fetchReq?.Dispose();
                _fetchReq = null;
            }
        }

        private static void DownloadProgress()
        {
            if (_downloadReq == null || !_downloadReq.isDone) return;
            EditorApplication.update -= DownloadProgress;

            try
            {
                if (_downloadReq.result != UnityWebRequest.Result.Success)
                {
                    var err = _downloadReq.error ?? "unknown";
                    Debug.LogError("[Yorimashi] Download failed: " + err);
                    SessionState.SetString(SessionKey_Error, "download failed: " + err);
                    SessionState.SetBool(SessionKey_Installing, false);
                    SessionState.SetString(SessionKey_InstallStage, "");
                    return;
                }

                var expectedSha = SessionState.GetString(SessionKey_LatestSha, null);
                var bytes = _downloadReq.downloadHandler.data;
                var actualSha = Sha256Hex(bytes);
                if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogError($"[Yorimashi] SHA256 mismatch! expected={expectedSha} actual={actualSha}");
                    SessionState.SetString(SessionKey_Error, "sha256 mismatch");
                    SessionState.SetBool(SessionKey_Installing, false);
                    SessionState.SetString(SessionKey_InstallStage, "");
                    return;
                }

                SessionState.SetString(SessionKey_InstallStage, "extracting");
                Debug.Log($"[Yorimashi] Downloaded {bytes.Length} bytes, sha256 OK. Extracting...");

                var projectPath = Path.GetDirectoryName(Application.dataPath); // <Project>/
                var packagesDir = Path.Combine(projectPath, "Packages");
                var targetDir = Path.Combine(packagesDir, "com.yorimashi.modder");
                var tempTgz = Path.Combine(Path.GetTempPath(), $"yorimashi_{Guid.NewGuid():N}.tgz");
                File.WriteAllBytes(tempTgz, bytes);

                // 备份旧包（保护未提交改动）
                if (Directory.Exists(targetDir))
                {
                    var backup = targetDir + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    Debug.Log($"[Yorimashi] Backing up existing package to {backup}");
                    Directory.Move(targetDir, backup);
                }

                Directory.CreateDirectory(targetDir);
                // tar 根 = 包内容 (./package.json 等)，解到 targetDir 里
                TarGzExtract(tempTgz, targetDir);
                try { File.Delete(tempTgz); } catch { }

                SessionState.SetString(SessionKey_InstallStage, "reloading");
                Debug.Log("[Yorimashi] Extraction complete. Refreshing AssetDatabase → domain reload will follow.");
                AssetDatabase.Refresh();

                SessionState.SetBool(SessionKey_Installing, false);
                SessionState.SetString(SessionKey_InstallStage, "");
            }
            catch (Exception ex)
            {
                Debug.LogError("[Yorimashi] Install failed: " + ex);
                SessionState.SetString(SessionKey_Error, "install failed: " + ex.Message);
                SessionState.SetBool(SessionKey_Installing, false);
                SessionState.SetString(SessionKey_InstallStage, "");
            }
            finally
            {
                _downloadReq?.Dispose();
                _downloadReq = null;
            }
        }

        // -------- tar.gz 解压（纯 .NET，不依赖外部库） --------

        private static void TarGzExtract(string tgzPath, string destDir)
        {
            using (var fs = File.OpenRead(tgzPath))
            using (var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionMode.Decompress))
            using (var ms = new MemoryStream())
            {
                gz.CopyTo(ms);
                ms.Position = 0;
                TarExtract(ms, destDir);
            }
        }

        /// <summary>极简 POSIX ustar 解压（够 UPM 包用；不支持 long name / sparse）</summary>
        private static void TarExtract(Stream tar, string destDir)
        {
            var header = new byte[512];
            while (true)
            {
                int read = ReadFull(tar, header, 0, 512);
                if (read < 512) break;
                // 全 0 = 结束
                bool allZero = true;
                for (int i = 0; i < 512; i++) { if (header[i] != 0) { allZero = false; break; } }
                if (allZero) break;

                string name = Encoding.ASCII.GetString(header, 0, 100).TrimEnd('\0');
                string prefix = Encoding.ASCII.GetString(header, 345, 155).TrimEnd('\0');
                if (!string.IsNullOrEmpty(prefix)) name = prefix + "/" + name;
                string sizeOctal = Encoding.ASCII.GetString(header, 124, 12).Trim('\0', ' ');
                long size = 0;
                if (!string.IsNullOrEmpty(sizeOctal))
                {
                    try { size = Convert.ToInt64(sizeOctal, 8); } catch { size = 0; }
                }
                char typeflag = (char)header[156];
                // '0' or '\0' = regular file, '5' = dir, 'L' = long name (GNU, we skip), 'x'/'g' = pax (skip payload)

                if (typeflag == '5' || (name.EndsWith("/") && size == 0))
                {
                    var dp = Path.Combine(destDir, name);
                    Directory.CreateDirectory(dp);
                }
                else if (typeflag == '0' || typeflag == '\0')
                {
                    var fp = Path.Combine(destDir, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(fp));
                    using (var outFs = File.Create(fp))
                    {
                        var buf = new byte[8192];
                        long remaining = size;
                        while (remaining > 0)
                        {
                            int want = (int)Math.Min(buf.Length, remaining);
                            int got = tar.Read(buf, 0, want);
                            if (got <= 0) break;
                            outFs.Write(buf, 0, got);
                            remaining -= got;
                        }
                    }
                }
                else
                {
                    // 未支持类型 → 跳过 payload
                    SkipBytes(tar, size);
                }
                // padding 到 512 边界
                long pad = (512 - (size % 512)) % 512;
                if (pad > 0) SkipBytes(tar, pad);
            }
        }

        private static int ReadFull(Stream s, byte[] buf, int offset, int count)
        {
            int total = 0;
            while (total < count)
            {
                int got = s.Read(buf, offset + total, count - total);
                if (got <= 0) break;
                total += got;
            }
            return total;
        }

        private static void SkipBytes(Stream s, long n)
        {
            var buf = new byte[Math.Min(8192L, n)];
            long left = n;
            while (left > 0)
            {
                int want = (int)Math.Min(buf.Length, left);
                int got = s.Read(buf, 0, want);
                if (got <= 0) break;
                left -= got;
            }
        }

        // -------- Utilities --------

        private static string Sha256Hex(byte[] data)
        {
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(data);
                var sb = new StringBuilder(64);
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        /// <summary>极简 JSON 字符串字段提取: "key": "value"  不支持 escape 内嵌引号。</summary>
        private static string ExtractJsonString(string json, string key)
        {
            if (string.IsNullOrEmpty(json)) return null;
            var needle = "\"" + key + "\"";
            int i = json.IndexOf(needle, StringComparison.Ordinal);
            if (i < 0) return null;
            i = json.IndexOf(':', i + needle.Length);
            if (i < 0) return null;
            // 跳空白
            i++;
            while (i < json.Length && (json[i] == ' ' || json[i] == '\t' || json[i] == '\r' || json[i] == '\n')) i++;
            if (i >= json.Length || json[i] != '"') return null;
            int start = i + 1;
            int end = json.IndexOf('"', start);
            if (end < 0) return null;
            return json.Substring(start, end - start);
        }

        // -------- Semver comparison --------
        // 支持 X.Y.Z 和 X.Y.Z-pre.N（够我们用）
        internal static int CompareSemver(string a, string b)
        {
            if (a == b) return 0;
            var aMain = a.Split('-')[0].Split('+')[0];
            var bMain = b.Split('-')[0].Split('+')[0];
            var aParts = aMain.Split('.');
            var bParts = bMain.Split('.');
            for (int i = 0; i < Math.Max(aParts.Length, bParts.Length); i++)
            {
                int ai = i < aParts.Length ? SafeInt(aParts[i]) : 0;
                int bi = i < bParts.Length ? SafeInt(bParts[i]) : 0;
                if (ai != bi) return ai < bi ? -1 : 1;
            }
            var aPre = a.Contains("-") ? a.Substring(a.IndexOf('-') + 1) : null;
            var bPre = b.Contains("-") ? b.Substring(b.IndexOf('-') + 1) : null;
            if (aPre == null && bPre == null) return 0;
            if (aPre == null) return 1;
            if (bPre == null) return -1;
            return string.Compare(aPre, bPre, StringComparison.Ordinal);
        }

        private static int SafeInt(string s)
        {
            return int.TryParse(s, out var v) ? v : 0;
        }
    }
}
