// menu_organizer/read_tree — M4-B (只读 tool)
//
// 输入:
//   avatar (string, required) — hierarchy path
//   source (string, opt) — "output"(默认)/"latest"/"trash"
//
// 返回:
//   { source: str, empty: bool, tree: MenuNodeJSON }

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using System.Collections.Generic;
using Yorimashi.Modder.Editor;
using Yorimashi.Modder.Editor.Diagnostics;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Yorimashi.Modder.Editor.MenuOrganizer.Tools
{
    public class MenuOrganizerReadTreeTool : YorimashiReadToolBase
    {
        public override string ToolName => "menu_organizer/read_tree";

        protected override Dictionary<string, object> BuildResult(Dictionary<string, object> parsed)
        {
            string avatar;
            using (Diag.Step("validate_avatar"))
            {
                Diag.Expect("params.avatar 应为非空 hierarchy path");
                if (!parsed.TryGetValue("avatar", out var av) || !(av is string ap) || string.IsNullOrEmpty(ap))
                {
                    Diag.Actual("missing/empty");
                    Diag.Hint(1.0, "缺 avatar 参数", "传 'avatar':'Ramune_test/R05'");
                    throw new System.ArgumentException("missing required 'avatar'");
                }
                avatar = ap;
            }

            string source = "output";
            if (parsed.TryGetValue("source", out var sv) && sv is string ss && !string.IsNullOrEmpty(ss))
                source = ss;

            VRCExpressionsMenu asset;
            using (Diag.Step("locate_menu_asset"))
            {
                Diag.Expect("能按 source 找到 VRCExpressionsMenu");
                if (source == "latest")
                {
                    var desc = MenuOrganizerHelpers.FindDescriptor(avatar);
                    if (desc == null)
                    {
                        Diag.Actual("VRCAvatarDescriptor not found");
                        Diag.Hint(0.85, "path 指向了 avatar 内部子物体而非根");
                        Diag.Hint(0.15, "avatar 已删除或场景未加载");
                        throw new System.InvalidOperationException("VRCAvatarDescriptor not found on: " + avatar);
                    }
                    asset = desc.expressionsMenu;
                }
                else
                {
                    var output = MenuOrganizerHelpers.FindOrNull(avatar);
                    if (output == null)
                    {
                        Diag.Actual("MenuOrganizerOutput not found");
                        Diag.Hint(0.9, "avatar 未挂 MenuOrganizerOutput", "先跑 menu_organizer/capture");
                        throw new System.InvalidOperationException("MenuOrganizerOutput not found on: " + avatar);
                    }
                    asset = source == "trash" ? output.trashRoot : output.menu;
                }
            }

            var root = MenuVRCBridge.FromVRCMenu(asset, source);
            return new Dictionary<string, object>
            {
                ["source"] = source,
                ["empty"] = asset == null,
                ["treeJson"] = MenuNodeJson.Serialize(root),
            };
        }
    }
}
#endif
