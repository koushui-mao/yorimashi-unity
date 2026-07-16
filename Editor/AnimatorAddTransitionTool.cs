// Yorimashi animator/add_transition — M3-E-P5 真改动 asset tool
//
// 在两个 state 间加 transition + optional conditions。
//
// 参数:
//   controllerPath (string, required)
//   layerName      (string, required)
//   fromStateName  (string, required)
//   toStateName    (string, required) - "(exit)" 表示 Exit
//   hasExitTime    (bool, optional, default false)
//   exitTime       (number, optional, default 0.0)
//   duration       (number, optional, default 0.0) - transition duration (seconds)
//   conditions     (array, optional) - 每个 element: {parameter, mode, threshold}
//     mode: "Equals" | "NotEqual" | "Greater" | "Less" | "If" (bool true) | "IfNot" (bool false)
//   dry_run
//
// Sentinel: "M3-E-P5 ANIMATOR ADD TRANSITION"
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;

namespace Yorimashi.Modder.Editor
{
    internal class AnimatorAddTransitionTool : AnimatorWriteToolBase
    {
        public const string SentinelTag = "M3-E-P5 ANIMATOR ADD TRANSITION";
        public override string ToolName => "animator/add_transition";

        private string _layerName;
        private string _fromState;
        private string _toState;
        private bool _hasExitTime;
        private float _exitTime;
        private float _duration;
        private List<Dictionary<string, object>> _conditions;
        private AnimatorState _srcStateObj;
        private AnimatorState _dstStateObj;
        private bool _toExit;

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            ResolveController(parsed);

            if (!parsed.TryGetValue("layerName", out var lnObj) || !(lnObj is string ln))
                throw new ArgumentException("missing required 'layerName'");
            if (!parsed.TryGetValue("fromStateName", out var fsObj) || !(fsObj is string fs))
                throw new ArgumentException("missing required 'fromStateName'");
            if (!parsed.TryGetValue("toStateName", out var tsObj) || !(tsObj is string ts))
                throw new ArgumentException("missing required 'toStateName'");

            _layerName = ln; _fromState = fs; _toState = ts;
            _toExit = ts == "(exit)";

            _hasExitTime = false;
            if (parsed.TryGetValue("hasExitTime", out var heObj) && heObj is bool heb) _hasExitTime = heb;
            _exitTime = ToFloat(parsed.TryGetValue("exitTime", out var etObj) ? etObj : null, 0f);
            _duration = ToFloat(parsed.TryGetValue("duration", out var duObj) ? duObj : null, 0f);

            _conditions = new List<Dictionary<string, object>>();
            if (parsed.TryGetValue("conditions", out var cObj) && cObj is List<object> arr)
            {
                foreach (var e in arr)
                {
                    if (e is Dictionary<string, object> ed) _conditions.Add(ed);
                }
            }

            // 定位 layer + state
            AnimatorControllerLayer targetLayer = null;
            foreach (var L in _resolvedCtrl.layers)
                if (L.name == _layerName) { targetLayer = L; break; }
            if (targetLayer == null)
                throw new InvalidOperationException("layer '" + _layerName + "' not found");

            var sm = targetLayer.stateMachine;
            _srcStateObj = null;
            foreach (var cs in sm.states)
                if (cs.state.name == _fromState) { _srcStateObj = cs.state; break; }
            if (_srcStateObj == null)
                throw new InvalidOperationException("fromState '" + _fromState + "' not found in layer '" + _layerName + "'");

            _dstStateObj = null;
            if (!_toExit)
            {
                foreach (var cs in sm.states)
                    if (cs.state.name == _toState) { _dstStateObj = cs.state; break; }
                if (_dstStateObj == null)
                    throw new InvalidOperationException("toState '" + _toState + "' not found in layer '" + _layerName + "'");
            }

            // 校验每个 condition 引用的 parameter 存在
            foreach (var c in _conditions)
            {
                if (!c.TryGetValue("parameter", out var pObj) || !(pObj is string pn))
                    throw new ArgumentException("condition missing 'parameter'");
                bool found = false;
                foreach (var p in _resolvedCtrl.parameters)
                    if (p.name == pn) { found = true; break; }
                if (!found)
                    throw new InvalidOperationException("condition references undefined parameter '" + pn + "'");
                if (!c.TryGetValue("mode", out var mObj) || !(mObj is string ms))
                    throw new ArgumentException("condition missing 'mode'");
                if (!Enum.TryParse<AnimatorConditionMode>(ms, ignoreCase: true, out _))
                    throw new ArgumentException("condition mode must be one of Equals/NotEqual/Greater/Less/If/IfNot, got: " + ms);
            }

            var item = new Dictionary<string, object>
            {
                { "op", "animator_add_transition" },
                { "controllerPath", _resolvedCtrlPath },
                { "layerName", _layerName },
                { "fromStateName", _fromState },
                { "toStateName", _toState },
                { "hasExitTime", _hasExitTime },
                { "exitTime", _exitTime },
                { "duration", _duration },
                { "conditionCount", _conditions.Count },
            };
            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>> { item },
                BackupPath = null,
                Summary = "will add transition " + _fromState + " → " + _toState
                        + " (" + _conditions.Count + " conditions)",
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            var backupDir = BackupController();
            Undo.RegisterCompleteObjectUndo(_resolvedCtrl, "Yorimashi animator/add_transition");

            AnimatorStateTransition tr;
            if (_toExit) tr = _srcStateObj.AddExitTransition();
            else         tr = _srcStateObj.AddTransition(_dstStateObj);

            tr.hasExitTime = _hasExitTime;
            tr.exitTime = _exitTime;
            tr.duration = _duration;

            foreach (var c in _conditions)
            {
                var pn = (string)c["parameter"];
                var ms = (string)c["mode"];
                Enum.TryParse<AnimatorConditionMode>(ms, ignoreCase: true, out var mode);
                float threshold = ToFloat(c.TryGetValue("threshold", out var thObj) ? thObj : null, 0f);
                tr.AddCondition(mode, threshold, pn);
            }

            SaveController();

            // Verify
            bool verified = false;
            foreach (var t in _srcStateObj.transitions)
            {
                if (_toExit && t.isExit && t.conditions.Length == _conditions.Count) { verified = true; break; }
                if (!_toExit && t.destinationState == _dstStateObj && t.conditions.Length == _conditions.Count) { verified = true; break; }
            }

            return new Dictionary<string, object>
            {
                { "verified", verified },
                { "backupPath", backupDir },
                { "fromStateName", _fromState },
                { "toStateName", _toState },
                { "conditionCount", _conditions.Count },
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
    }
}
