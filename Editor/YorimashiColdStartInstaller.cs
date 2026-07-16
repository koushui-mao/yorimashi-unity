// Yorimashi ColdStartInstaller — v0.5.0 冷启动更新应用器
//
// 用途: 检测 Library/YorimashiPendingUpdate/apply.flag, 存在则
//       解压 pending.zip 覆盖 Packages/com.yorimashi.modder/, 删 flag。
//
// 关键: [InitializeOnLoad] 保证在 Unity Editor 启动早期跑, 此时:
//       - 磁盘无锁 (Unity 尚未开始正常 asset import)
//       - Editor 主 UI 未渲染
//       - 我们可以放心覆盖 Packages/ 里的文件
//     Unity 会在我们完成后继续走正常的 asset scan + compile 流程,
//     检测到 .cs 变化 → 自动 domain reload → 新代码生效。
//     整个过程不需要 AssetDatabase.Refresh 手动调用 (Unity 会自己发现)。
//
// 关键 2: 静态构造器里跑的是**旧版**的这个类 (因为磁盘还没换),
//         这没关系 —— 我们只需要它把新文件写到磁盘, Unity 之后 domain reload
//         会加载新版, 老版内存对象会被 GC。
//
// 关键 3: 冷启动装机器**只删除 Yorimashi 自己的 package**, 不动其他 packages。
//         删除采用先重命名到 tmp 再删的模式, 减少残留风险。
//
// Sentinel: "M3-E COLD START INSTALLER v1"
using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    [InitializeOnLoad]
    internal static class YorimashiColdStartInstaller
    {
        public const string SentinelTag = "M3-E COLD START INSTALLER v1";

        private const string PkgName = "com.yorimashi.modder";

        static YorimashiColdStartInstaller()
        {
            try
            {
                RunIfPending();
            }
            catch (Exception e)
            {
                // 不能 throw, 否则整个 Unity 启动流程炸
                UnityEngine.Debug.LogError("[Yorimashi ColdStartInstaller] fatal: " + e);
            }
        }

        private static void RunIfPending()
        {
            var projRoot = Directory.GetParent(Application.dataPath).FullName;
            var pendingDir = Path.Combine(projRoot, "Library", "YorimashiPendingUpdate");
            var flagPath = Path.Combine(pendingDir, "apply.flag");
            var zipPath = Path.Combine(pendingDir, "pending.zip");

            if (!File.Exists(flagPath) || !File.Exists(zipPath))
                return; // 99% 的启动都走这条路 (无 pending)

            UnityEngine.Debug.Log("[Yorimashi ColdStartInstaller] detected pending update, applying...");

            // 读 flag: 第一行是版本, 第二行是 sha256
            string targetVersion = "";
            string expectedSha = "";
            try
            {
                var lines = File.ReadAllLines(flagPath);
                if (lines.Length > 0) targetVersion = lines[0].Trim();
                if (lines.Length > 1) expectedSha = lines[1].Trim();
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[Yorimashi ColdStartInstaller] cannot read flag: " + e.Message);
                SafeDelete(flagPath);
                return;
            }

            if (string.IsNullOrEmpty(targetVersion))
            {
                UnityEngine.Debug.LogError("[Yorimashi ColdStartInstaller] flag file empty, aborting");
                SafeDelete(flagPath);
                SafeDelete(zipPath);
                return;
            }

            // 校验 zip sha256 (防止损坏)
            byte[] zipBytes;
            try
            {
                zipBytes = File.ReadAllBytes(zipPath);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[Yorimashi ColdStartInstaller] cannot read zip: " + e.Message);
                SafeDelete(flagPath);
                return;
            }

            if (!string.IsNullOrEmpty(expectedSha))
            {
                var actualSha = Sha256Hex(zipBytes);
                if (!string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
                {
                    UnityEngine.Debug.LogError(
                        "[Yorimashi ColdStartInstaller] sha256 mismatch: expected " + expectedSha
                        + " got " + actualSha + ". Aborting apply and cleaning up.");
                    SafeDelete(flagPath);
                    SafeDelete(zipPath);
                    return;
                }
            }

            // 目标: Packages/com.yorimashi.modder/
            var pkgPath = Path.Combine(projRoot, "Packages", PkgName);

            // 备份策略: 把旧目录改名为 .old-<ts>, 装完删掉; 失败则可回滚
            var backupPath = pkgPath + ".old-" + DateTime.UtcNow.Ticks;
            if (Directory.Exists(pkgPath))
            {
                try
                {
                    Directory.Move(pkgPath, backupPath);
                }
                catch (Exception e)
                {
                    UnityEngine.Debug.LogError(
                        "[Yorimashi ColdStartInstaller] cannot rename existing package (in use?): " + e.Message
                        + ". Aborting apply; pending files retained for next boot.");
                    return; // 不删 flag, 下次冷启动再试
                }
            }

            // 解压 zip 到 Packages/com.yorimashi.modder/
            try
            {
                Directory.CreateDirectory(pkgPath);
                ExtractZip(zipPath, pkgPath);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogError("[Yorimashi ColdStartInstaller] extract failed: " + e.Message + ". Rolling back.");
                // 回滚: 删掉半装的新目录, 把备份还原
                try { if (Directory.Exists(pkgPath)) Directory.Delete(pkgPath, true); } catch { }
                try { if (Directory.Exists(backupPath)) Directory.Move(backupPath, pkgPath); } catch { }
                // 不删 flag, 下次再试
                return;
            }

            // 装成功: 删备份 + 删 flag + 删 zip
            try { if (Directory.Exists(backupPath)) Directory.Delete(backupPath, true); } catch { }
            SafeDelete(flagPath);
            SafeDelete(zipPath);

            UnityEngine.Debug.Log(
                "[Yorimashi ColdStartInstaller] applied v" + targetVersion
                + ". Unity will now recompile scripts (30 sec).");

            // 不需要手动调 AssetDatabase.Refresh —
            // Unity 冷启动后段会自己扫 Packages/, 发现 .cs 变化触发 domain reload。
            // 手动调反而可能触发 reimport 死锁 (v0.4.x 的教训)。
        }

        // -------- 辅助函数 --------

        private static void ExtractZip(string zipPath, string destDir)
        {
            using (var fs = File.OpenRead(zipPath))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    // entry.FullName 里可能有 / 分隔符 (unix zip), Windows 也能吃
                    var relPath = entry.FullName.Replace('/', Path.DirectorySeparatorChar);
                    if (string.IsNullOrEmpty(relPath)) continue;

                    // 防路径穿越 (zip slip attack)
                    var fullPath = Path.GetFullPath(Path.Combine(destDir, relPath));
                    if (!fullPath.StartsWith(Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("zip slip detected: " + entry.FullName);
                    }

                    // 目录 entry (FullName 以 / 结尾)
                    if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                    {
                        Directory.CreateDirectory(fullPath);
                        continue;
                    }

                    var parent = Path.GetDirectoryName(fullPath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    entry.ExtractToFile(fullPath, overwrite: true);
                }
            }
        }

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

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
