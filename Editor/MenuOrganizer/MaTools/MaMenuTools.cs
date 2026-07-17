// ma_menu/* 7 个写 + 只读 tool (M4-A 剩下部分)
// create_folder / set_parent / set_field / set_installer / set_merge_order /
// delete_item / read_target_menu

#if YORIMASHI_MENU_ORGANIZER_ENABLED
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using Yorimashi.Modder.Editor;
using Yorimashi.Modder.Editor.Diagnostics;

namespace Yorimashi.Modder.Editor.MenuOrganizer.MaTools
{
    // ---------- 共享 write tool base (MutatesAsset=false, MA 组件是 instance state) ----------
    public abstract class MaMenuWriteToolBase : YorimashiWriteToolBase
    {
        protected override bool MutatesAsset => false;

        /// <summary>子类实现具体操作, 返回 (summary, changeItem).</summary>
        protected abstract (string summary, Dictionary<string, object> changeItem) ApplyMaOp(
            Dictionary<string, object> parsed);

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            using (Diag.Step("check_ma_available"))
            {
                if (!MaReflection.MaAvailable)
                {
                    Diag.Hint(1.0, "客户项目未装 Modular Avatar");
                    throw new InvalidOperationException("Modular Avatar not installed");
                }
            }
            // preview 只算 changes description, 不 mutate; 真正应用在 ApplyChanges
            parsed["_previewOnly"] = true;
            var (summary, item) = ApplyMaOp(parsed);
            parsed.Remove("_previewOnly");
            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>> { item },
                BackupPath = null,
                Summary = summary,
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            var (summary, item) = ApplyMaOp(parsed);
            return item;
        }

