// Yorimashi MenuOrganizer — MenuNode 数据模型 (M4-B3, 纯逻辑层)
//
// **纯 System.* 依赖**，服务端 dotnet 能完整编译 + 测试。
// 不引 UnityEngine / VRC / NDMF。真 Unity 层负责把 VRCExpressionsMenu 转成 MenuNode。
//
// 语义对齐 VRCExpressionsMenu.Control：
//   - Toggle / SubMenu / Button / TwoAxisPuppet / FourAxisPuppet / RadialPuppet
//   - 每个 Control 有 name / parameter / value / subParameters / subMenu
//
// 我们只在 MenuNode 里存**够 diff/merge 用的字段**，不代表 100% Control 序列化。
// 真 build 时把 MenuNode 转回 Control，构造 runtime SO。
//
// Sentinel: "M4-B3 MENU NODE MODEL"

using System;
using System.Collections.Generic;
using System.Text;

namespace Yorimashi.Modder.Editor.MenuOrganizer
{
    /// <summary>
    /// VRC Control 类型对应的枚举（数字对齐 VRCExpressionsMenu.Control.ControlType）。
    /// 我们不引 VRC SDK, 数值硬编码, 转换时对齐 SDK 侧枚举。
    /// </summary>
    public enum MenuControlType
    {
        Button = 0,
        Toggle = 1,
        SubMenu = 2,
        TwoAxisPuppet = 3,
        FourAxisPuppet = 4,
        RadialPuppet = 5,
    }

    /// <summary>
    /// 一条菜单项的数据快照。
    /// </summary>
    public class MenuNode
    {
        public string Name = "";
        public MenuControlType Type = MenuControlType.Toggle;
        public string ParameterName = "";
        public float ParameterValue = 0f;
        /// <summary>
        /// SubParameters (Puppet 用). 存 name only, 顺序敏感.
        /// </summary>
        public List<string> SubParameterNames = new List<string>();
        /// <summary>
        /// Icon 引用 GUID (Unity asset GUID string). 服务端不解析实际 Texture2D.
        /// </summary>
        public string IconGuid = null;
        /// <summary>
        /// SubMenu 引用 GUID (指向另一个 VRCExpressionsMenu asset).
        /// 若是 Children 型子菜单则为 null, Children 存在 Children 字段里.
        /// </summary>
        public string SubMenuAssetGuid = null;
        /// <summary>
        /// 子节点树（只对 SubMenu 类型有意义）.
        /// </summary>
        public List<MenuNode> Children = new List<MenuNode>();
        /// <summary>
        /// 标记：这个 SubMenu 是"添加"文件夹（新增项自动归类桶）.
        /// </summary>
        public bool IsAddBucket = false;
        /// <summary>
        /// 标记：溢出时自动折进 more 子菜单.
        /// </summary>
        public bool AutoMore = true;

        /// <summary>
        /// ItemKey — 决定"MA 又生成的这项是不是我认识的"的指纹.
        /// 只看功能特征 (type + parameter + value + subParameters), 不看 name/icon.
        /// 语义: MA 改了 name/icon/位置, 只要参数没变就还是同一项.
        /// </summary>
        public string ItemKey()
        {
            var sb = new StringBuilder(64);
            sb.Append((int)Type).Append('|')
              .Append(ParameterName ?? "").Append('|')
              .Append(ParameterValue.ToString("R",
                    System.Globalization.CultureInfo.InvariantCulture)).Append('|');
            if (SubParameterNames != null)
            {
                for (int i = 0; i < SubParameterNames.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(SubParameterNames[i] ?? "");
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// SubMenu 型节点的目录 key —— 用 name 作为身份 (不看内容, 内容 diff 交给 children 递归).
        /// </summary>
        public string FolderKey()
        {
            return Type == MenuControlType.SubMenu ? (Name ?? "") : null;
        }

        public bool IsSubMenu => Type == MenuControlType.SubMenu;

        public MenuNode Clone()
        {
            var c = new MenuNode
            {
                Name = Name,
                Type = Type,
                ParameterName = ParameterName,
                ParameterValue = ParameterValue,
                IconGuid = IconGuid,
                SubMenuAssetGuid = SubMenuAssetGuid,
                IsAddBucket = IsAddBucket,
                AutoMore = AutoMore,
            };
            c.SubParameterNames = new List<string>(SubParameterNames ?? new List<string>());
            c.Children = new List<MenuNode>();
            foreach (var child in Children)
                c.Children.Add(child.Clone());
            return c;
        }
    }

    /// <summary>
    /// 一棵完整菜单树 + 回收站。
    /// </summary>
    public class MenuTree
    {
        public MenuNode Root = new MenuNode
        {
            Name = "Root",
            Type = MenuControlType.SubMenu,
        };
        public MenuNode Trash = new MenuNode
        {
            Name = "Trash",
            Type = MenuControlType.SubMenu,
        };

        /// <summary>
        /// 遍历所有非 SubMenu 的叶子，收集 ItemKey.
        /// </summary>
        public static void CollectLeafKeys(MenuNode node, HashSet<string> keys)
        {
            if (node == null) return;
            if (!node.IsSubMenu)
            {
                keys.Add(node.ItemKey());
                return;
            }
            foreach (var c in node.Children)
                CollectLeafKeys(c, keys);
        }

        /// <summary>
        /// 遍历所有 SubMenu 节点，收集 folder name.
        /// </summary>
        public static void CollectFolderNames(MenuNode node, HashSet<string> names)
        {
            if (node == null) return;
            if (node.IsSubMenu)
            {
                if (!string.IsNullOrEmpty(node.Name))
                    names.Add(node.Name);
                foreach (var c in node.Children)
                    CollectFolderNames(c, names);
            }
        }
    }
}
