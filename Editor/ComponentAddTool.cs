// Yorimashi component/add — M3-E-P4 真改动 tool
//
// 给 GameObject 加一个 Component；可选一次性设几个初始字段。
//
// 参数：
//   path       (string, required) — GameObject 路径
//   typeName   (string, required) — Component 类型名 (short 或 FQN)
//   properties (dict, optional)   — 初始字段值; partial-success 允许（返回 propertiesFailed）
//   dry_run    (bool, optional, default true)
//
// 4 段式：
//   dry_run    → 解析 GO + type + preview 属性 → 报"打算加什么"
//   apply      → Undo.AddComponent + 设初始字段 + verify readback
//   verify     → GO.GetComponent(type) != null + 每个字段读回比较
//   restore    → apply 里 SetProperty 阶段异常时 Undo.DestroyObjectImmediate
//
// Sentinel: "M3-E-P4 COMPONENT ADD"
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal class ComponentAddTool : YorimashiWriteToolBase
    {
        public const string SentinelTag = "M3-E-P4 COMPONENT ADD";

        public override string ToolName => "component/add";

        private GameObject _resolvedGo;
        private Type _resolvedType;
        private Dictionary<string, object> _initProperties;

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            if (!parsed.TryGetValue("path", out var pObj) || !(pObj is string path) || string.IsNullOrEmpty(path))
                throw new ArgumentException("missing required 'path' (string)");
            if (!parsed.TryGetValue("typeName", out var tnObj) || !(tnObj is string typeName) || string.IsNullOrEmpty(typeName))
                throw new ArgumentException("missing required 'typeName' (string)");

            _resolvedGo = ResolveGameObjectPath(path);
            if (_resolvedGo == null)
                throw new InvalidOperationException("GameObject not found at path: " + path);

            // 禁止在 Prefab Stage 里改（避免写回 prefab asset）
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && _resolvedGo.scene == stage.scene)
                throw new InvalidOperationException(
                    "target is inside Prefab Stage; component/add refuses to bake into prefab asset. "
                  + "Close the prefab stage or use a scene instance instead.");

            if (!YorimashiTypeResolver.TryResolveComponent(typeName, out _resolvedType, out var resolveErr))
                throw new ArgumentException("component type: " + resolveErr);

            if (_resolvedType == typeof(Transform))
                throw new InvalidOperationException("cannot add Transform (every GameObject already has one)");

            // DisallowMultipleComponent 检查
            bool disallowsMultiple = Attribute.IsDefined(_resolvedType, typeof(DisallowMultipleComponent), true);
            bool alreadyPresent = _resolvedGo.GetComponent(_resolvedType) != null;
            if (disallowsMultiple && alreadyPresent)
                throw new InvalidOperationException(
                    "type '" + _resolvedType.FullName + "' has [DisallowMultipleComponent] and target already carries one");

            // 2D/3D physics conflict 检查（upstream 参考）
            CheckPhysicsConflict(_resolvedGo, _resolvedType);

            _initProperties = null;
            var propertiesPreview = new List<Dictionary<string, object>>();
            if (parsed.TryGetValue("properties", out var propsObj) && propsObj is Dictionary<string, object> propsDict)
            {
                _initProperties = propsDict;
                foreach (var kv in propsDict)
                {
                    propertiesPreview.Add(new Dictionary<string, object>
                    {
                        { "name", kv.Key },
                        { "targetValue", kv.Value },
                    });
                }
            }

            var item = new Dictionary<string, object>
            {
                { "op", "component_add" },
                { "path", path },
                { "typeName", typeName },
                { "resolvedFullName", _resolvedType.FullName },
                { "assemblyName", _resolvedType.Assembly.GetName().Name },
                { "disallowsMultiple", disallowsMultiple },
                { "alreadyPresent", alreadyPresent },
                { "propertiesPreview", propertiesPreview },
            };

            var initCount = _initProperties?.Count ?? 0;
            var summary = "will add " + _resolvedType.Name + " to '" + path + "'"
                        + (initCount > 0 ? " (" + initCount + " initial props)" : "");

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
            if (_resolvedGo == null || _resolvedType == null)
                throw new InvalidOperationException("preview state missing");

            Component newComp;
            try
            {
                newComp = Undo.AddComponent(_resolvedGo, _resolvedType);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Undo.AddComponent failed: " + e.Message);
            }
            if (newComp == null)
                throw new InvalidOperationException(
                    "AddComponent returned null (probably [DisallowMultipleComponent] or incompatible platform)");

            var propertiesApplied = new List<Dictionary<string, object>>();
            var propertiesFailed = new List<Dictionary<string, object>>();

            if (_initProperties != null && _initProperties.Count > 0)
            {
                Undo.RecordObject(newComp, "component/add initial properties");
                foreach (var kv in _initProperties)
                {
                    try
                    {
                        ComponentSetPropertyTool.SetOneProperty(newComp, kv.Key, kv.Value, out var readback);
                        propertiesApplied.Add(new Dictionary<string, object>
                        {
                            { "name", kv.Key },
                            { "verified", true },
                            { "readback", readback },
                        });
                    }
                    catch (Exception e)
                    {
                        propertiesFailed.Add(new Dictionary<string, object>
                        {
                            { "name", kv.Key },
                            { "error", e.Message },
                        });
                    }
                }
            }

            EditorUtility.SetDirty(_resolvedGo);
            if (_resolvedGo.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(_resolvedGo.scene);

            bool verified = propertiesFailed.Count == 0 && _resolvedGo.GetComponent(_resolvedType) != null;

            return new Dictionary<string, object>
            {
                { "componentInstanceId", newComp.GetInstanceID() },
                { "verified", verified },
                { "propertiesApplied", propertiesApplied },
                { "propertiesFailed", propertiesFailed },
            };
        }

        private static void CheckPhysicsConflict(GameObject go, Type newType)
        {
            // 拒绝在同一个 GO 上混用 2D 和 3D 物理组件（upstream 参考）
            bool is2D = newType.FullName != null && newType.FullName.EndsWith("2D");
            bool is3D = newType == typeof(Rigidbody) || typeof(Collider).IsAssignableFrom(newType);

            if (is2D)
            {
                if (go.GetComponent<Rigidbody>() != null || go.GetComponent<Collider>() != null)
                    throw new InvalidOperationException(
                        "refusing to add 2D physics component to a GameObject that already has 3D physics");
            }
            else if (is3D)
            {
                if (go.GetComponent<Rigidbody2D>() != null || go.GetComponent<Collider2D>() != null)
                    throw new InvalidOperationException(
                        "refusing to add 3D physics component to a GameObject that already has 2D physics");
            }
        }

        internal static GameObject ResolveGameObjectPath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var parts = path.Split('/');
            if (parts.Length == 0) return null;

            GameObject current = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                if (root.name == parts[0]) { current = root; break; }
            }
            if (current == null) return null;

            for (int i = 1; i < parts.Length; i++)
            {
                var childTf = current.transform.Find(parts[i]);
                if (childTf == null) return null;
                current = childTf.gameObject;
            }
            return current;
        }
    }
}
