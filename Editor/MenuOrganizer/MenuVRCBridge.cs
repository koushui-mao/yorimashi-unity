// Yorimashi MenuOrganizer — VRC ↔ MenuNode 数据搬运 (M4-B, Unity 层)
//
// 只有装了 NDMF/MA/VRC SDK 才编译.
// **注意**: 服务端 tests-cs 不包含此文件, Unity 实机测试覆盖.
//
// 语义:
//   - FromVRCMenu(root, guidResolver): 深遍历 VRCExpressionsMenu → MenuNode 树
//     * subMenu asset 引用递归展开成 Children (跟 MokonuKit 一样, 拆共享子菜单)
//     * ancestors set 防循环
//     * FaceEmo 动态图标 (非 asset 引用) → 用 iconGuid=null + 保留 Texture2D 引用
//   - ToVRCMenu(node): MenuNode 树 → VRC 运行时 ScriptableObject.CreateInstance
//     * 不写盘, 只在内存中构造用于 desc.expressionsMenu 覆盖
//
// Sentinel: "M4-B UNITY BRIDGE"

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Yorimashi.Modder.Editor.MenuOrganizer
{
    public static class MenuVRCBridge
    {
        public const string SentinelTag = "M4-B UNITY BRIDGE";

        /// <summary>
        /// 把 VRCExpressionsMenu 树转成我们的 MenuNode 模型.
        /// 会递归展开 subMenu 引用 (深拷贝, 不再共享).
        /// texturePool 存 Icon Texture2D -> MenuNode 的映射, 供 BuildRuntime 时再挂回去.
        /// </summary>
        public static MenuNode FromVRCMenu(
            VRCExpressionsMenu rootAsset,
            string rootName,
            Dictionary<MenuNode, Texture2D> texturePool = null)
        {
            var visited = new HashSet<VRCExpressionsMenu>();
            var root = new MenuNode
            {
                Name = rootName,
                Type = MenuControlType.SubMenu,
            };
            if (rootAsset != null)
                LoadInto(root, rootAsset, visited, texturePool);
            return root;
        }

        static void LoadInto(
            MenuNode dest,
            VRCExpressionsMenu asset,
            HashSet<VRCExpressionsMenu> ancestors,
            Dictionary<MenuNode, Texture2D> texturePool)
        {
            if (asset == null) return;
            if (!ancestors.Add(asset)) return;   // 防循环
            try
            {
                foreach (var ctrl in asset.controls)
                {
                    var node = new MenuNode
                    {
                        Name = ctrl.name ?? "",
                        Type = (MenuControlType)(int)ctrl.type,
                        ParameterName = ctrl.parameter?.name ?? "",
                        ParameterValue = ctrl.value,
                    };
                    if (ctrl.subParameters != null)
                    {
                        foreach (var sp in ctrl.subParameters)
                        {
                            node.SubParameterNames.Add(sp?.name ?? "");
                        }
                    }
                    // Icon
                    if (ctrl.icon != null)
                    {
                        var path = AssetDatabase.GetAssetPath(ctrl.icon);
                        if (!string.IsNullOrEmpty(path))
                        {
                            node.IconGuid = AssetDatabase.AssetPathToGUID(path);
                        }
                        if (texturePool != null)
                            texturePool[node] = ctrl.icon;
                    }
                    // SubMenu 递归
                    if (node.Type == MenuControlType.SubMenu && ctrl.subMenu != null)
                    {
                        var subPath = AssetDatabase.GetAssetPath(ctrl.subMenu);
                        if (!string.IsNullOrEmpty(subPath))
                            node.SubMenuAssetGuid = AssetDatabase.AssetPathToGUID(subPath);
                        LoadInto(node, ctrl.subMenu, ancestors, texturePool);
                    }
                    dest.Children.Add(node);
                }
            }
            finally
            {
                ancestors.Remove(asset);
            }
        }

        /// <summary>
        /// 把 MenuNode 树转成 VRC 运行时 SO (ScriptableObject.CreateInstance, 不写盘).
        /// 用于 NDMF Plugin 里的 desc.expressionsMenu 覆盖.
        /// </summary>
        public static VRCExpressionsMenu BuildRuntime(
            MenuNode root,
            Dictionary<MenuNode, Texture2D> texturePool = null)
        {
            var asset = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            if (root != null)
            {
                foreach (var child in root.Children)
                {
                    asset.controls.Add(BuildControl(child, texturePool));
                }
            }
            return asset;
        }

        static VRCExpressionsMenu.Control BuildControl(
            MenuNode node,
            Dictionary<MenuNode, Texture2D> texturePool)
        {
            var ctrl = new VRCExpressionsMenu.Control
            {
                name = node.Name ?? "",
                type = (VRCExpressionsMenu.Control.ControlType)(int)node.Type,
                value = node.ParameterValue,
                parameter = new VRCExpressionsMenu.Control.Parameter
                {
                    name = node.ParameterName ?? "",
                },
            };
            if (node.SubParameterNames != null && node.SubParameterNames.Count > 0)
            {
                var subs = new List<VRCExpressionsMenu.Control.Parameter>();
                foreach (var name in node.SubParameterNames)
                {
                    subs.Add(new VRCExpressionsMenu.Control.Parameter { name = name ?? "" });
                }
                ctrl.subParameters = subs.ToArray();
            }
            else
            {
                ctrl.subParameters = new VRCExpressionsMenu.Control.Parameter[0];
            }
            // Icon 恢复
            if (texturePool != null && texturePool.TryGetValue(node, out var tex))
            {
                ctrl.icon = tex;
            }
            else if (!string.IsNullOrEmpty(node.IconGuid))
            {
                var path = AssetDatabase.GUIDToAssetPath(node.IconGuid);
                if (!string.IsNullOrEmpty(path))
                    ctrl.icon = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            }
            // SubMenu
            if (node.Type == MenuControlType.SubMenu)
            {
                if (!string.IsNullOrEmpty(node.SubMenuAssetGuid))
                {
                    // 优先按 GUID 引用（保留 asset 引用）
                    var path = AssetDatabase.GUIDToAssetPath(node.SubMenuAssetGuid);
                    if (!string.IsNullOrEmpty(path))
                        ctrl.subMenu = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(path);
                }
                if (ctrl.subMenu == null && node.Children.Count > 0)
                {
                    // 内联子树 → 递归构造 runtime SO
                    var subAsset = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                    foreach (var subChild in node.Children)
                    {
                        subAsset.controls.Add(BuildControl(subChild, texturePool));
                    }
                    ctrl.subMenu = subAsset;
                }
            }
            return ctrl;
        }
    }
}
#endif
