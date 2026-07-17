// Yorimashi MenuOrganizer — Merge 算法 (M4-B 核心, 纯逻辑层)
//
// 完全独立算法, clean-room, 不引用 MokonuKit 任何代码.
//
// 语义:
//   Input:
//     organized = 用户已整理过的菜单树 (可能是空 = 从零开始)
//     latest    = MA 刚刚拼完的最终产物 (每次 build 都可能变)
//     trash     = 回收站 (deletedLeafKeys + deletedFolderNames 的来源)
//   Output:
//     merged 树 = organized 结构 + MA 新增项 (自动进 addBucket)
//                 - Trash 黑名单里的项永不复活
//                 - Root 层最多 MaxRootCapacity 项, SubMenu 最多 MaxSubCapacity 项
//                 - 超出自动折 "more" 子菜单
//
// VRC 硬容量:
//   Root 菜单 8 格 (SDK 硬限制) —— 我们 MaxRootCapacity = 8
//   SubMenu 8 格 —— MaxSubCapacity = 8 (需要留 Back 位置由 VRC 客户端处理, 不算我们)
//
// Sentinel: "M4-B MERGE CORE"

using System;
using System.Collections.Generic;

namespace Yorimashi.Modder.Editor.MenuOrganizer
{
    public static class MenuTreeMerge
    {
        public const string SentinelTag = "M4-B MERGE CORE";

        /// <summary>VRC ExpressionsMenu 硬限制 8 格.</summary>
        public const int MaxCapacity = 8;

        /// <summary>更多子菜单的名字, 溢出时自动创建.</summary>
        public const string MoreFolderName = "more";

        /// <summary>
        /// 核心 merge 入口. 返回一个新树, 不改动 organized / latest / trash.
        /// </summary>
        /// <param name="organized">用户整理版 root (SubMenu 型). 可为 null (从 latest 起 fresh).</param>
        /// <param name="latest">MA 生成的最新版 root (SubMenu 型). 可为 null.</param>
        /// <param name="trash">回收站 root (SubMenu 型). 可为 null.</param>
        /// <param name="addMenuName">新增项归类桶的名字, 默认 "添加".</param>
        /// <param name="prioritizeAddBucket">true 时把 addBucket 置顶.</param>
        public static MenuNode Merge(
            MenuNode organized,
            MenuNode latest,
            MenuNode trash,
            string addMenuName = "添加",
            bool prioritizeAddBucket = false)
        {
            // 1. 收集黑名单
            var deletedLeafKeys = new HashSet<string>();
            var deletedFolderNames = new HashSet<string>();
            if (trash != null)
            {
                MenuTree.CollectLeafKeys(trash, deletedLeafKeys);
                CollectDeletedFolderNamesTopLevel(trash, deletedFolderNames);
            }

            // 2. 起点树: organized 深拷贝. 如果 organized 为空则从 latest 起 fresh (第一次运行)
            MenuNode result;
            if (organized == null || organized.Children.Count == 0)
            {
                // fresh: 直接深拷贝 latest, 但过滤 trash 黑名单
                result = latest != null ? FilterTrashed(latest, deletedLeafKeys, deletedFolderNames)
                                        : NewRoot();
                NormalizeOverflow(result);
                return result;
            }
            else
            {
                result = organized.Clone();
            }

            // 3. 收集 organized 里已知的 leaf key 和 folder name
            var knownLeafKeys = new HashSet<string>();
            var knownFolderNames = new HashSet<string>();
            MenuTree.CollectLeafKeys(result, knownLeafKeys);
            MenuTree.CollectFolderNames(result, knownFolderNames);

            // 4. Trash 里的 leaf key 也算 "已知" (永不复活)
            foreach (var k in deletedLeafKeys) knownLeafKeys.Add(k);
            // Trash 里的 folder name 算 "永远不合并"
            foreach (var n in deletedFolderNames) knownFolderNames.Add(n);

            // 5. 从 latest diff 出真正新增的 leaf + folder
            var addedLeaves = new List<MenuNode>();
            var addedFolders = new List<MenuNode>();
            if (latest != null)
                CollectAddedNodes(latest, knownLeafKeys, knownFolderNames,
                                  addedLeaves, addedFolders);

            // 6. 递归 merge 同名 folder (organized 里已有 "服装", latest 里也有 "服装" -> 内容合并)
            if (latest != null)
                MergeSameFolders(result, latest, deletedLeafKeys, deletedFolderNames);

            // 7. 新增 leaf 塞进 addBucket
            if (addedLeaves.Count > 0)
            {
                var bucket = EnsureAddBucket(result, addMenuName, prioritizeAddBucket);
                foreach (var leaf in addedLeaves)
                    bucket.Children.Add(leaf.Clone());
            }

            // 8. 新增 folder 直接挂 root
            foreach (var folder in addedFolders)
            {
                if (result.Children.Count < MaxCapacity)
                    result.Children.Add(folder.Clone());
                else
                    result.Children.Add(folder.Clone());  // 让 NormalizeOverflow 处理
            }

            // 9. 溢出折 more
            NormalizeOverflow(result);
            return result;
        }

