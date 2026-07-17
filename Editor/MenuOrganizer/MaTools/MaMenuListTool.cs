// ma_menu/* 8 tool 集中实现 (M4-A)
//
// 依代前置编排层: 装 avatar 时决定菜单挂哪个父目录, 直接操作 MA MenuItem/MenuInstaller.
// 相比 M4-B (后置整理), 这批 tool 全部 MutatesAsset=false, 只改 scene 内 Component
// instance state, 不回写 asset (跟老 M3-E blendshape/set 等一样).
//
// 8 tool:
//   ma_menu/list                只读, 列 avatar 下所有 MA MenuInstaller+MenuItem
//   ma_menu/create_folder       建 GameObject + MA MenuItem(SubMenu, Children)
//   ma_menu/set_parent          移 MenuItem GameObject 到另一个父菜单
//   ma_menu/set_field           改 MA MenuItem 的字段
//   ma_menu/set_installer       改 MA MenuInstaller 的 installTargetMenu
//   ma_menu/set_merge_order     改 MergeOrder
//   ma_menu/delete_item         删 MA MenuItem 组件 (不删 GO)
//   ma_menu/read_target_menu    只读, 读 MenuInstaller 指的 VRCExpressionsMenu asset
//
// **注意**: MA API 使用了 reflection 而非 hard link, 兼容 MA 版本变动.
//          原因: 依代要跨多个 MA 版本工作 (1.10.5 ~ 1.11+), 直接 hard-link
//          某个 MA 类字段会因 upgrade 挂. 用 reflection 找 field/property.

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using Yorimashi.Modder.Editor;
using Yorimashi.Modder.Editor.Diagnostics;

namespace Yorimashi.Modder.Editor.MenuOrganizer.MaTools
{
    /// <summary>
    /// MA 组件类型反射查找 (兼容 MA 版本变动).
    /// </summary>
    internal static class MaReflection
    {
        public const string MenuItemTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMenuItem";
        public const string MenuInstallerTypeName = "nadena.dev.modular_avatar.core.ModularAvatarMenuInstaller";

        static Type _menuItemType;
        static Type _menuInstallerType;

        public static Type MenuItemType => _menuItemType ??= ResolveType(MenuItemTypeName);
        public static Type MenuInstallerType => _menuInstallerType ??= ResolveType(MenuInstallerTypeName);

        static Type ResolveType(string typeName)
        {
            var t = Type.GetType(typeName);
            if (t != null) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                t = asm.GetType(typeName);
                if (t != null) return t;
            }
            return null;
        }

        public static bool MaAvailable => MenuItemType != null && MenuInstallerType != null;

        /// <summary>
        /// 反射读组件字段/属性. 不存在返回 null.
        /// </summary>
        public static object GetMember(object obj, string name)
        {
            if (obj == null) return null;
            var t = obj.GetType();
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) return f.GetValue(obj);
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null) return p.GetValue(obj);
            return null;
        }

        /// <summary>反射写组件字段/属性.</summary>
        public static bool SetMember(object obj, string name, object value)
        {
            if (obj == null) return false;
            var t = obj.GetType();
            var f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (f != null) { f.SetValue(obj, value); return true; }
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (p != null && p.CanWrite) { p.SetValue(obj, value); return true; }
            return false;
        }
    }

    // ==============================================================
    // 1) ma_menu/list — 遍历 avatar 下所有 MA MenuInstaller + MenuItem
    // ==============================================================
    public class MaMenuListTool : YorimashiReadToolBase
    {
        public override string ToolName => "ma_menu/list";

        protected override Dictionary<string, object> BuildResult(Dictionary<string, object> parsed)
        {
            using (Diag.Step("check_ma_available"))
            {
                if (!MaReflection.MaAvailable)
                {
                    Diag.Actual("MA types not found");
                    Diag.Hint(1.0, "客户项目未装 Modular Avatar",
                                    "从 https://modular-avatar.nadena.dev/ 装 MA 后重试");
                    throw new InvalidOperationException("Modular Avatar not installed in this project");
                }
            }

            string avatar;
            using (Diag.Step("validate_avatar"))
            {
                if (!parsed.TryGetValue("avatar", out var av) || !(av is string ap) || string.IsNullOrEmpty(ap))
                {
                    Diag.Hint(1.0, "缺 avatar 参数");
                    throw new ArgumentException("missing 'avatar'");
                }
                avatar = ap;
            }

            var go = GameObject.Find(avatar);
            if (go == null)
                throw new InvalidOperationException("avatar not found in scene: " + avatar);

            var installers = new List<Dictionary<string, object>>();
            var items = new List<Dictionary<string, object>>();

            // 遍历所有 Transform 子孙, 找 MA 组件
            foreach (var tr in go.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                var path = GetHierarchyPath(go, tr);
                foreach (var comp in tr.GetComponents<MonoBehaviour>())
                {
                    if (comp == null) continue;
                    var t = comp.GetType();
                    if (t == MaReflection.MenuInstallerType)
                    {
                        installers.Add(new Dictionary<string, object>
                        {
                            ["path"] = path,
                            ["installTargetMenu"] = AssetPathOf(MaReflection.GetMember(comp, "installTargetMenu")),
                            ["menuToAppend"] = AssetPathOf(MaReflection.GetMember(comp, "menuToAppend")),
                        });
                    }
                    else if (t == MaReflection.MenuItemType)
                    {
                        items.Add(new Dictionary<string, object>
                        {
                            ["path"] = path,
                            ["menuName"] = go.name,   // MA MenuItem 用 GameObject.name 作菜单名
                            ["controlType"] = ControlTypeStr(MaReflection.GetMember(comp, "Control")),
                            ["mergeOrder"] = MergeOrderOf(MaReflection.GetMember(comp, "MergeOrder")),
                            ["hasControl"] = MaReflection.GetMember(comp, "Control") != null,
                        });
                    }
                }
            }

            return new Dictionary<string, object>
            {
                ["avatar"] = avatar,
                ["installers"] = installers,
                ["items"] = items,
                ["totalInstallers"] = installers.Count,
                ["totalItems"] = items.Count,
            };
        }

        static string GetHierarchyPath(GameObject root, Transform tr)
        {
            var stack = new List<string>();
            var cur = tr;
            while (cur != null && cur.gameObject != root)
            {
                stack.Add(cur.name);
                cur = cur.parent;
            }
            stack.Add(root.name);
            stack.Reverse();
            return string.Join("/", stack);
        }

        static string AssetPathOf(object obj)
        {
            if (obj is UnityEngine.Object uo && uo != null)
                return AssetDatabase.GetAssetPath(uo);
            return null;
        }

        static string ControlTypeStr(object control)
        {
            if (control == null) return null;
            var typeField = control.GetType().GetField("type", BindingFlags.Public | BindingFlags.Instance);
            if (typeField == null) return null;
            var v = typeField.GetValue(control);
            return v?.ToString();
        }

        static int MergeOrderOf(object v)
        {
            try
            {
                if (v == null) return 0;
                return Convert.ToInt32(v);
            }
            catch { return 0; }
        }
    }
}
#endif
