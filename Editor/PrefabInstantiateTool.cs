// Yorimashi prefab/instantiate — M3-E-P4 真改动 tool
//
// 把 Prefab asset 实例化到 scene 里（走 PrefabUtility.InstantiatePrefab
// 保留 prefab 链接），可指定 parent + local/world transform。
//
// 参数：
//   prefabPath (string, required) — "Assets/..." 或 bare name (唯一匹配)
//   parentPath (string, required) — 必须在白名单沙盒下 (scope guard)
//   name       (string, optional) — 覆盖新 GO 名字，默认沿用 prefab asset 名
//   position   ({x,y,z}, optional, default 0/0/0)
//   rotation   ({x,y,z}, optional, Euler 度, default 0/0/0)
//   scale      ({x,y,z}, optional, default 1/1/1)
//   space      ("local"|"world", default local)
//   dry_run    (bool, optional, default true)
//
// 4 段式：
//   dry_run  → 解析 prefab asset + parent + 冲突检查 → 报"打算实例化什么在哪"
//   apply    → PrefabUtility.InstantiatePrefab + Undo.RegisterCreatedObjectUndo + 设 transform
//   verify   → PrefabUtility.IsPartOfAnyPrefab + GetCorrespondingObjectFromSource
//   restore  → 单一 Undo group, Ctrl+Z 一次撤销
//
// 关键决策：
//   - 用 PrefabUtility.InstantiatePrefab（保留链接），拒绝 Object.Instantiate
//   - parentPath 必填（scope guard 唯一落点）
//   - 平级重名拒绝（避免 hierarchy path resolver 撞车）
//   - parent 在 PrefabStage 里 → 拒绝
//
// Sentinel: "M3-E-P4 PREFAB INSTANTIATE"
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal class PrefabInstantiateTool : YorimashiWriteToolBase
    {
        public const string SentinelTag = "M3-E-P4 PREFAB INSTANTIATE";

        public override string ToolName => "prefab/instantiate";

        // scope guard 走 parentPath 而不是 path
        protected override bool AllowWriteOnTarget(Dictionary<string, object> parsed, out string reason)
        {
            string parentPath = null;
            if (parsed != null && parsed.TryGetValue("parentPath", out var p) && p is string ps)
                parentPath = ps;
            if (string.IsNullOrEmpty(parentPath))
            {
                reason = "missing required 'parentPath' — prefab/instantiate must nest under a whitelisted sandbox parent (Ramune_test/, WriteTest/, Yorimashi_WriteTest/).";
                return false;
            }
            string[] allowlist = { "Ramune_test/", "WriteTest/", "Yorimashi_WriteTest/", "Ramune_test", "WriteTest", "Yorimashi_WriteTest" };
            foreach (var prefix in allowlist)
            {
                if (parentPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    reason = null;
                    return true;
                }
            }
            reason = "parentPath '" + parentPath + "' not under a whitelisted sandbox root. "
                   + "Allowed prefixes: Ramune_test/, WriteTest/, Yorimashi_WriteTest/.";
            return false;
        }

        private GameObject _prefabAsset;
        private GameObject _parentGo;
        private string _newName;
        private Vector3 _position;
        private Vector3 _rotation;
        private Vector3 _scale;
        private string _space;

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            if (!parsed.TryGetValue("prefabPath", out var pfObj) || !(pfObj is string prefabPath) || string.IsNullOrEmpty(prefabPath))
                throw new ArgumentException("missing required 'prefabPath' (string)");
            if (!parsed.TryGetValue("parentPath", out var ppObj) || !(ppObj is string parentPath) || string.IsNullOrEmpty(parentPath))
                throw new ArgumentException("missing required 'parentPath' (string)");

            string resolvedAssetPath;
            if (prefabPath.Contains("/") || prefabPath.EndsWith(".prefab"))
            {
                resolvedAssetPath = prefabPath.EndsWith(".prefab") ? prefabPath : prefabPath + ".prefab";
            }
            else
            {
                // bare name → AssetDatabase.FindAssets 唯一匹配
                var guids = AssetDatabase.FindAssets("t:Prefab " + prefabPath);
                var matches = new List<string>();
                foreach (var g in guids)
                {
                    var p = AssetDatabase.GUIDToAssetPath(g);
                    if (Path.GetFileNameWithoutExtension(p) == prefabPath)
                        matches.Add(p);
                }
                if (matches.Count == 0)
                    throw new InvalidOperationException("prefab '" + prefabPath + "' not found in project");
                if (matches.Count > 1)
                    throw new InvalidOperationException("prefab name '" + prefabPath + "' is ambiguous: matches "
                        + string.Join(", ", matches) + ". Pass a full 'Assets/...' path.");
                resolvedAssetPath = matches[0];
            }

            _prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(resolvedAssetPath);
            if (_prefabAsset == null)
                throw new InvalidOperationException("failed to load prefab at '" + resolvedAssetPath + "'");

            var assetType = PrefabUtility.GetPrefabAssetType(_prefabAsset);
            if (assetType == PrefabAssetType.NotAPrefab)
                throw new InvalidOperationException("asset at '" + resolvedAssetPath + "' is not a prefab");

            _parentGo = ComponentAddTool.ResolveGameObjectPath(parentPath);
            if (_parentGo == null)
                throw new InvalidOperationException("parent GameObject not found at path: " + parentPath);

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && _parentGo.scene == stage.scene)
                throw new InvalidOperationException(
                    "parent is inside Prefab Stage; refusing to nest a new prefab instance into a prefab asset. "
                  + "Close the stage or pick a scene parent.");

            _newName = _prefabAsset.name;
            if (parsed.TryGetValue("name", out var nObj) && nObj is string ns && !string.IsNullOrEmpty(ns))
                _newName = ns;

            // 平级重名拒绝
            for (int i = 0; i < _parentGo.transform.childCount; i++)
            {
                var child = _parentGo.transform.GetChild(i);
                if (child.name == _newName)
                    throw new InvalidOperationException(
                        "parent '" + parentPath + "' already has a child named '" + _newName + "'. "
                      + "Pass a different 'name' or remove the existing child first.");
            }

            _position = ReadVec3(parsed, "position", Vector3.zero);
            _rotation = ReadVec3(parsed, "rotation", Vector3.zero);
            _scale = ReadVec3(parsed, "scale", Vector3.one);
            _space = "local";
            if (parsed.TryGetValue("space", out var sObj) && sObj is string ss)
            {
                if (ss != "local" && ss != "world")
                    throw new ArgumentException("space must be 'local' or 'world'");
                _space = ss;
            }

            var item = new Dictionary<string, object>
            {
                { "op", "prefab_instantiate" },
                { "prefabPath", resolvedAssetPath },
                { "prefabAssetType", assetType.ToString() },
                { "parentPath", parentPath },
                { "newGameObjectName", _newName },
                { "position", new Dictionary<string, object> { {"x",_position.x},{"y",_position.y},{"z",_position.z} } },
                { "rotation", new Dictionary<string, object> { {"x",_rotation.x},{"y",_rotation.y},{"z",_rotation.z} } },
                { "scale",    new Dictionary<string, object> { {"x",_scale.x},{"y",_scale.y},{"z",_scale.z} } },
                { "space", _space },
            };

            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>> { item },
                BackupPath = null,
                Summary = "will instantiate '" + resolvedAssetPath + "' as '" + _newName + "' under '" + parentPath + "'",
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            if (_prefabAsset == null || _parentGo == null)
                throw new InvalidOperationException("preview state missing");

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Yorimashi prefab/instantiate " + _newName);

            var newGo = PrefabUtility.InstantiatePrefab(_prefabAsset, _parentGo.transform) as GameObject;
            if (newGo == null)
                throw new InvalidOperationException("PrefabUtility.InstantiatePrefab returned null");

            Undo.RegisterCreatedObjectUndo(newGo, "Yorimashi prefab/instantiate " + _newName);

            if (newGo.name != _newName)
                newGo.name = _newName;

            Undo.RecordObject(newGo.transform, "Yorimashi prefab/instantiate transform");
            if (_space == "world")
            {
                newGo.transform.position = _position;
                newGo.transform.eulerAngles = _rotation;
                newGo.transform.localScale = _scale;   // scale 永远 local
            }
            else
            {
                newGo.transform.localPosition = _position;
                newGo.transform.localEulerAngles = _rotation;
                newGo.transform.localScale = _scale;
            }

            EditorUtility.SetDirty(newGo);
            if (_parentGo.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(_parentGo.scene);

            Undo.CollapseUndoOperations(undoGroup);

            bool isPartOfAny = PrefabUtility.IsPartOfAnyPrefab(newGo);
            var source = PrefabUtility.GetCorrespondingObjectFromSource(newGo);
            bool linkOk = source == _prefabAsset;
            bool parentOk = newGo.transform.parent == _parentGo.transform;

            var resolvedPath = BuildScenePath(newGo);
            return new Dictionary<string, object>
            {
                { "createdInstanceId", newGo.GetInstanceID() },
                { "resolvedPath", resolvedPath },
                { "isPrefabInstance", isPartOfAny },
                { "isPartOfAnyPrefab", isPartOfAny },
                { "prefabLinkOk", linkOk },
                { "parentOk", parentOk },
                { "verified", isPartOfAny && linkOk && parentOk },
            };
        }

        private static Vector3 ReadVec3(Dictionary<string, object> parsed, string key, Vector3 fallback)
        {
            if (!parsed.TryGetValue(key, out var obj)) return fallback;
            if (!(obj is Dictionary<string, object> d)) return fallback;
            return new Vector3(
                GetF(d, "x", fallback.x),
                GetF(d, "y", fallback.y),
                GetF(d, "z", fallback.z));
        }

        private static float GetF(Dictionary<string, object> d, string k, float fb)
        {
            if (!d.TryGetValue(k, out var v)) return fb;
            if (v is float f) return f;
            if (v is double dd) return (float)dd;
            if (v is int i) return (float)i;
            if (v is long l) return (float)l;
            return fb;
        }

        private static string BuildScenePath(GameObject go)
        {
            var stack = new Stack<string>();
            var t = go.transform;
            while (t != null)
            {
                stack.Push(t.name);
                t = t.parent;
            }
            return string.Join("/", stack.ToArray());
        }
    }
}
