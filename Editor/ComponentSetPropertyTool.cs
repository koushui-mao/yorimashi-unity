// Yorimashi component/set_property — M3-E-P4 真改动 tool
//
// 改 Component 上单个字段/属性。reflection → SerializedProperty 双通道 fallback。
//
// 参数：
//   path       (string, required) — GameObject 路径
//   typeName   (string, required) — Component 类型（同 GO 有多个时用 index 区分）
//   index      (int, optional, default 0) — 第几个同类 Component
//   property   (string, required) — 字段名
//   value      (any) — JSON 值；数字/布尔/字符串/{x,y,z}/{r,g,b,a} 等
//   dry_run    (bool, optional, default true)
//
// 4 段式：
//   dry_run  → 解析 target + 定位 field → 记 old value
//   apply    → Undo.RecordObject + reflection 或 SerializedProperty 写入
//   verify   → 读回 + 值比较 (float 走 epsilon)
//   restore  → 若写入失败, Undo group 自动回滚 (Ctrl+Z)
//
// 关键决策（upstream 学习）：
//   - UnityEvent 派生类型必须走 SerializedProperty (reflection 会创建断链对象)
//   - Object reference 也走 SerializedProperty (readback 更可靠)
//   - Enum 值支持 int 或 string 输入
//   - Vector2/3/4/Color 支持 {x,y,z} / {r,g,b,a} dict
//   - readback null → 报 "reference did not persist"
//
// Sentinel: "M3-E-P4 COMPONENT SET PROPERTY"
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEditor;
using UnityEditor.Experimental.SceneManagement;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;

namespace Yorimashi.Modder.Editor
{
    internal class ComponentSetPropertyTool : YorimashiWriteToolBase
    {
        public const string SentinelTag = "M3-E-P4 COMPONENT SET PROPERTY";

        public override string ToolName => "component/set_property";

        private GameObject _resolvedGo;
        private Component _target;
        private Type _targetType;
        private string _propertyName;
        private object _targetValue;
        private object _oldValue;
        private string _resolvedVia;      // "reflection" | "serializedProperty"
        private string _fieldTypeName;

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            if (!parsed.TryGetValue("path", out var pObj) || !(pObj is string path) || string.IsNullOrEmpty(path))
                throw new ArgumentException("missing required 'path' (string)");
            if (!parsed.TryGetValue("typeName", out var tnObj) || !(tnObj is string typeName) || string.IsNullOrEmpty(typeName))
                throw new ArgumentException("missing required 'typeName' (string)");
            if (!parsed.TryGetValue("property", out var prObj) || !(prObj is string propName) || string.IsNullOrEmpty(propName))
                throw new ArgumentException("missing required 'property' (string)");
            if (!parsed.TryGetValue("value", out _targetValue))
                throw new ArgumentException("missing required 'value'");
            int index = 0;
            if (parsed.TryGetValue("index", out var idxObj))
            {
                if (idxObj is int ii) index = ii;
                else if (idxObj is long il) index = (int)il;
            }

            _resolvedGo = ComponentAddTool.ResolveGameObjectPath(path);
            if (_resolvedGo == null)
                throw new InvalidOperationException("GameObject not found at path: " + path);

            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage != null && _resolvedGo.scene == stage.scene)
                throw new InvalidOperationException(
                    "target is inside Prefab Stage; component/set_property refuses to bake into prefab asset.");

            if (!YorimashiTypeResolver.TryResolveComponent(typeName, out _targetType, out var err))
                throw new ArgumentException("component type: " + err);

            var comps = _resolvedGo.GetComponents(_targetType);
            if (comps.Length == 0)
                throw new InvalidOperationException(
                    "no Component of type '" + _targetType.FullName + "' on '" + path + "'");
            if (index < 0 || index >= comps.Length)
                throw new ArgumentOutOfRangeException("index",
                    "index " + index + " out of range; GO has " + comps.Length + " " + _targetType.Name);

            _target = comps[index];
            if (_target == null)
                throw new InvalidOperationException("component slot is null (missing script?)");

            _propertyName = propName;

            // 决定走哪个通道 + 记 old value
            LocateAndReadOld(_target, propName, out _resolvedVia, out _fieldTypeName, out _oldValue);

            var item = new Dictionary<string, object>
            {
                { "op", "component_set_property" },
                { "path", path },
                { "typeName", typeName },
                { "index", index },
                { "property", propName },
                { "resolvedVia", _resolvedVia },
                { "fieldType", _fieldTypeName },
                { "from", ValueForJson(_oldValue) },
                { "to", ValueForJson(_targetValue) },
            };