        // ---------- helpers ----------

        internal static MenuNode NewRoot()
        {
            return new MenuNode
            {
                Name = "Root",
                Type = MenuControlType.SubMenu,
            };
        }

        /// <summary>
        /// Trash 只算 top-level 的 SubMenu 名字, 不递归. 因为回收站里的 folder
        /// 层级是用户扔进来的时刻的层级, 只有顶层 name 有 "永不复活" 语义.
        /// </summary>
        internal static void CollectDeletedFolderNamesTopLevel(
            MenuNode trash, HashSet<string> outNames)
        {
            if (trash == null) return;
            foreach (var c in trash.Children)
            {
                if (c.IsSubMenu && !string.IsNullOrEmpty(c.Name))
                    outNames.Add(c.Name);
            }
        }

        /// <summary>
        /// 深拷贝 tree, 过滤掉 trash 黑名单里的 leaf 和 folder.
        /// </summary>
        internal static MenuNode FilterTrashed(
            MenuNode src, HashSet<string> deletedLeafKeys, HashSet<string> deletedFolderNames)
        {
            if (src == null) return null;
            var copy = new MenuNode
            {
                Name = src.Name,
                Type = src.Type,
                ParameterName = src.ParameterName,
                ParameterValue = src.ParameterValue,
                IconGuid = src.IconGuid,
                SubMenuAssetGuid = src.SubMenuAssetGuid,
                IsAddBucket = src.IsAddBucket,
                AutoMore = src.AutoMore,
                SubParameterNames = new List<string>(src.SubParameterNames ?? new List<string>()),
            };
            foreach (var child in src.Children)
            {
                if (child.IsSubMenu)
                {
                    if (deletedFolderNames.Contains(child.Name)) continue;
                    var subCopy = FilterTrashed(child, deletedLeafKeys, deletedFolderNames);
                    if (subCopy != null) copy.Children.Add(subCopy);
                }
                else
                {
                    if (deletedLeafKeys.Contains(child.ItemKey())) continue;
                    copy.Children.Add(child.Clone());
                }
            }
            return copy;
        }

        /// <summary>
        /// 遍历 latest tree, 找出 knownLeafKeys / knownFolderNames 里没有的项.
        /// 分两桶: addedLeaves = 新叶子, addedFolders = 新 folder.
        /// 只在 root 层做 diff (子 folder 的内容 diff 由 MergeSameFolders 递归处理).
        /// </summary>
        internal static void CollectAddedNodes(
            MenuNode latestRoot,
            HashSet<string> knownLeafKeys,
            HashSet<string> knownFolderNames,
            List<MenuNode> addedLeaves,
            List<MenuNode> addedFolders)
        {
            if (latestRoot == null) return;
            foreach (var c in latestRoot.Children)
            {
                if (c.IsSubMenu)
                {
                    if (!knownFolderNames.Contains(c.Name))
                        addedFolders.Add(c);
                    // 已知 folder 的内容 diff 交给 MergeSameFolders
                }
                else
                {
                    if (!knownLeafKeys.Contains(c.ItemKey()))
                        addedLeaves.Add(c);
                }
            }
        }

