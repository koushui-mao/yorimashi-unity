// Yorimashi MenuOrganizer — Runtime component (M4-B2)
//
// 挂在 avatar 根下的标记组件，告诉 MenuOrganizerPlugin 该 avatar 用了本插件：
//   - menu = 用户已整理的 Root asset
//   - trashRoot = 回收站 Root asset (黑名单来源)
//   - addMenuName = 新增项自动进的文件夹名
//   - prioritizeAddMenu = 是否把"添加"文件夹置顶
//   - bypass = 临时禁用整理 (Play 时用原始 MA 生成菜单, 不删组件)
//
// **红线**:
//   - 必须 IEditorOnly, 防止上传到 VRChat (VRC SDK 上传时剥离)
//   - [DisallowMultipleComponent] 一个 avatar 只挂一个
//   - 未装 NDMF/MA/VRC SDK 时整个类跳过编译 (由 asmdef versionDefines 控制)
//
// Sentinel: "M4-B2 MENU ORGANIZER OUTPUT"

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace Yorimashi.Modder.Editor.MenuOrganizer
{
    [DisallowMultipleComponent]
    [AddComponentMenu("Yorimashi/MenuOrganizer Output")]
    public class MenuOrganizerOutput : MonoBehaviour, IEditorOnly
    {
        public const string SentinelTag = "M4-B2 MENU ORGANIZER OUTPUT";

        [Tooltip("整理后的菜单根 asset (Root.asset)")]
        public VRCExpressionsMenu menu;

        [Tooltip("回收站根 asset (Trash.asset) — 该资源的 leaf 键会加入永久黑名单")]
        public VRCExpressionsMenu trashRoot;

        [Tooltip("MA 新增项自动进的文件夹名, 默认 '添加'")]
        public string addMenuName = "添加";

        [Tooltip("勾选后 '添加' 文件夹置顶显示")]
        public bool prioritizeAddMenu = false;

        [Tooltip("勾选后 build 时跳过整理 (用原始 MA 生成菜单), 组件保留.")]
        public bool bypass = false;
    }
}
#endif