            var summary = "will set " + _targetType.Name + "." + propName + " on '" + path
                        + "[" + index + "]': " + FormatShort(_oldValue) + " → " + FormatShort(_targetValue);

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
            if (_target == null) throw new InvalidOperationException("preview state missing");

            Undo.RecordObject(_target, "component/set_property");
            SetOneProperty(_target, _propertyName, _targetValue, out var readback);

            EditorUtility.SetDirty(_target);
            if (_resolvedGo.scene.IsValid())
                EditorSceneManager.MarkSceneDirty(_resolvedGo.scene);

            bool verified = ValuesRoughlyEqual(readback, _targetValue);

            return new Dictionary<string, object>
            {
                { "readback", ValueForJson(readback) },
                { "verified", verified },
                { "wroteViaSerializedProperty", _resolvedVia == "serializedProperty" },
            };
        }

        // ===== helpers ==========================================================

        internal static void SetOneProperty(Component target, string propName, object value, out object readback)
        {
            var type = target.GetType();

            // UnityEvent 派生类型和 Object reference 必须走 SerializedProperty
            var field = YorimashiTypeResolver.FindSerializedFieldInHierarchy(type, propName);
            var prop = field == null ? YorimashiTypeResolver.FindPropertyInHierarchy(type, propName) : null;

            Type fieldType = field?.FieldType ?? prop?.PropertyType;
            bool forceSerialized = fieldType != null && (
                typeof(UnityEventBase).IsAssignableFrom(fieldType)
                || typeof(UnityEngine.Object).IsAssignableFrom(fieldType));

            if (!forceSerialized && field != null)
            {
                var coerced = Coerce(value, field.FieldType);
                field.SetValue(target, coerced);
                readback = field.GetValue(target);
                return;
            }
            if (!forceSerialized && prop != null)
            {
                var coerced = Coerce(value, prop.PropertyType);
                prop.SetValue(target, coerced, null);
                readback = prop.GetValue(target, null);
                return;
            }

            // SerializedProperty 通道
            var so = new SerializedObject(target);
            var sp = so.FindProperty(propName);
            if (sp == null)
            {
                // 兼容 "m_XXX" 私有字段命名
                sp = so.FindProperty("m_" + char.ToUpper(propName[0]) + propName.Substring(1));
            }
            if (sp == null)
                throw new InvalidOperationException("field '" + propName + "' not found on " + type.FullName);

            SetSerializedProperty(sp, value);
            so.ApplyModifiedProperties();
            so.Update();

            var freshSp = new SerializedObject(target).FindProperty(sp.propertyPath);
            readback = ReadSerializedProperty(freshSp);
            if (readback == null && fieldType != null && typeof(UnityEngine.Object).IsAssignableFrom(fieldType) && value != null)
                throw new InvalidOperationException(
                    "reference did not persist (assigned " + FormatShort(value) + " but readback is null)");
        }

        private static void LocateAndReadOld(Component target, string propName,
            out string via, out string fieldTypeName, out object oldValue)
        {
            var type = target.GetType();

            var field = YorimashiTypeResolver.FindSerializedFieldInHierarchy(type, propName);
            if (field != null)
            {
                bool forceSp = typeof(UnityEventBase).IsAssignableFrom(field.FieldType)
                            || typeof(UnityEngine.Object).IsAssignableFrom(field.FieldType);
                if (!forceSp)
                {
                    via = "reflection";
                    fieldTypeName = field.FieldType.FullName;
                    oldValue = field.GetValue(target);
                    return;
                }
            }

            var prop = YorimashiTypeResolver.FindPropertyInHierarchy(type, propName);
            if (prop != null)
            {
                bool forceSp = typeof(UnityEventBase).IsAssignableFrom(prop.PropertyType)
                            || typeof(UnityEngine.Object).IsAssignableFrom(prop.PropertyType);
                if (!forceSp)
                {
                    via = "reflection";
                    fieldTypeName = prop.PropertyType.FullName;
                    oldValue = prop.GetValue(target, null);
                    return;
                }
            }

            // SerializedProperty
            var so = new SerializedObject(target);
            var sp = so.FindProperty(propName);
            if (sp == null)
                sp = so.FindProperty("m_" + char.ToUpper(propName[0]) + propName.Substring(1));
            if (sp == null)
                throw new InvalidOperationException("field '" + propName + "' not found on " + type.FullName);

            via = "serializedProperty";
            fieldTypeName = sp.propertyType.ToString();
            oldValue = ReadSerializedProperty(sp);
        }

        private static object Coerce(object value, Type target)
        {
            if (value == null) return null;
            if (target.IsInstanceOfType(value)) return value;

            if (target == typeof(int) || target == typeof(long))
            {
                if (value is long l) return target == typeof(int) ? (object)(int)l : (object)l;
                if (value is double d) return target == typeof(int) ? (object)(int)d : (object)(long)d;
                if (value is int i) return target == typeof(int) ? (object)i : (object)(long)i;
            }
            if (target == typeof(float))
            {
                if (value is double d) return (float)d;
                if (value is int i) return (float)i;
                if (value is long l) return (float)l;
            }
            if (target == typeof(double))
            {
                if (value is float f) return (double)f;
                if (value is int i) return (double)i;
                if (value is long l) return (double)l;
            }
            if (target == typeof(bool))
            {
                if (value is bool b) return b;
            }
            if (target == typeof(string))
            {
                return value.ToString();
            }
            if (target.IsEnum)
            {
                if (value is string es) return Enum.Parse(target, es, ignoreCase: true);
                if (value is long el) return Enum.ToObject(target, el);
                if (value is int ei) return Enum.ToObject(target, ei);
                if (value is double ed) return Enum.ToObject(target, (long)ed);
            }
            if (target == typeof(Vector2) && value is Dictionary<string, object> v2)
                return new Vector2(GetFloat(v2, "x"), GetFloat(v2, "y"));
            if (target == typeof(Vector3) && value is Dictionary<string, object> v3)
                return new Vector3(GetFloat(v3, "x"), GetFloat(v3, "y"), GetFloat(v3, "z"));
            if (target == typeof(Vector4) && value is Dictionary<string, object> v4)
                return new Vector4(GetFloat(v4, "x"), GetFloat(v4, "y"), GetFloat(v4, "z"), GetFloat(v4, "w"));
            if (target == typeof(Color) && value is Dictionary<string, object> c4)
                return new Color(GetFloat(c4, "r"), GetFloat(c4, "g"), GetFloat(c4, "b"), GetFloat(c4, "a", 1f));

            return value;
        }

        private static float GetFloat(Dictionary<string, object> d, string k, float fallback = 0f)
        {
            if (!d.TryGetValue(k, out var v)) return fallback;
            if (v is float f) return f;
            if (v is double dd) return (float)dd;
            if (v is int i) return (float)i;
            if (v is long l) return (float)l;
            return fallback;
        }

        private static void SetSerializedProperty(SerializedProperty sp, object value)
        {
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    sp.boolValue = value is bool b && b; break;
                case SerializedPropertyType.Integer:
                    if (value is long l) sp.longValue = l;
                    else if (value is int i) sp.intValue = i;
                    else if (value is double d) sp.intValue = (int)d;
                    else throw new InvalidOperationException("cannot coerce " + value?.GetType().Name + " to int");
                    break;
                case SerializedPropertyType.Float:
                    if (value is double df) sp.floatValue = (float)df;
                    else if (value is float ff) sp.floatValue = ff;
                    else if (value is int fi) sp.floatValue = (float)fi;
                    else throw new InvalidOperationException("cannot coerce " + value?.GetType().Name + " to float");
                    break;
                case SerializedPropertyType.String:
                    sp.stringValue = value?.ToString() ?? "";
                    break;
                case SerializedPropertyType.Color:
                    if (value is Dictionary<string, object> cd)
                        sp.colorValue = new Color(GetFloat(cd, "r"), GetFloat(cd, "g"), GetFloat(cd, "b"), GetFloat(cd, "a", 1f));
                    break;
                case SerializedPropertyType.Vector2:
                    if (value is Dictionary<string, object> v2)
                        sp.vector2Value = new Vector2(GetFloat(v2, "x"), GetFloat(v2, "y"));
                    break;
                case SerializedPropertyType.Vector3:
                    if (value is Dictionary<string, object> v3)
                        sp.vector3Value = new Vector3(GetFloat(v3, "x"), GetFloat(v3, "y"), GetFloat(v3, "z"));
                    break;
                case SerializedPropertyType.Vector4:
                    if (value is Dictionary<string, object> v4)
                        sp.vector4Value = new Vector4(GetFloat(v4, "x"), GetFloat(v4, "y"), GetFloat(v4, "z"), GetFloat(v4, "w"));
                    break;
                case SerializedPropertyType.Enum:
                    if (value is string es)
                    {
                        int idx = sp.enumNames.Length > 0
                            ? Array.FindIndex(sp.enumNames, n => string.Equals(n, es, StringComparison.OrdinalIgnoreCase))
                            : -1;
                        if (idx < 0) throw new InvalidOperationException("enum '" + es + "' not in " + string.Join(",", sp.enumNames));
                        sp.enumValueIndex = idx;
                    }
                    else if (value is long el) sp.enumValueIndex = (int)el;
                    else if (value is int ei) sp.enumValueIndex = ei;
                    else throw new InvalidOperationException("cannot coerce enum from " + value?.GetType().Name);
                    break;
                case SerializedPropertyType.ObjectReference:
                    sp.objectReferenceValue = ResolveObjectReference(value);
                    break;
                default:
                    throw new InvalidOperationException("SerializedProperty type " + sp.propertyType + " not supported by set_property yet");
            }
        }

        private static UnityEngine.Object ResolveObjectReference(object value)
        {
            if (value == null) return null;
            if (value is UnityEngine.Object direct) return direct;
            if (value is Dictionary<string, object> dict)
            {
                if (dict.TryGetValue("guid", out var guidObj) && guidObj is string guid && !string.IsNullOrEmpty(guid))
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!string.IsNullOrEmpty(path))
                        return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                }
                if (dict.TryGetValue("assetPath", out var pObj) && pObj is string ap && !string.IsNullOrEmpty(ap))
                    return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(ap);
            }
            if (value is string s && s.StartsWith("Assets/"))
                return AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(s);
            throw new InvalidOperationException("cannot resolve Object reference from " + value);
        }

        private static object ReadSerializedProperty(SerializedProperty sp)
        {
            if (sp == null) return null;
            switch (sp.propertyType)
            {
                case SerializedPropertyType.Boolean:  return sp.boolValue;
                case SerializedPropertyType.Integer:  return (long)sp.intValue;
                case SerializedPropertyType.Float:    return sp.floatValue;
                case SerializedPropertyType.String:   return sp.stringValue;
                case SerializedPropertyType.Color:    return new Dictionary<string, object> {
                    {"r", sp.colorValue.r},{"g", sp.colorValue.g},{"b", sp.colorValue.b},{"a", sp.colorValue.a}};
                case SerializedPropertyType.Vector2:  return new Dictionary<string, object> {
                    {"x", sp.vector2Value.x},{"y", sp.vector2Value.y}};
                case SerializedPropertyType.Vector3:  return new Dictionary<string, object> {
                    {"x", sp.vector3Value.x},{"y", sp.vector3Value.y},{"z", sp.vector3Value.z}};
                case SerializedPropertyType.Vector4:  return new Dictionary<string, object> {
                    {"x", sp.vector4Value.x},{"y", sp.vector4Value.y},{"z", sp.vector4Value.z},{"w", sp.vector4Value.w}};
                case SerializedPropertyType.Enum:
                    return sp.enumValueIndex >= 0 && sp.enumValueIndex < sp.enumNames.Length
                        ? (object)sp.enumNames[sp.enumValueIndex] : null;
                case SerializedPropertyType.ObjectReference:
                    if (sp.objectReferenceValue == null) return null;
                    var path = AssetDatabase.GetAssetPath(sp.objectReferenceValue);
                    return new Dictionary<string, object> {
                        {"instanceId", sp.objectReferenceValue.GetInstanceID()},
                        {"name", sp.objectReferenceValue.name},
                        {"assetPath", path ?? ""},
                    };
                default: return sp.propertyType.ToString();
            }
        }

        private static bool ValuesRoughlyEqual(object a, object b)
        {
            if (a == null && b == null) return true;
            if (a == null || b == null) return false;
            if (a is float fa)
            {
                float fb = b is float x ? x : (b is double d ? (float)d : (b is int i ? (float)i : float.NaN));
                if (float.IsNaN(fb)) return false;
                return Mathf.Abs(fa - fb) < 1e-4f;
            }
            if (a is double da)
            {
                double db = b is double x ? x : (b is float f ? (double)f : (b is int i ? (double)i : double.NaN));
                if (double.IsNaN(db)) return false;
                return Math.Abs(da - db) < 1e-4;
            }
            if (a is Dictionary<string, object> ad && b is Dictionary<string, object> bd)
            {
                foreach (var kv in bd)
                {
                    if (!ad.TryGetValue(kv.Key, out var va)) return false;
                    if (!ValuesRoughlyEqual(va, kv.Value)) return false;
                }
                return true;
            }
            return a.Equals(b);
        }

        private static object ValueForJson(object v)
        {
            if (v is UnityEngine.Object o)
                return new Dictionary<string, object> {
                    {"instanceId", o.GetInstanceID()}, {"name", o.name} };
            return v;
        }

        private static string FormatShort(object v)
        {
            if (v == null) return "null";
            if (v is UnityEngine.Object o) return o.name;
            if (v is Dictionary<string, object>) return "{...}";
            return v.ToString();
        }
    }
}
