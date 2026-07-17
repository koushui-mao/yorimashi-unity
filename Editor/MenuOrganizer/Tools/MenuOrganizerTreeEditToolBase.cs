// menu_organizer/reorder — 改指定节点的 children 顺序
// menu_organizer/move_to_folder — 移动节点到指定子文件夹
// menu_organizer/rename — 改 name/icon
// menu_organizer/trash — 移入 Trash + 加黑名单
// menu_organizer/restore — 从 Trash 拿回
// menu_organizer/build_output — 编辑器手动触发一次 merge 落盘
//
// 共同 pattern: 读 Root.asset → MenuNode 树 → 应用操作 → 写回 Root.asset
//              (trash/restore 还会读写 Trash.asset)

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Yorimashi.Modder.Editor;
using Yorimashi.Modder.Editor.Diagnostics;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace Yorimashi.Modder.Editor.MenuOrganizer.Tools
{
    /// <summary>
    /// 树编辑 tool 通用基类. 子类实现 ApplyToTree(root, trash, parsed) 描述具体操作.
    /// 基类负责: 校验 avatar → 加载 Root/Trash asset → 反序列化 → 委托子类改 →
    ///          序列化 → 写回 asset → Undo 集成.
    /// </summary>
    public abstract class MenuOrganizerTreeEditToolBase : YorimashiWriteToolBase
    {
        protected override bool MutatesAsset => true;

        protected override bool AllowAssetMutation(Dictionary<string, object> parsed, out string reason)
        {
            reason = null;
            return true;   // 只写 Assets/Yorimashi/MenuOrganizer/<avatar>/
        }

        /// <summary>子类改 root / trash 树, 返回 (summary, changesJson) 描述发生了什么.</summary>
        protected abstract (string summary, string changesJson) ApplyToTree(
            MenuNode root, MenuNode trash, Dictionary<string, object> parsed);

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            string avatarPath;
            using (Diag.Step("validate_avatar"))
            {
                if (!parsed.TryGetValue("avatar", out var av) || !(av is string ap) || string.IsNullOrEmpty(ap))
                {
                    Diag.Expect("avatar 应为非空字符串");
                    Diag.Actual("missing/empty");
                    Diag.Hint(1.0, "缺 avatar 参数");
                    throw new System.ArgumentException("missing 'avatar'");
                }
                avatarPath = ap;
                parsed["_avatarPath"] = ap;
            }

            MenuOrganizerOutput output;
            using (Diag.Step("locate_output"))
            {
                output = MenuOrganizerHelpers.FindOrNull(avatarPath);
                if (output == null)
                {
                    Diag.Actual("MenuOrganizerOutput not found");
                    Diag.Hint(0.9, "avatar 未挂组件", "先跑 menu_organizer/capture");
                    throw new System.InvalidOperationException("MenuOrganizerOutput not found: " + avatarPath);
                }
                parsed["_output"] = output;
            }

            // 只算 dry_run 预览: 深遍历现树, 应用 op, 计算变更摘要
            var rootNode = MenuVRCBridge.FromVRCMenu(output.menu, "Root");
            var trashNode = MenuVRCBridge.FromVRCMenu(output.trashRoot, "Trash");
            var (summary, changesJson) = ApplyToTree(rootNode, trashNode, parsed);
            parsed["_previewRoot"] = rootNode;
            parsed["_previewTrash"] = trashNode;

            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>>
                {
                    new Dictionary<string, object>
                    {
                        ["tool"] = ToolName,
                        ["avatar"] = avatarPath,
                        ["changesJson"] = changesJson,
                    }
                },
                BackupPath = null,
                Summary = summary,
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            var output = (MenuOrganizerOutput)parsed["_output"];
            var rootNode = (MenuNode)parsed["_previewRoot"];
            var trashNode = (MenuNode)parsed["_previewTrash"];
            var avatarPath = (string)parsed["_avatarPath"];

            using (Diag.Step("apply.write_assets"))
            {
                Diag.Expect("Root.asset + Trash.asset 应能写盘");
                var avatarName = MenuOrganizerHelpers.Sanitize(avatarPath.Replace('/', '_'));
                var dir = MenuOrganizerHelpers.OutputDir(avatarName);
                MenuOrganizerHelpers.EnsureAssetFolder(dir);
                var rootPath = MenuOrganizerHelpers.RootAssetPath(avatarName);
                var trashPath = MenuOrganizerHelpers.TrashAssetPath(avatarName);

                // 写 Root
                var newRootAsset = MenuVRCBridge.BuildRuntime(rootNode);
                if (output.menu != null)
                {
                    var existingPath = AssetDatabase.GetAssetPath(output.menu);
                    if (!string.IsNullOrEmpty(existingPath))
                    {
                        AssetDatabase.DeleteAsset(existingPath);
                    }
                }
                AssetDatabase.CreateAsset(newRootAsset, rootPath);
                Undo.RecordObject(output, ToolName + ": update menu ref");
                output.menu = newRootAsset;

                // 写 Trash (仅当 trashNode 非空)
                if (trashNode != null && trashNode.Children.Count > 0)
                {
                    var newTrashAsset = MenuVRCBridge.BuildRuntime(trashNode);
                    if (output.trashRoot != null)
                    {
                        var existingPath = AssetDatabase.GetAssetPath(output.trashRoot);
                        if (!string.IsNullOrEmpty(existingPath))
                            AssetDatabase.DeleteAsset(existingPath);
                    }
                    AssetDatabase.CreateAsset(newTrashAsset, trashPath);
                    output.trashRoot = newTrashAsset;
                }
                AssetDatabase.SaveAssets();

                return new Dictionary<string, object>
                {
                    ["rootAssetPath"] = rootPath,
                    ["trashAssetPath"] = trashPath,
                };
            }
        }

        // ---------- helpers 给子类用 ----------

        /// <summary>用"/"分隔的路径找到节点 (例:"Outfits/Casual/Shirt"). null=未找到.</summary>
        protected static MenuNode FindByPath(MenuNode root, string path)
        {
            if (root == null || string.IsNullOrEmpty(path)) return root;
            var segs = path.Split('/');
            var cur = root;
            foreach (var s in segs)
            {
                if (string.IsNullOrEmpty(s)) continue;
                MenuNode next = null;
                foreach (var c in cur.Children)
                {
                    if (c.Name == s) { next = c; break; }
                }
                if (next == null) return null;
                cur = next;
            }
            return cur;
        }

        /// <summary>找到某个节点的父节点 (线性 BFS).</summary>
        protected static MenuNode FindParent(MenuNode root, MenuNode target)
        {
            if (root == null || target == null || ReferenceEquals(root, target)) return null;
            foreach (var c in root.Children)
            {
                if (ReferenceEquals(c, target)) return root;
                var found = FindParent(c, target);
                if (found != null) return found;
            }
            return null;
        }
    }
}
#endif
