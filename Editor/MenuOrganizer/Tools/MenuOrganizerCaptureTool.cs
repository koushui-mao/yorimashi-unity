// menu_organizer/capture — M4-B4 (写 tool)
//
// 触发流程 (需要 Play 模式 / NDMF Apply on Play):
//   1. Play 模式下 MA 拼菜单会写入 desc.expressionsMenu
//   2. capture 深遍历 desc.expressionsMenu 到 MenuNode
//   3. Serialize 到 Assets/Yorimashi/MenuOrganizer/<avatar>/Root.asset
//   4. 挂 MenuOrganizerOutput 组件到 avatar
//
// **push back**: 服务端假设用户手动 Enter Play 完 latest 生效再调 capture.
// 未来可自动化: capture 分两阶段, 先 mark_and_reload_scene, 用户确认进入 Play 后再 call finish_capture.
//
// 输入:
//   avatar (string, required)
//   dry_run (bool, default true)

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using Yorimashi.Modder.Editor;
using Yorimashi.Modder.Editor.Diagnostics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Yorimashi.Modder.Editor.MenuOrganizer.Tools
{
    public class MenuOrganizerCaptureTool : YorimashiWriteToolBase
    {
        public override string ToolName => "menu_organizer/capture";
        protected override bool MutatesAsset => true;

        protected override bool AllowAssetMutation(Dictionary<string, object> parsed, out string reason)
        {
            // 只写自己的 output dir, 不动别人的 asset
            reason = null;
            return true;
        }

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            using (Diag.Step("validate_avatar"))
            {
                Diag.Expect("avatar 参数必须提供");
                if (!parsed.TryGetValue("avatar", out var av) || !(av is string ap) || string.IsNullOrEmpty(ap))
                {
                    Diag.Actual("missing");
                    Diag.Hint(1.0, "缺 avatar 参数");
                    throw new System.ArgumentException("missing 'avatar'");
                }
                parsed["_avatarPath"] = ap;
            }

            string avatarPath = (string)parsed["_avatarPath"];
            using (Diag.Step("locate_descriptor"))
            {
                Diag.Expect("能找到 VRCAvatarDescriptor 且 expressionsMenu 非 null");
                var desc = MenuOrganizerHelpers.FindDescriptor(avatarPath);
                if (desc == null)
                {
                    Diag.Actual("VRCAvatarDescriptor not found");
                    Diag.Hint(0.85, "path 指向了子物体不是根", "用 avatar root path");
                    throw new System.InvalidOperationException("VRCAvatarDescriptor not found on: " + avatarPath);
                }
                if (desc.expressionsMenu == null)
                {
                    Diag.Actual("expressionsMenu is null");
                    Diag.Hint(0.7, "MA 还未跑, 需要 Play 模式让 NDMF Apply on Play 触发",
                                    "在 Play 模式下再触发 capture; 或先手动 Enter Play");
                    Diag.Hint(0.3, "avatar 完全没有菜单配置");
                    throw new System.InvalidOperationException("desc.expressionsMenu is null on: " + avatarPath);
                }
                parsed["_descriptor"] = desc;
            }

            var avatarName = MenuOrganizerHelpers.Sanitize(avatarPath.Replace('/', '_'));
            var rootPath = MenuOrganizerHelpers.RootAssetPath(avatarName);
            var items = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["op"] = "capture",
                    ["avatar"] = avatarPath,
                    ["outputPath"] = rootPath,
                }
            };
            return new WriteToolChanges
            {
                Items = items,
                BackupPath = null,
                Summary = "capture " + avatarPath + " → " + rootPath,
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            string avatarPath = (string)parsed["_avatarPath"];
            var desc = (VRCAvatarDescriptor)parsed["_descriptor"];

            using (Diag.Step("apply.snapshot_and_write"))
            {
                Diag.Expect("能写 Root.asset 到 Assets/Yorimashi/MenuOrganizer/<avatar>/");
                var texPool = new Dictionary<MenuNode, Texture2D>();
                var root = MenuVRCBridge.FromVRCMenu(desc.expressionsMenu, "Root", texPool);
                var avatarName = MenuOrganizerHelpers.Sanitize(avatarPath.Replace('/', '_'));
                var dir = MenuOrganizerHelpers.OutputDir(avatarName);
                var rootPath = MenuOrganizerHelpers.RootAssetPath(avatarName);
                MenuOrganizerHelpers.EnsureAssetFolder(dir);

                var rootAsset = MenuVRCBridge.BuildRuntime(root, texPool);
                AssetDatabase.CreateAsset(rootAsset, rootPath);
                AssetDatabase.SaveAssets();

                // 挂 MenuOrganizerOutput 组件到 avatar
                var go = GameObject.Find(avatarPath);
                var comp = go.GetComponent<MenuOrganizerOutput>();
                if (comp == null)
                {
                    Undo.RecordObject(go, "capture: add MenuOrganizerOutput");
                    comp = Undo.AddComponent<MenuOrganizerOutput>(go);
                }
                Undo.RecordObject(comp, "capture: set menu ref");
                comp.menu = rootAsset;

                return new Dictionary<string, object>
                {
                    ["capturedTo"] = rootPath,
                    ["componentAdded"] = comp != null,
                };
            }
        }
    }
}
#endif
