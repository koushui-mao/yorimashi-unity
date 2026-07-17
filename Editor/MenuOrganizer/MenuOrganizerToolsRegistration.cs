// MenuOrganizer 9 个 tool 的注册段 (M4-B).
// InitializeOnLoad 保证 Unity 加载完 asmdef 立即注册, 无需老 YorimashiTools 显式调.

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using UnityEditor;
using Yorimashi.Modder.Editor;
using Yorimashi.Modder.Editor.MenuOrganizer.Tools;

namespace Yorimashi.Modder.Editor.MenuOrganizer
{
    [InitializeOnLoad]
    public static class MenuOrganizerToolsRegistration
    {
        static MenuOrganizerToolsRegistration()
        {
            // Ensure 主 registry 已 boot (老 tool 也在)
            YorimashiToolRegistry.EnsureBooted();
            RegisterAll();
        }

        public static void RegisterAll()
        {
            // 1) read_tree (读)
            {
                var t = new MenuOrganizerReadTreeTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "读取 avatar 的菜单树 (source: output/latest/trash)",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"avatar\"],\"properties\":{" +
                        "\"avatar\":{\"type\":\"string\"}," +
                        "\"source\":{\"type\":\"string\",\"enum\":[\"output\",\"latest\",\"trash\"],\"default\":\"output\"}" +
                    "}}",
                }, t.Execute);
            }

            // 2) capture
            {
                var t = new MenuOrganizerCaptureTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "捕获 MA 拼好的最终菜单树到 Output. 需要 Play 模式先运行让 NDMF 生成 desc.expressionsMenu.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"avatar\"],\"properties\":{" +
                        "\"avatar\":{\"type\":\"string\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 3) reorder
            {
                var t = new MenuOrganizerReorderTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "改指定 folder 的 children 顺序.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"avatar\",\"newOrder\"],\"properties\":{" +
                        "\"avatar\":{\"type\":\"string\"}," +
                        "\"path\":{\"type\":\"string\",\"description\":\"folder path relative to root, empty=root\"}," +
                        "\"newOrder\":{\"type\":\"array\",\"items\":{\"type\":\"string\"}}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 4) move_to_folder
            {
                var t = new MenuOrganizerMoveToFolderTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "移动节点到指定子文件夹.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"avatar\",\"sourcePath\"],\"properties\":{" +
                        "\"avatar\":{\"type\":\"string\"}," +
                        "\"sourcePath\":{\"type\":\"string\"}," +
                        "\"targetFolderPath\":{\"type\":\"string\",\"description\":\"empty=root\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 5) rename
            {
                var t = new MenuOrganizerRenameTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "改 menu 节点的 name/icon.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"avatar\",\"path\"],\"properties\":{" +
                        "\"avatar\":{\"type\":\"string\"}," +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"newName\":{\"type\":\"string\"}," +
                        "\"newIconGuid\":{\"type\":\"string\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 6) trash
            {
                var t = new MenuOrganizerTrashTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "把节点扔进回收站, 永不复活.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"avatar\",\"path\"],\"properties\":{" +
                        "\"avatar\":{\"type\":\"string\"}," +
                        "\"path\":{\"type\":\"string\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 7) restore
            {
                var t = new MenuOrganizerRestoreTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "从回收站拿回节点.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"avatar\",\"trashPath\"],\"properties\":{" +
                        "\"avatar\":{\"type\":\"string\"}," +
                        "\"trashPath\":{\"type\":\"string\"}," +
                        "\"targetFolderPath\":{\"type\":\"string\",\"description\":\"empty=root\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 8) build_output
            {
                var t = new MenuOrganizerBuildOutputTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "手动触发一次 Merge (读 latest+organized+trash), 写盘 Output. NDMF build 时也会做, 但这里让 agent 预览.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"avatar\"],\"properties\":{" +
                        "\"avatar\":{\"type\":\"string\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }

            // 9) rollback (救急)
            {
                var t = new MenuOrganizerRollbackTool();
                YorimashiToolRegistry.Register(new ToolInfo
                {
                    name = t.ToolName,
                    description = "救急: 删 MenuOrganizerOutput 组件 + 删 Output/ 目录, 恢复到 plugin 未介入前状态. 需 confirm=true.",
                    inputSchemaJson = "{\"type\":\"object\",\"required\":[\"avatar\",\"confirm\"],\"properties\":{" +
                        "\"avatar\":{\"type\":\"string\"}," +
                        "\"confirm\":{\"type\":\"boolean\"}," +
                        "\"dry_run\":{\"type\":\"boolean\",\"default\":true}" +
                    "}}",
                }, t.Execute);
            }
        }
    }
}
#endif
