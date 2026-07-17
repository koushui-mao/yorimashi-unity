// M4-B 6 个 tree-edit tool 集中实现
// reorder / move_to_folder / rename / trash / restore / build_output
//
// 全部继承 MenuOrganizerTreeEditToolBase, 只实现 ApplyToTree(root, trash, parsed).

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using System.Collections.Generic;
using System.Linq;
using Yorimashi.Modder.Editor;
using Yorimashi.Modder.Editor.Diagnostics;

namespace Yorimashi.Modder.Editor.MenuOrganizer.Tools
{
    // ===============================================================
    // 1) reorder — 修改指定 folder 的 children 顺序
    // 输入: path (string, 目标 folder 路径, "" = root), newOrder (list<string>, 子节点名字数组)
    // ===============================================================
    public class MenuOrganizerReorderTool : MenuOrganizerTreeEditToolBase
    {
        public override string ToolName => "menu_organizer/reorder";

        protected override (string, string) ApplyToTree(MenuNode root, MenuNode trash, Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.reorder"))
            {
                var path = parsed.TryGetValue("path", out var pv) && pv is string ps ? ps : "";
                var folder = FindByPath(root, path);
                if (folder == null)
                {
                    Diag.Actual("path not found: " + path);
                    Diag.Hint(0.9, "路径拼写错误或不存在", "先跑 read_tree 看当前结构");
                    throw new System.InvalidOperationException("path not found in tree: " + path);
                }
                if (!parsed.TryGetValue("newOrder", out var noVal) || !(noVal is List<object> noList))
                {
                    Diag.Hint(1.0, "缺 newOrder (list<string>)");
                    throw new System.ArgumentException("missing 'newOrder' array");
                }
                var newOrder = noList.Select(x => x?.ToString() ?? "").ToList();
                var nameToNode = folder.Children.ToDictionary(c => c.Name, c => c);
                var reordered = new List<MenuNode>();
                foreach (var name in newOrder)
                {
                    if (nameToNode.TryGetValue(name, out var node))
                    {
                        reordered.Add(node);
                        nameToNode.Remove(name);
                    }
                }
                // 剩下的按原相对顺序 append
                foreach (var leftover in folder.Children)
                {
                    if (nameToNode.ContainsKey(leftover.Name))
                        reordered.Add(leftover);
                }
                folder.Children.Clear();
                folder.Children.AddRange(reordered);
                return ("reorder " + (string.IsNullOrEmpty(path) ? "root" : path)
                        + " (" + folder.Children.Count + " items)",
                        "{\"path\":\"" + path + "\",\"count\":" + folder.Children.Count + "}");
            }
        }
    }

    // ===============================================================
    // 2) move_to_folder — 移动节点到指定子文件夹
    // 输入: sourcePath (string, 源节点路径), targetFolderPath (string, 目标 folder 路径, ""=root)
    // ===============================================================
    public class MenuOrganizerMoveToFolderTool : MenuOrganizerTreeEditToolBase
    {
        public override string ToolName => "menu_organizer/move_to_folder";

        protected override (string, string) ApplyToTree(MenuNode root, MenuNode trash, Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.move"))
            {
                var src = parsed.TryGetValue("sourcePath", out var sv) && sv is string ss ? ss : null;
                var dst = parsed.TryGetValue("targetFolderPath", out var dv) && dv is string ds ? ds : "";
                if (string.IsNullOrEmpty(src))
                {
                    Diag.Hint(1.0, "缺 sourcePath");
                    throw new System.ArgumentException("missing 'sourcePath'");
                }
                var srcNode = FindByPath(root, src);
                if (srcNode == null)
                {
                    Diag.Actual("source not found: " + src);
                    Diag.Hint(0.9, "源路径不存在");
                    throw new System.InvalidOperationException("source path not found: " + src);
                }
                var srcParent = FindParent(root, srcNode);
                if (srcParent == null)
                {
                    Diag.Hint(1.0, "sourcePath 指向 root 本身, 不能移动");
                    throw new System.InvalidOperationException("cannot move root itself");
                }
                var dstFolder = FindByPath(root, dst);
                if (dstFolder == null)
                {
                    Diag.Actual("target not found: " + dst);
                    Diag.Hint(0.9, "目标 folder 不存在", "先用 reorder 或 create 建 folder");
                    throw new System.InvalidOperationException("target folder not found: " + dst);
                }
                if (!dstFolder.IsSubMenu)
                {
                    Diag.Hint(1.0, "target 不是 SubMenu, 不能作为容器");
                    throw new System.InvalidOperationException("target is not SubMenu: " + dst);
                }
                srcParent.Children.Remove(srcNode);
                dstFolder.Children.Add(srcNode);
                return ("move " + src + " → " + (string.IsNullOrEmpty(dst) ? "root" : dst),
                        "{\"src\":\"" + src + "\",\"dst\":\"" + dst + "\"}");
            }
        }
    }

    // ===============================================================
    // 3) rename — 改 name / icon
    // 输入: path (string), newName (string, optional), newIconGuid (string, optional)
    // ===============================================================
    public class MenuOrganizerRenameTool : MenuOrganizerTreeEditToolBase
    {
        public override string ToolName => "menu_organizer/rename";

