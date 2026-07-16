// Yorimashi Animator write tool 共通基类 — M3-E-P5
//
// 所有 animator/add_* tool 的父类, 处理:
//   1. controllerPath 参数解析 + 加载 AnimatorController asset
//   2. Asset mutation guard: 白名单沙盒目录下的 .controller 才允许改
//   3. 备份 .controller 文件到 Library/yorimashi_oplog/backups/animator_<ts>/
//
// 子类只需实现:
//   - MutatePreviewCore(ctrl, parsed, out changes) — 计算 changes 但不改
//   - MutateApplyCore(ctrl, parsed) — 真改, 返回 applied dict
//
// Sentinel: "M3-E-P5 ANIMATOR WRITE BASE"
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal abstract class AnimatorWriteToolBase : YorimashiWriteToolBase
    {
        // Animator 改的是 asset (磁盘 .controller 文件)
        protected override bool MutatesAsset => true;

        // 白名单前缀 (asset path 判定, 大小写不敏感)
        private static readonly string[] AllowedControllerPrefixes = {
            "Assets/Ramune_test/",
            "Assets/WriteTest/",
            "Assets/Yorimashi_WriteTest/",
        };

        protected AnimatorController _resolvedCtrl;
        protected string _resolvedCtrlPath;

        // Animator write tool 不用 path 参数, 用 controllerPath, 所以关掉基类 path scope guard
        protected override bool AllowWriteOnTarget(Dictionary<string, object> parsed, out string reason)
        {
            reason = null;
            return true;
        }

        /// <summary>
        /// M3-C 硬红线: controllerPath 必须在沙盒白名单下才允许改。
        /// </summary>
        protected override bool AllowAssetMutation(Dictionary<string, object> parsed, out string reason)
        {
            string ctrlPath = null;
            if (parsed != null && parsed.TryGetValue("controllerPath", out var cpObj) && cpObj is string cp)
                ctrlPath = cp;

            if (string.IsNullOrEmpty(ctrlPath))
            {
                reason = "missing required 'controllerPath' (asset path to .controller file)";
                return false;
            }

            foreach (var prefix in AllowedControllerPrefixes)
            {
                if (ctrlPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    reason = null;
                    return true;
                }
            }

            reason = "controllerPath '" + ctrlPath + "' not under a whitelisted sandbox directory. "
                   + "Allowed: " + string.Join(", ", AllowedControllerPrefixes)
                   + ". Refusing to modify AnimatorController assets outside the sandbox.";
            return false;
        }

        /// <summary>
        /// 解析 controllerPath 并加载 asset (子类 BuildPreview 里必须先调这个)。
        /// </summary>
        protected void ResolveController(Dictionary<string, object> parsed)
        {
            if (!parsed.TryGetValue("controllerPath", out var cpObj) || !(cpObj is string cp) || string.IsNullOrEmpty(cp))
                throw new ArgumentException("missing required 'controllerPath'");

            _resolvedCtrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(cp);
            if (_resolvedCtrl == null)
                throw new InvalidOperationException("AnimatorController not found at '" + cp + "'");

            _resolvedCtrlPath = cp;
        }

        /// <summary>
        /// 备份 .controller 文件到 Library/yorimashi_oplog/backups/animator_<ts>/。
        /// 返回备份目录路径 (放进 WriteToolChanges.BackupPath)。
        /// </summary>
        protected string BackupController()
        {
            if (string.IsNullOrEmpty(_resolvedCtrlPath))
                return null;

            var projRoot = Directory.GetParent(Application.dataPath).FullName;
            var backupDir = Path.Combine(projRoot, "Library", "yorimashi_oplog", "backups",
                "animator_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
            Directory.CreateDirectory(backupDir);

            var srcAbs = Path.Combine(projRoot, _resolvedCtrlPath.Replace('/', Path.DirectorySeparatorChar));
            var dstAbs = Path.Combine(backupDir, Path.GetFileName(srcAbs));
            try
            {
                File.Copy(srcAbs, dstAbs, overwrite: true);
                // meta 也备一份
                if (File.Exists(srcAbs + ".meta"))
                    File.Copy(srcAbs + ".meta", dstAbs + ".meta", overwrite: true);
            }
            catch (Exception e)
            {
                UnityEngine.Debug.LogWarning("[Yorimashi Animator] backup failed: " + e.Message);
            }
            return backupDir;
        }

        /// <summary>
        /// 保存 + 刷 asset (改完 controller 必须调)。
        /// </summary>
        protected void SaveController()
        {
            EditorUtility.SetDirty(_resolvedCtrl);
            AssetDatabase.SaveAssetIfDirty(_resolvedCtrl);
        }
    }
}
