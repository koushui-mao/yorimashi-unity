// Yorimashi Type Resolver — M3-E-P4 helper
//
// short name (e.g. "BoxCollider") 或 FQN (e.g. "UnityEngine.BoxCollider")
// → 解析到 System.Type，可选加约束（比如必须是 Component 子类）。
//
// 借鉴 CoplayDev/unity-mcp 的 UnityTypeResolver 但精简：
//   1. 优先 Type.GetType（含 FQN + 常见 assembly hint）
//   2. Fallback：扫 AppDomain 所有 loaded assemblies，找类名匹配
//   3. Player-first 优先级：UnityEngine.* 优于 UnityEditor.*
//   4. TypeCache（Editor API）作为最后 fallback，扫 Component 全谱系
//
// 使用：
//   if (YorimashiTypeResolver.TryResolveComponent("BoxCollider", out var t, out var err)) {...}
//
// Sentinel: "M3-E-P4 TYPE RESOLVER"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal static class YorimashiTypeResolver
    {
        public const string SentinelTag = "M3-E-P4 TYPE RESOLVER";

        // 常见 Unity/编辑器 assembly，加速命中率
        private static readonly string[] CommonAssemblyHints = new[]
        {
            "UnityEngine",
            "UnityEngine.CoreModule",
            "UnityEngine.PhysicsModule",
            "UnityEngine.Physics2DModule",
            "UnityEngine.AnimationModule",
            "UnityEngine.UIModule",
            "UnityEngine.UI",
            "UnityEngine.AudioModule",
            "UnityEngine.ParticleSystemModule",
            "UnityEngine.VideoModule",
            "UnityEngine.TerrainModule",
            "UnityEditor",
        };

        /// <summary>
        /// 尝试把 typeName 解析成 Component 子类。null-safe。
        /// short name (e.g. "BoxCollider") / FQN ("UnityEngine.BoxCollider") 都接受。
        /// </summary>
        public static bool TryResolveComponent(string typeName, out Type type, out string error)
        {
            return TryResolve(typeName, typeof(Component), out type, out error);
        }

        /// <summary>
        /// 通用版本：约束 constraintBase（可为 null 表示不约束）。
        /// </summary>
        public static bool TryResolve(string typeName, Type constraintBase, out Type type, out string error)
        {
            type = null;
            error = null;

            if (string.IsNullOrEmpty(typeName))
            {
                error = "typeName is empty";
                return false;
            }

            // 1. 直接 GetType，处理 AssemblyQualified 和 FQN
            var t = Type.GetType(typeName, throwOnError: false);
            if (t != null && CheckConstraint(t, constraintBase, out error))
            {
                type = t;
                return true;
            }

            // 2. 用 assembly hint 拼一遍
            foreach (var asm in CommonAssemblyHints)
            {
                var candidate = typeName.Contains(".")
                    ? $"{typeName}, {asm}"
                    : $"UnityEngine.{typeName}, {asm}";
                var t2 = Type.GetType(candidate, throwOnError: false);
                if (t2 == null && !typeName.Contains("."))
                {
                    t2 = Type.GetType($"UnityEditor.{typeName}, {asm}", throwOnError: false);
                }
                if (t2 != null && CheckConstraint(t2, constraintBase, out error))
                {
                    type = t2;
                    return true;
                }
            }

            // 3. 全 AppDomain 扫描，Player-first
            var allTypes = new List<Type>();
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] asmTypes;
                try { asmTypes = asm.GetTypes(); }
                catch (ReflectionTypeLoadException e) { asmTypes = e.Types.Where(x => x != null).ToArray(); }
                catch { continue; }
                foreach (var candidate in asmTypes)
                {
                    if (candidate == null) continue;
                    if (candidate.FullName == typeName || candidate.Name == typeName)
                        allTypes.Add(candidate);
                }
            }

            // 优先级：约束匹配 > FullName 精确 > UnityEngine 命名空间优先
            var filtered = constraintBase == null
                ? allTypes
                : allTypes.Where(x => constraintBase.IsAssignableFrom(x)).ToList();

            if (filtered.Count == 0 && allTypes.Count > 0)
            {
                error = "type '" + typeName + "' found but not assignable to " + constraintBase?.FullName;
                return false;
            }

            if (filtered.Count == 0)
            {
                // 4. TypeCache fallback（Editor-only, Component 谱系）
                if (constraintBase == typeof(Component))
                {
                    var derived = TypeCache.GetTypesDerivedFrom<Component>();
                    foreach (var c in derived)
                    {
                        if (c == null) continue;
                        if (c.FullName == typeName || c.Name == typeName)
                            filtered.Add(c);
                    }
                }
            }

            if (filtered.Count == 0)
            {
                error = "type '" + typeName + "' not found in any loaded assembly";
                return false;
            }

            if (filtered.Count == 1)
            {
                type = filtered[0];
                return true;
            }

            // 多选：优先 UnityEngine.* 命名空间
            var unityEnginePick = filtered.FirstOrDefault(x =>
                x.Namespace != null && x.Namespace.StartsWith("UnityEngine"));
            if (unityEnginePick != null)
            {
                type = unityEnginePick;
                return true;
            }

            error = "type '" + typeName + "' is ambiguous (matches " + filtered.Count
                  + " types: " + string.Join(", ", filtered.Select(x => x.FullName).Take(5)) + " ...). "
                  + "Pass a fully-qualified name (e.g. 'Namespace.TypeName').";
            return false;
        }

        private static bool CheckConstraint(Type t, Type constraintBase, out string error)
        {
            error = null;
            if (constraintBase == null) return true;
            if (!constraintBase.IsAssignableFrom(t))
            {
                error = "type '" + t.FullName + "' is not assignable to '" + constraintBase.FullName + "'";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 在 target Component 的类型链里找带 [SerializeField] 的 NonPublic 字段。
        /// </summary>
        public static FieldInfo FindSerializedFieldInHierarchy(Type type, string fieldName)
        {
            if (type == null || string.IsNullOrEmpty(fieldName)) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var current = type;
            while (current != null && current != typeof(object))
            {
                var f = current.GetField(fieldName, flags);
                if (f != null)
                {
                    if (f.IsPublic) return f;
                    if (Attribute.IsDefined(f, typeof(SerializeField), true)) return f;
                }
                current = current.BaseType;
            }
            return null;
        }

        /// <summary>
        /// 找 property (public/nonpublic instance)。
        /// </summary>
        public static PropertyInfo FindPropertyInHierarchy(Type type, string propName)
        {
            if (type == null || string.IsNullOrEmpty(propName)) return null;
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var current = type;
            while (current != null && current != typeof(object))
            {
                var p = current.GetProperty(propName, flags);
                if (p != null && p.CanRead && p.CanWrite) return p;
                current = current.BaseType;
            }
            return null;
        }
    }
}