        /// <summary>
        /// 递归 merge organized 和 latest 里同名的子 folder.
        /// organized 已有的排序保留, latest 里新增的项按同规则塞进对应子 folder 或 addBucket.
        /// </summary>
        internal static void MergeSameFolders(
            MenuNode organizedRoot,
            MenuNode latestRoot,
            HashSet<string> deletedLeafKeys,
            HashSet<string> deletedFolderNames)
        {
            // 建 organized 的 folder name -> node 索引
            var orgFolders = new Dictionary<string, MenuNode>();
            foreach (var c in organizedRoot.Children)
            {
                if (c.IsSubMenu && !string.IsNullOrEmpty(c.Name)
                    && !orgFolders.ContainsKey(c.Name))
                    orgFolders[c.Name] = c;
            }

            foreach (var latestChild in latestRoot.Children)
            {
                if (!latestChild.IsSubMenu) continue;
                if (deletedFolderNames.Contains(latestChild.Name)) continue;
                if (!orgFolders.TryGetValue(latestChild.Name, out var orgSub))
                    continue;  // 新 folder 由 CollectAddedNodes 处理

                // 递归 merge 内部
                var subKnownLeaf = new HashSet<string>();
                var subKnownFolder = new HashSet<string>();
                MenuTree.CollectLeafKeys(orgSub, subKnownLeaf);
                MenuTree.CollectFolderNames(orgSub, subKnownFolder);
                foreach (var k in deletedLeafKeys) subKnownLeaf.Add(k);
                foreach (var n in deletedFolderNames) subKnownFolder.Add(n);

                foreach (var innerLatest in latestChild.Children)
                {
                    if (innerLatest.IsSubMenu)
                    {
                        if (deletedFolderNames.Contains(innerLatest.Name)) continue;
                        if (!subKnownFolder.Contains(innerLatest.Name))
                            orgSub.Children.Add(innerLatest.Clone());
                    }
                    else
                    {
                        var key = innerLatest.ItemKey();
                        if (!subKnownLeaf.Contains(key))
                            orgSub.Children.Add(innerLatest.Clone());
                    }
                }
                // 递归到孙 folder
                MergeSameFolders(orgSub, latestChild, deletedLeafKeys, deletedFolderNames);
            }
        }

        /// <summary>
        /// 找现有 addBucket 或创建一个. addBucket 是 IsAddBucket=true 的 SubMenu.
        /// </summary>
        internal static MenuNode EnsureAddBucket(
            MenuNode root, string name, bool prioritize)
        {
            foreach (var c in root.Children)
            {
                if (c.IsSubMenu && c.IsAddBucket)
                    return c;
            }
            // 或者按名字匹配
            foreach (var c in root.Children)
            {
                if (c.IsSubMenu && c.Name == name)
                {
                    c.IsAddBucket = true;
                    return c;
                }
            }
            var bucket = new MenuNode
            {
                Name = name,
                Type = MenuControlType.SubMenu,
                IsAddBucket = true,
                AutoMore = true,
            };
            if (prioritize)
                root.Children.Insert(0, bucket);
            else
                root.Children.Add(bucket);
            return bucket;
        }

        /// <summary>
        /// 递归处理溢出: 每层 > MaxCapacity, 把多的项塞进 "more" 子菜单.
        /// more 也超时递归塞下一层 more.
        /// </summary>
        public static void NormalizeOverflow(MenuNode root)
        {
            if (root == null || !root.IsSubMenu) return;
            // 先递归子 folder
            foreach (var c in root.Children)
            {
                if (c.IsSubMenu) NormalizeOverflow(c);
            }
            // 本层溢出
            if (root.Children.Count <= MaxCapacity) return;
            if (!root.AutoMore) return;  // 用户手动关掉了

            // Overflow: 保留前 MaxCapacity-1 项, 剩下的塞 more (more 占最后 1 格)
            var moved = new List<MenuNode>();
            int keep = MaxCapacity - 1;
            for (int i = root.Children.Count - 1; i >= keep; i--)
            {
                moved.Insert(0, root.Children[i]);
                root.Children.RemoveAt(i);
            }
            var more = new MenuNode
            {
                Name = MoreFolderName,
                Type = MenuControlType.SubMenu,
                AutoMore = true,
            };
            more.Children.AddRange(moved);
            root.Children.Add(more);
            // 递归 more 层继续折
            NormalizeOverflow(more);
        }

        /// <summary>
        /// 诊断辅助: 计算某个 root 的容量健康度.
        /// </summary>
        public static bool AnyOverCapacity(MenuNode root)
        {
            if (root == null || !root.IsSubMenu) return false;
            if (root.Children.Count > MaxCapacity) return true;
            foreach (var c in root.Children)
            {
                if (c.IsSubMenu && AnyOverCapacity(c)) return true;
            }
            return false;
        }
    }
}
