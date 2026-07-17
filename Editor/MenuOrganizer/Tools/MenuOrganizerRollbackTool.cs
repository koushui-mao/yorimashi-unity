// menu_organizer/rollback — M4-B15 (救急 tool)
//
// 目的: 用户 dogfood 时发现 build_output 结果不对, 一条命令回滚:
//   1. 删 MenuOrganizerOutput 组件
//   2. 删 Assets/Yorimashi/MenuOrganizer/<avatar>/ 目录 (Root.asset + Trash.asset)
//   3. Unity 下次 build 时 NDMF Plugin 发现无组件 → 跳过整理 → 用原始 MA 菜单
//
// 输入:
//   avatar (string, required)
//   confirm (bool, required=true) — 因为破坏性, 必须显式 confirm
//
// **push back**: rollback 不备份, 因为设计初衷就是"恢复到本 plugin 未介入前的状态".
// 用户如果想保留当前整理结果, 应该在 rollback 前手动 read_tree 存 JSON.

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Yorimashi.Modder.Editor;
using Yorimashi.Modder.Editor.Diagnostics;

namespace Yorimashi.Modder.Editor.MenuOrganizer.Tools
{
    public class MenuOrganizerRollbackTool : YorimashiWriteToolBase
    {
        public override string ToolName => "menu_organizer/rollback";
        protected override bool MutatesAsset => true;
        protected override bool AllowAssetMutation(Dictionary<string, object> parsed, out string reason)
        {
            // 只删自己的 output dir
            reason = null;
            return true;
        }

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            string avatarPath;
            using (Diag.Step("validate_avatar"))
            {
                if (!parsed.TryGetValue("avatar", out var av) || !(av is string ap) || string.IsNullOrEmpty(ap))
                {
                    Diag.Hint(1.0, "缺 avatar 参数");
                    throw new System.ArgumentException("missing 'avatar'");
                }
                avatarPath = ap;
                parsed["_avatarPath"] = ap;
            }

            using (Diag.Step("confirm_check"))
            {
                Diag.Expect("params.confirm 必须为 true, 因为 rollback 会删组件 + 删 asset");
                bool confirmed = parsed.TryGetValue("confirm", out var cv) && cv is bool cb && cb;
                if (!confirmed)
                {
                    Diag.Actual("confirm != true");
                    Diag.Hint(1.0, "破坏性操作必须显式 confirm=true",
                                    "'confirm': true 加到 params");
                    throw new System.InvalidOperationException(
                        "rollback needs 'confirm': true (destructive: removes component + deletes Output/ folder)");
                }
            }

            var avatarName = MenuOrganizerHelpers.Sanitize(avatarPath.Replace('/', '_'));
            var dir = MenuOrganizerHelpers.OutputDir(avatarName);
            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["op"] = "rollback",
                        ["avatar"] = avatarPath,
                        ["willRemove"] = "MenuOrganizerOutput 组件 + " + dir + "/ 目录",
                    }
                },
                BackupPath = null,
                Summary = "rollback " + avatarPath + " (删组件 + 删 " + dir + ")",
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            var avatarPath = (string)parsed["_avatarPath"];
            using (Diag.Step("apply.rollback"))
            {
                var comp = MenuOrganizerHelpers.FindOrNull(avatarPath);
                if (comp != null)
                    Undo.DestroyObjectImmediate(comp);

                var avatarName = MenuOrganizerHelpers.Sanitize(avatarPath.Replace('/', '_'));
                var dir = MenuOrganizerHelpers.OutputDir(avatarName);
                if (AssetDatabase.IsValidFolder(dir))
                    AssetDatabase.DeleteAsset(dir);
                AssetDatabase.SaveAssets();

                return new Dictionary<string, object>
                {
                    ["componentRemoved"] = comp != null,
                    ["dirRemoved"] = dir,
                };
            }
        }
    }
}
#endif
