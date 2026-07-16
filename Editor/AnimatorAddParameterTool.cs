// Yorimashi animator/add_parameter — M3-E-P5 真改动 asset tool
//
// 给 AnimatorController 加一个 parameter (Bool/Int/Float/Trigger)。
//
// 参数:
//   controllerPath (string, required) - "Assets/.../XXX.controller" (白名单沙盒下)
//   parameterName  (string, required) - 参数名
//   parameterType  (string, required) - "Bool" | "Int" | "Float" | "Trigger"
//   defaultValue   (any, optional) - 默认值 (bool 时 true/false, Int 时整数, Float 时数字, Trigger 忽略)
//   dry_run (bool, default true)
//
// Sentinel: "M3-E-P5 ANIMATOR ADD PARAMETER"
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;

namespace Yorimashi.Modder.Editor
{
    internal class AnimatorAddParameterTool : AnimatorWriteToolBase
    {
        public const string SentinelTag = "M3-E-P5 ANIMATOR ADD PARAMETER";
        public override string ToolName => "animator/add_parameter";

        private string _paramName;
        private AnimatorControllerParameterType _paramType;
        private object _defaultValue;

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            ResolveController(parsed);

            if (!parsed.TryGetValue("parameterName", out var pnObj) || !(pnObj is string pn) || string.IsNullOrEmpty(pn))
                throw new ArgumentException("missing required 'parameterName'");
            if (!parsed.TryGetValue("parameterType", out var ptObj) || !(ptObj is string pt) || string.IsNullOrEmpty(pt))
                throw new ArgumentException("missing required 'parameterType'");

            if (!Enum.TryParse<AnimatorControllerParameterType>(pt, ignoreCase: true, out _paramType))
                throw new ArgumentException("parameterType must be one of Bool/Int/Float/Trigger, got: " + pt);

            _paramName = pn;
            parsed.TryGetValue("defaultValue", out _defaultValue);

            // 冲突检查: 已存在同名 parameter
            foreach (var p in _resolvedCtrl.parameters)
            {
                if (p.name == _paramName)
                    throw new InvalidOperationException("parameter '" + _paramName + "' already exists (type=" + p.type + ")");
            }

            var item = new Dictionary<string, object>
            {
                { "op", "animator_add_parameter" },
                { "controllerPath", _resolvedCtrlPath },
                { "parameterName", _paramName },
                { "parameterType", _paramType.ToString() },
                { "defaultValue", _defaultValue },
            };
            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>> { item },
                BackupPath = null,
                Summary = "will add " + _paramType + " parameter '" + _paramName + "' to " + _resolvedCtrlPath,
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            var backupDir = BackupController();
            Undo.RegisterCompleteObjectUndo(_resolvedCtrl, "Yorimashi animator/add_parameter");

            var newParam = new AnimatorControllerParameter
            {
                name = _paramName,
                type = _paramType,
            };
            switch (_paramType)
            {
                case AnimatorControllerParameterType.Float:
                    newParam.defaultFloat = ToFloat(_defaultValue, 0f); break;
                case AnimatorControllerParameterType.Int:
                    newParam.defaultInt = ToInt(_defaultValue, 0); break;
                case AnimatorControllerParameterType.Bool:
                case AnimatorControllerParameterType.Trigger:
                    newParam.defaultBool = ToBool(_defaultValue, false); break;
            }

            _resolvedCtrl.AddParameter(newParam);
            SaveController();

            // Verify by readback
            bool verified = false;
            foreach (var p in _resolvedCtrl.parameters)
            {
                if (p.name == _paramName && p.type == _paramType) { verified = true; break; }
            }

            return new Dictionary<string, object>
            {
                { "verified", verified },
                { "backupPath", backupDir },
                { "parameterName", _paramName },
                { "parameterType", _paramType.ToString() },
            };
        }

        private static float ToFloat(object v, float fb)
        {
            if (v is float f) return f;
            if (v is double d) return (float)d;
            if (v is int i) return (float)i;
            if (v is long l) return (float)l;
            return fb;
        }
        private static int ToInt(object v, int fb)
        {
            if (v is int i) return i;
            if (v is long l) return (int)l;
            if (v is double d) return (int)d;
            return fb;
        }
        private static bool ToBool(object v, bool fb)
        {
            if (v is bool b) return b;
            return fb;
        }
    }
}