        protected override (string, string) ApplyToTree(MenuNode root, MenuNode trash, Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.rename"))
            {
                var path = parsed.TryGetValue("path", out var pv) && pv is string ps ? ps : null;
                if (string.IsNullOrEmpty(path))
                {
                    Diag.Hint(1.0, "缺 path");
                    throw new System.ArgumentException("missing 'path'");
                }
                var node = FindByPath(root, path);
                if (node == null)
                {
                    Diag.Actual("node not found: " + path);
                    Diag.Hint(0.9, "路径不存在");
                    throw new System.InvalidOperationException("path not found: " + path);
                }
                string oldName = node.Name;
                string oldIcon = node.IconGuid;
                if (parsed.TryGetValue("newName", out var nn) && nn is string ns)
                    node.Name = ns;
                if (parsed.TryGetValue("newIconGuid", out var ig) && ig is string igs)
                    node.IconGuid = igs;
                return ("rename " + path + " name:" + oldName + "→" + node.Name,
                        "{\"path\":\"" + path + "\",\"oldName\":\"" + oldName + "\",\"newName\":\"" + node.Name + "\"}");
            }
        }
    }

    // ===============================================================
    // 4) trash — 把节点移入 Trash (永不复活语义)
    // 输入: path (string, 目标节点路径)
    // ===============================================================
    public class MenuOrganizerTrashTool : MenuOrganizerTreeEditToolBase
    {
        public override string ToolName => "menu_organizer/trash";

        protected override (string, string) ApplyToTree(MenuNode root, MenuNode trash, Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.trash"))
            {
                var path = parsed.TryGetValue("path", out var pv) && pv is string ps ? ps : null;
                if (string.IsNullOrEmpty(path))
                {
                    Diag.Hint(1.0, "缺 path");
                    throw new System.ArgumentException("missing 'path'");
                }
                var node = FindByPath(root, path);
                if (node == null)
                {
                    Diag.Actual("node not found: " + path);
                    throw new System.InvalidOperationException("path not found: " + path);
                }
                var parent = FindParent(root, node);
                if (parent == null)
                {
                    Diag.Hint(1.0, "不能扔 root 本身");
                    throw new System.InvalidOperationException("cannot trash root");
                }
                parent.Children.Remove(node);
                // 深拷贝进 trash (保留原树独立)
                trash.Children.Add(node.Clone());
                return ("trash " + path,
                        "{\"path\":\"" + path + "\",\"itemKey\":\"" + node.ItemKey() + "\"}");
            }
        }
    }

    // ===============================================================
    // 5) restore — 从 Trash 拿回节点
    // 输入: trashPath (string, trash 里的路径), targetFolderPath (string, 恢复到哪个 folder, ""=root)
    // ===============================================================
    public class MenuOrganizerRestoreTool : MenuOrganizerTreeEditToolBase
    {
        public override string ToolName => "menu_organizer/restore";

        protected override (string, string) ApplyToTree(MenuNode root, MenuNode trash, Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.restore"))
            {
                var tp = parsed.TryGetValue("trashPath", out var tv) && tv is string ts ? ts : null;
                var dst = parsed.TryGetValue("targetFolderPath", out var dv) && dv is string ds ? ds : "";
                if (string.IsNullOrEmpty(tp))
                {
                    Diag.Hint(1.0, "缺 trashPath");
                    throw new System.ArgumentException("missing 'trashPath'");
                }
                var node = FindByPath(trash, tp);
                if (node == null)
                {
                    Diag.Actual("node not in trash: " + tp);
                    Diag.Hint(0.9, "trash 里没有该路径", "read_tree source=trash 看现状");
                    throw new System.InvalidOperationException("trash path not found: " + tp);
                }
                var parent = FindParent(trash, node);
                if (parent != null) parent.Children.Remove(node);
                var dstFolder = FindByPath(root, dst);
                if (dstFolder == null || !dstFolder.IsSubMenu)
                {
                    Diag.Hint(0.9, "目标 folder 不存在或非 SubMenu");
                    throw new System.InvalidOperationException("target folder not found: " + dst);
                }
                dstFolder.Children.Add(node);
                return ("restore " + tp + " → " + (string.IsNullOrEmpty(dst) ? "root" : dst),
                        "{\"trashPath\":\"" + tp + "\",\"targetFolder\":\"" + dst + "\"}");
            }
        }
    }

    // ===============================================================
    // 6) build_output — 手动触发一次 Merge (读 latest + organized + trash),
    //                  写盘 Output. NDMF Plugin 在 build 时也会做, 但这个 tool
    //                  让 agent 能主动预览合并结果.
    // 输入: avatar (string)
    // ===============================================================
    public class MenuOrganizerBuildOutputTool : MenuOrganizerTreeEditToolBase
    {
        public override string ToolName => "menu_organizer/build_output";

        protected override (string, string) ApplyToTree(MenuNode root, MenuNode trash, Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.merge"))
            {
                var output = (MenuOrganizerOutput)parsed["_output"];
                var avatarPath = (string)parsed["_avatarPath"];
                var desc = MenuOrganizerHelpers.FindDescriptor(avatarPath);
                if (desc == null)
                {
                    Diag.Actual("VRCAvatarDescriptor not found");
                    Diag.Hint(0.85, "path 指向了子物体");
                    throw new System.InvalidOperationException("descriptor not found: " + avatarPath);
                }

                var latest = MenuVRCBridge.FromVRCMenu(desc.expressionsMenu, "Latest");
                var merged = MenuTreeMerge.Merge(root, latest, trash,
                    addMenuName: string.IsNullOrEmpty(output.addMenuName) ? "添加" : output.addMenuName,
                    prioritizeAddBucket: output.prioritizeAddMenu);
                // Replace root with merged
                root.Children.Clear();
                foreach (var c in merged.Children) root.Children.Add(c);
                return ("build_output on " + avatarPath + " → " + root.Children.Count + " children",
                        "{\"rootCount\":" + root.Children.Count + "}");
            }
        }
    }
}
#endif