        protected static GameObject ResolveGameObject(string path, string paramName = "path")
        {
            if (string.IsNullOrEmpty(path))
                throw new ArgumentException("missing '" + paramName + "'");
            var go = GameObject.Find(path);
            if (go == null)
            {
                Diag.Hint(0.9, "GameObject 未找到, path 拼写或场景未加载", "先跑 hierarchy/find_by_name");
                throw new InvalidOperationException(paramName + " not found: " + path);
            }
            return go;
        }
    }

    // ==============================================================
    // 2) ma_menu/create_folder — 建 GameObject + MA MenuItem(SubMenu)
    // ==============================================================
    public class MaMenuCreateFolderTool : MaMenuWriteToolBase
    {
        public override string ToolName => "ma_menu/create_folder";

        protected override (string, Dictionary<string, object>) ApplyMaOp(Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.create_folder"))
            {
                if (!parsed.TryGetValue("parentPath", out var pv) || !(pv is string ps))
                {
                    Diag.Hint(1.0, "缺 parentPath");
                    throw new ArgumentException("missing 'parentPath'");
                }
                if (!parsed.TryGetValue("folderName", out var fn) || !(fn is string fns))
                {
                    Diag.Hint(1.0, "缺 folderName");
                    throw new ArgumentException("missing 'folderName'");
                }

                bool previewOnly = parsed.ContainsKey("_previewOnly");
                var parent = ResolveGameObject(ps, "parentPath");

                if (previewOnly)
                {
                    return ("preview: create " + fns + " under " + ps,
                            new Dictionary<string, object>
                            {
                                ["op"] = "create_folder",
                                ["parent"] = ps,
                                ["folderName"] = fns,
                            });
                }

                var go = new GameObject(fns);
                Undo.RegisterCreatedObjectUndo(go, "create MA menu folder");
                go.transform.SetParent(parent.transform, worldPositionStays: false);

                var menuItem = Undo.AddComponent(go, MaReflection.MenuItemType);
                // 设置 SubMenu type + Children source
                var control = MaReflection.GetMember(menuItem, "Control");
                if (control != null)
                {
                    control.GetType().GetField("type").SetValue(control, 2);   // SubMenu
                }
                // MenuSource = Children (1)
                MaReflection.SetMember(menuItem, "MenuSource", 1);

                return ("create MA folder " + fns + " under " + ps,
                        new Dictionary<string, object>
                        {
                            ["createdPath"] = ps + "/" + fns,
                            ["controlType"] = "SubMenu",
                        });
            }
        }
    }

    // ==============================================================
    // 3) ma_menu/set_parent — 移动 MenuItem GameObject 到另一个父菜单
    // ==============================================================
    public class MaMenuSetParentTool : MaMenuWriteToolBase
    {
        public override string ToolName => "ma_menu/set_parent";

        protected override (string, Dictionary<string, object>) ApplyMaOp(Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.set_parent"))
            {
                var itemPath = (parsed.TryGetValue("itemPath", out var iv) && iv is string ipv) ? ipv : null;
                var newParent = (parsed.TryGetValue("newParentPath", out var nv) && nv is string nps) ? nps : null;
                if (string.IsNullOrEmpty(itemPath))
                    throw new ArgumentException("missing 'itemPath'");
                if (string.IsNullOrEmpty(newParent))
                    throw new ArgumentException("missing 'newParentPath'");

                bool previewOnly = parsed.ContainsKey("_previewOnly");
                var item = ResolveGameObject(itemPath, "itemPath");
                var parent = ResolveGameObject(newParent, "newParentPath");

                if (previewOnly)
                    return ("preview: move " + itemPath + " → " + newParent,
                            new Dictionary<string, object>
                            {
                                ["op"] = "set_parent",
                                ["item"] = itemPath,
                                ["newParent"] = newParent,
                            });

                Undo.SetTransformParent(item.transform, parent.transform, "set MA menu parent");
                return ("moved " + itemPath + " → " + newParent,
                        new Dictionary<string, object>
                        {
                            ["item"] = itemPath,
                            ["newParent"] = newParent,
                        });
            }
        }
    }

    // ==============================================================
    // 4) ma_menu/set_field — 改 MA MenuItem 字段
    // fields (dict, optional): { name, parameter, value, ... }
    // ==============================================================
    public class MaMenuSetFieldTool : MaMenuWriteToolBase
    {
        public override string ToolName => "ma_menu/set_field";

        protected override (string, Dictionary<string, object>) ApplyMaOp(Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.set_field"))
            {
                var path = (parsed.TryGetValue("path", out var pv) && pv is string ps) ? ps : null;
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("missing 'path'");
                var go = ResolveGameObject(path);
                var menuItem = go.GetComponent(MaReflection.MenuItemType);
                if (menuItem == null)
                {
                    Diag.Hint(0.9, "该 GameObject 未挂 MA MenuItem", "先跑 ma_menu/create_folder 或换 path");
                    throw new InvalidOperationException("no MA MenuItem on: " + path);
                }

                if (!parsed.TryGetValue("fields", out var fv) || !(fv is Dictionary<string, object> fields))
                    throw new ArgumentException("missing 'fields' dict");

                bool previewOnly = parsed.ContainsKey("_previewOnly");
                if (!previewOnly)
                {
                    Undo.RecordObject((UnityEngine.Object)menuItem, "set MA menu field");
                }

                var applied = new List<string>();
                var control = MaReflection.GetMember(menuItem, "Control");
                foreach (var kv in fields)
                {
                    // 顶层字段: Automatic / MenuSource / menuSource_otherObjectChildren / MergeOrder
                    if (MaReflection.SetMember(menuItem, kv.Key, kv.Value))
                    {
                        applied.Add(kv.Key + "=(top)");
                        continue;
                    }
                    // Control 内字段: name / type / parameter / value
                    if (control != null && MaReflection.SetMember(control, kv.Key, kv.Value))
                    {
                        applied.Add(kv.Key + "=(control)");
                        continue;
                    }
                    Diag.Hint(0.3, "字段名 " + kv.Key + " 未在 MenuItem/Control 上找到, 已跳过");
                }

                if (!previewOnly)
                    EditorUtility.SetDirty((UnityEngine.Object)menuItem);

                return ("set_field " + path + " (" + applied.Count + " applied)",
                        new Dictionary<string, object>
                        {
                            ["path"] = path,
                            ["fieldsApplied"] = applied,
                        });
            }
        }
    }

    // ==============================================================
    // 5) ma_menu/set_installer — 改 MenuInstaller.installTargetMenu
    // ==============================================================
    public class MaMenuSetInstallerTool : MaMenuWriteToolBase
    {
        public override string ToolName => "ma_menu/set_installer";

        protected override (string, Dictionary<string, object>) ApplyMaOp(Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.set_installer"))
            {
                var path = (parsed.TryGetValue("path", out var pv) && pv is string ps) ? ps : null;
                var assetPath = (parsed.TryGetValue("targetMenuAssetPath", out var av) && av is string aps) ? aps : null;
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("missing 'path'");

                bool previewOnly = parsed.ContainsKey("_previewOnly");
                var go = ResolveGameObject(path);
                var installer = go.GetComponent(MaReflection.MenuInstallerType);
                if (installer == null)
                {
                    Diag.Hint(0.9, "该 GameObject 未挂 MA MenuInstaller");
                    throw new InvalidOperationException("no MA MenuInstaller on: " + path);
                }

                VRCExpressionsMenu asset = null;
                if (!string.IsNullOrEmpty(assetPath))
                {
                    asset = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(assetPath);
                    if (asset == null)
                    {
                        Diag.Hint(0.85, "targetMenuAssetPath 找不到对应 VRCExpressionsMenu asset");
                        throw new InvalidOperationException("target menu asset not found: " + assetPath);
                    }
                }

                if (!previewOnly)
                {
                    Undo.RecordObject((UnityEngine.Object)installer, "set MenuInstaller target");
                    MaReflection.SetMember(installer, "installTargetMenu", asset);
                    EditorUtility.SetDirty((UnityEngine.Object)installer);
                }
                return ("set_installer " + path + " → " + (assetPath ?? "null"),
                        new Dictionary<string, object>
                        {
                            ["path"] = path,
                            ["targetMenuAssetPath"] = assetPath,
                        });
            }
        }
    }

    // ==============================================================
    // 6) ma_menu/set_merge_order — 改 MenuItem.MergeOrder
    // ==============================================================
    public class MaMenuSetMergeOrderTool : MaMenuWriteToolBase
    {
        public override string ToolName => "ma_menu/set_merge_order";

        protected override (string, Dictionary<string, object>) ApplyMaOp(Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.set_merge_order"))
            {
                var path = (parsed.TryGetValue("path", out var pv) && pv is string ps) ? ps : null;
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("missing 'path'");
                int newOrder;
                if (parsed.TryGetValue("newOrder", out var nv))
                {
                    if (nv is int ni) newOrder = ni;
                    else if (nv is long nl) newOrder = (int)nl;
                    else if (nv is double nd) newOrder = (int)nd;
                    else if (nv is string ns && int.TryParse(ns, out var np)) newOrder = np;
                    else throw new ArgumentException("newOrder must be number");
                }
                else throw new ArgumentException("missing 'newOrder'");

                bool previewOnly = parsed.ContainsKey("_previewOnly");
                var go = ResolveGameObject(path);
                var menuItem = go.GetComponent(MaReflection.MenuItemType);
                if (menuItem == null)
                {
                    Diag.Hint(0.9, "该 GameObject 未挂 MA MenuItem");
                    throw new InvalidOperationException("no MA MenuItem on: " + path);
                }

                if (!previewOnly)
                {
                    Undo.RecordObject((UnityEngine.Object)menuItem, "set MergeOrder");
                    MaReflection.SetMember(menuItem, "MergeOrder", newOrder);
                    EditorUtility.SetDirty((UnityEngine.Object)menuItem);
                }
                return ("set_merge_order " + path + " → " + newOrder,
                        new Dictionary<string, object>
                        {
                            ["path"] = path,
                            ["mergeOrder"] = newOrder,
                        });
            }
        }
    }

    // ==============================================================
    // 7) ma_menu/delete_item — 删 MA MenuItem 组件 (不删 GameObject)
    // ==============================================================
    public class MaMenuDeleteItemTool : MaMenuWriteToolBase
    {
        public override string ToolName => "ma_menu/delete_item";

        protected override (string, Dictionary<string, object>) ApplyMaOp(Dictionary<string, object> parsed)
        {
            using (Diag.Step("apply.delete_item"))
            {
                var path = (parsed.TryGetValue("path", out var pv) && pv is string ps) ? ps : null;
                if (string.IsNullOrEmpty(path)) throw new ArgumentException("missing 'path'");
                bool previewOnly = parsed.ContainsKey("_previewOnly");
                var go = ResolveGameObject(path);
                var menuItem = go.GetComponent(MaReflection.MenuItemType);
                if (menuItem == null)
                {
                    Diag.Hint(0.9, "该 GameObject 未挂 MA MenuItem, 无需删");
                    throw new InvalidOperationException("no MA MenuItem on: " + path);
                }

                if (!previewOnly)
                    Undo.DestroyObjectImmediate((UnityEngine.Object)menuItem);
                return ("delete_item " + path,
                        new Dictionary<string, object>
                        {
                            ["path"] = path,
                            ["deleted"] = true,
                        });
            }
        }
    }

    // ==============================================================
    // 8) ma_menu/read_target_menu — 只读: 读 MenuInstaller 指向的 VRCExpressionsMenu 内容
    // ==============================================================
    public class MaMenuReadTargetMenuTool : YorimashiReadToolBase
    {
        public override string ToolName => "ma_menu/read_target_menu";

        protected override Dictionary<string, object> BuildResult(Dictionary<string, object> parsed)
        {
            using (Diag.Step("check_ma"))
            {
                if (!MaReflection.MaAvailable)
                {
                    Diag.Hint(1.0, "MA 未装");
                    throw new InvalidOperationException("Modular Avatar not installed");
                }
            }
            var path = (parsed.TryGetValue("path", out var pv) && pv is string ps) ? ps : null;
            if (string.IsNullOrEmpty(path)) throw new ArgumentException("missing 'path'");

            var go = GameObject.Find(path);
            if (go == null) throw new InvalidOperationException("not found: " + path);
            var installer = go.GetComponent(MaReflection.MenuInstallerType);
            if (installer == null)
            {
                Diag.Hint(0.9, "该 GameObject 未挂 MA MenuInstaller");
                throw new InvalidOperationException("no MA MenuInstaller on: " + path);
            }

            var target = MaReflection.GetMember(installer, "installTargetMenu") as VRCExpressionsMenu;
            var root = MenuVRCBridge.FromVRCMenu(target, "TargetMenu");
            return new Dictionary<string, object>
            {
                ["installerPath"] = path,
                ["targetAssetPath"] = target != null ? AssetDatabase.GetAssetPath(target) : null,
                ["treeJson"] = MenuNodeJson.Serialize(root),
                ["empty"] = target == null,
            };
        }
    }
}
#endif
