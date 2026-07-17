// Yorimashi MenuOrganizer — NDMF Plugin 主类 (M4-B12)
//
// 挂钩 NDMF Transforming phase, AfterPlugin("nadena.dev.modular-avatar").
// MA 拼完 desc.expressionsMenu 后我们接管:
//   1. 找 avatar 上的 MenuOrganizerOutput 组件 (没有则完全放行)
//   2. bypass=true → skip
//   3. 从 output.menu / output.trashRoot / desc.expressionsMenu 读三棵树
//   4. 走 MenuTreeMerge.Merge 得到最终树
//   5. BuildRuntime → ScriptableObject.CreateInstance (不写盘)
//   6. 覆盖 desc.expressionsMenu
//
// **红线**:
//   - 只在 avatar 挂了 MenuOrganizerOutput 时生效, 未挂的 avatar 完全不受影响
//   - 未装 NDMF/MA/VRC SDK 时整个类跳过编译
//   - 异常吞掉打 log, 不带崩 NDMF 管线 (导致整个 avatar build 失败)
//
// Sentinel: "M4-B12 MENU ORGANIZER PLUGIN"

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using System;
using System.Collections.Generic;
using nadena.dev.ndmf;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

[assembly: ExportsPlugin(typeof(Yorimashi.Modder.Editor.MenuOrganizer.MenuOrganizerPlugin))]

namespace Yorimashi.Modder.Editor.MenuOrganizer
{
    public class MenuOrganizerPlugin : Plugin<MenuOrganizerPlugin>
    {
        public const string SentinelTag = "M4-B12 MENU ORGANIZER PLUGIN";

        public override string QualifiedName => "online.koushui.yorimashi.menu_organizer";
        public override string DisplayName => "Yorimashi MenuOrganizer";

        protected override void Configure()
        {
            InPhase(BuildPhase.Transforming)
                .AfterPlugin("nadena.dev.modular-avatar")
                .Run("MenuOrganizer.ApplyOverlay", RunOverlay);
        }

        static void RunOverlay(BuildContext ctx)
        {
            try
            {
                RunOverlayInner(ctx);
            }
            catch (Exception e)
            {
                // 不允许把 NDMF build 管线搞挂 —— 任何异常在这里吞掉打 log,
                // avatar 会以未经我们整理的原始 desc.expressionsMenu 上传.
                Debug.LogError("[Yorimashi.MenuOrganizer] overlay failed, "
                             + "falling back to raw MA-generated menu: " + e);
            }
        }

        static void RunOverlayInner(BuildContext ctx)
        {
            if (ctx?.AvatarRootObject == null) return;
            var avatar = ctx.AvatarRootObject;

            var output = avatar.GetComponentInChildren<MenuOrganizerOutput>(includeInactive: true);
            if (output == null) return;   // 未挂组件 = 用户没用本 plugin, 完全放行

            if (output.bypass)
            {
                Debug.Log("[Yorimashi.MenuOrganizer] bypass=true → skip overlay");
                return;
            }

            var desc = ctx.AvatarDescriptor;
            if (desc == null)
            {
                Debug.LogWarning("[Yorimashi.MenuOrganizer] avatar has no VRCAvatarDescriptor");
                return;
            }

            // 读三棵树 → MenuNode 模型
            var texPool = new Dictionary<MenuNode, Texture2D>();
            var organized = MenuVRCBridge.FromVRCMenu(
                output.menu, "Organized", texPool);
            var trash = MenuVRCBridge.FromVRCMenu(
                output.trashRoot, "Trash", null);
            var latest = MenuVRCBridge.FromVRCMenu(
                desc.expressionsMenu, "Latest", texPool);

            // 核心 merge (纯逻辑层, 服务端已测)
            var merged = MenuTreeMerge.Merge(
                organized, latest, trash,
                addMenuName: string.IsNullOrEmpty(output.addMenuName) ? "添加" : output.addMenuName,
                prioritizeAddBucket: output.prioritizeAddMenu);

            // 转回 VRC runtime SO
            var newMenu = MenuVRCBridge.BuildRuntime(merged, texPool);

            desc.expressionsMenu = newMenu;
            Debug.Log("[Yorimashi.MenuOrganizer] overlay applied on '"
                    + avatar.name + "', root children="
                    + merged.Children.Count);
        }
    }
}
#endif
