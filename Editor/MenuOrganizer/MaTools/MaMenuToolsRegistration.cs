// MA menu 8 个 tool 注册段 (M4-A). [InitializeOnLoad] 自动 register.

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using UnityEditor;
using Yorimashi.Modder.Editor;
using Yorimashi.Modder.Editor.MenuOrganizer.MaTools;

namespace Yorimashi.Modder.Editor.MenuOrganizer.MaTools
{
    [InitializeOnLoad]
    public static class MaMenuToolsRegistration
    {
        static MaMenuToolsRegistration()
        {
            YorimashiToolRegistry.EnsureBooted();
            RegisterAll();
        }

        public static void RegisterAll()
        {
            // 1) list
            {
                var t = new MaMenuListTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "遍历 avatar 下所有 MA MenuInstaller + MenuItem 组件.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"avatar\"],\"properties\":{" +
                        "\"avatar\":{\"type\":\"string\"}" +
                    "}}",
                }, t.Execute);
            }

            // 2) create_folder
            {
                var t = new MaMenuCreateFolderTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "建带 MA MenuItem(SubMenu, Children) 的 GameObject.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"parentPath\",\"folderName\"],\"properties\":{" +
                        "\"parentPath\":{\"type\":\"string\"}," +
                        "\"folderName\":{\"type\":\"string\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 3) set_parent
            {
                var t = new MaMenuSetParentTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "移动 MenuItem GameObject 到另一个父菜单.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"itemPath\",\"newParentPath\"],\"properties\":{" +
                        "\"itemPath\":{\"type\":\"string\"}," +
                        "\"newParentPath\":{\"type\":\"string\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 4) set_field
            {
                var t = new MaMenuSetFieldTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "改 MA MenuItem 字段 (顶层 MenuSource/Automatic/MergeOrder 或 Control 内 name/type/parameter/value).",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"path\",\"fields\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"fields\":{\"type\":\"object\",\"additionalProperties\":true}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 5) set_installer
            {
                var t = new MaMenuSetInstallerTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "改 MA MenuInstaller 的 installTargetMenu.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"targetMenuAssetPath\":{\"type\":\"string\",\"description\":\"VRCExpressionsMenu asset path; empty=null\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 6) set_merge_order
            {
                var t = new MaMenuSetMergeOrderTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "改 MA MenuItem 的 MergeOrder (排序权重).",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"path\",\"newOrder\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"newOrder\":{\"type\":\"integer\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 7) delete_item
            {
                var t = new MaMenuDeleteItemTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "删 MA MenuItem 组件 (不删 GameObject).",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 8) read_target_menu (只读)
            {
                var t = new MaMenuReadTargetMenuTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "读 MenuInstaller 指的 VRCExpressionsMenu asset 内容 (only-read).",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"path\"],\"properties\":{" +
                        "\"path\":{\"type\":\"string\"}" +
                    "}}",
                }, t.Execute);
            }
        }
    }
}
#endif
