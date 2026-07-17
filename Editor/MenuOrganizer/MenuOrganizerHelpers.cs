// Yorimashi MenuOrganizer — Tool 层共享 helpers (M4-B, Unity 层)
//
// 每个 menu_organizer/* tool 需要:
//   - 定位 avatar 上的 MenuOrganizerOutput 组件 (可能不存在, tool 视情况自建)
//   - 从 Output.menu asset 读 MenuTree 或创建新的
//   - 写盘 Output.menu + Output.trashRoot (加锁保护并发)
//   - Undo 记录
//
// Sentinel: "M4-B TOOL HELPERS"

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Yorimashi.Modder.Editor.MenuOrganizer
{
    public static class MenuOrganizerHelpers
    {
        public const string SentinelTag = "M4-B TOOL HELPERS";

        /// <summary>Output asset 目录 (Assets/Yorimashi/MenuOrganizer/&lt;AvatarName&gt;/)</summary>
        public static string OutputDir(string avatarName)
        {
            return "Assets/Yorimashi/MenuOrganizer/" + Sanitize(avatarName);
        }

        public static string RootAssetPath(string avatarName)
        {
            return OutputDir(avatarName) + "/Root.asset";
        }

        public static string TrashAssetPath(string avatarName)
        {
            return OutputDir(avatarName) + "/Trash.asset";
        }

        /// <summary>
        /// 从 hierarchy path 找 avatar GameObject 并返回其 MenuOrganizerOutput.
        /// existing=false: 若组件不存在返回 null (由 tool 决定是否 add).
        /// </summary>
        public static MenuOrganizerOutput FindOrNull(string avatarPath)
        {
            if (string.IsNullOrEmpty(avatarPath)) return null;
            var go = GameObject.Find(avatarPath);
            if (go == null) return null;
            return go.GetComponent<MenuOrganizerOutput>();
        }

        /// <summary>
        /// 找 avatar 的 VRCAvatarDescriptor. 用于读 desc.expressionsMenu (latest).
        /// </summary>
        public static VRCAvatarDescriptor FindDescriptor(string avatarPath)
        {
            if (string.IsNullOrEmpty(avatarPath)) return null;
            var go = GameObject.Find(avatarPath);
            if (go == null) return null;
            return go.GetComponent<VRCAvatarDescriptor>();
        }

        /// <summary>
        /// 确保目录存在, 递归 mkdir. Unity AssetDatabase.CreateAsset 不会自动 mkdir.
        /// </summary>
        public static void EnsureAssetFolder(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace("\\", "/");
            var leaf = Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && parent != "Assets")
                EnsureAssetFolder(parent);
            AssetDatabase.CreateFolder(parent, leaf);
        }

        /// <summary>
        /// SanitizeName: 文件名中的非法字符替换为 '_'.
        /// </summary>
        public static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "unnamed";
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var c in s)
            {
                if (c < ' ' || "/\\:*?\"<>|".IndexOf(c) >= 0)
                    sb.Append('_');
                else
                    sb.Append(c);
            }
            return sb.ToString();
        }

        /// <summary>
        /// 备份现有 menu asset 到 Library/yorimashi_oplog/menu_snapshots/&lt;request_id&gt;.json.
        /// 用于 rollback tool 恢复.
        /// </summary>
        public static string SnapshotBackup(
            VRCExpressionsMenu currentMenu, string avatarName, string requestId)
        {
            var dir = Path.Combine(
                Application.persistentDataPath, "yorimashi_oplog", "menu_snapshots");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, requestId + ".json");
            var payload = new StringBuilder();
            payload.Append("{\"avatar\":\"").Append(avatarName.Replace("\"", "\\\""));
            payload.Append("\",\"menuAssetPath\":\"");
            if (currentMenu != null)
            {
                var p = AssetDatabase.GetAssetPath(currentMenu);
                payload.Append(p?.Replace("\"", "\\\"") ?? "");
            }
            payload.Append("\",\"snapshotUtc\":\"")
                   .Append(DateTime.UtcNow.ToString("o"))
                   .Append("\"}");
            File.WriteAllText(file, payload.ToString());
            return file;
        }
    }
}
#endif
