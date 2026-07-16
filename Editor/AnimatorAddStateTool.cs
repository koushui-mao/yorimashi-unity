// Yorimashi animator/add_state — M3-E-P5 真改动 asset tool
//
// 在指定 layer 里加一个 AnimatorState。可选 motion (AnimationClip asset)。
// 若 layer 里还没 default state, 新 state 自动设为 default (Unity 官方行为)。
//
// 参数:
//   controllerPath (string, required)
//   layerName      (string, required) - 目标 layer 名字
//   stateName      (string, required) - 新 state 名字
//   motionAssetPath (string, optional) - "Assets/.../XXX.anim" AnimationClip 路径
//   speed          (number, optional, default 1.0)
//   writeDefaultValues (bool, optional, default true - VRChat 标准是 false, 但保守默认 true)
//   makeDefault    (bool, optional, default = "layer 里没 default 就 true, 否则 false")
//   dry_run
//
// Sentinel: "M3-E-P5 ANIMATOR ADD STATE"
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal class AnimatorAddStateTool : AnimatorWriteToolBase
    {
        public const string SentinelTag = "M3-E-P5 ANIMATOR ADD STATE";
        public override string ToolName => "animator/add_state";

        private string _layerName;
        private string _stateName;
        private string _motionPath;
        private float _speed;
        private bool _writeDefaults;
        private bool? _makeDefault;
        private AnimatorControllerLayer _targetLayer;
        private int _layerIndex;

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            ResolveController(parsed);

            if (!parsed.TryGetValue("layerName", out var lnObj) || !(lnObj is string ln) || string.IsNullOrEmpty(ln))
                throw new ArgumentException("missing required 'layerName'");
            if (!parsed.TryGetValue("stateName", out var snObj) || !(snObj is string sn) || string.IsNullOrEmpty(sn))
                throw new ArgumentException("missing required 'stateName'");

            _layerName = ln;
            _stateName = sn;

            _speed = 1f;
            if (parsed.TryGetValue("speed", out var spObj))
            {
                if (spObj is float sf) _speed = sf;
                else if (spObj is double sd) _speed = (float)sd;
                else if (spObj is int si) _speed = (float)si;
            }
            _writeDefaults = true;
            if (parsed.TryGetValue("writeDefaultValues", out var wdObj) && wdObj is bool wdb) _writeDefaults = wdb;

            _makeDefault = null;
            if (parsed.TryGetValue("makeDefault", out var mdObj) && mdObj is bool mdb) _makeDefault = mdb;

            _motionPath = null;
            if (parsed.TryGetValue("motionAssetPath", out var mObj) && mObj is string mp && !string.IsNullOrEmpty(mp))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(mp);
                if (clip == null)
                    throw new InvalidOperationException("AnimationClip not found at '" + mp + "'");
                _motionPath = mp;
            }

            // 定位 layer
            var layers = _resolvedCtrl.layers;
            _targetLayer = null;
            _layerIndex = -1;
            for (int i = 0; i < layers.Length; i++)
            {
                if (layers[i].name == _layerName) { _targetLayer = layers[i]; _layerIndex = i; break; }
            }
            if (_targetLayer == null)
                throw new InvalidOperationException("layer '" + _layerName + "' not found in controller");

            // 冲突: 同名 state
            var sm = _targetLayer.stateMachine;
            if (sm == null)
                throw new InvalidOperationException("layer '" + _layerName + "' has no stateMachine");
            foreach (var cs in sm.states)
            {
                if (cs.state.name == _stateName)
                    throw new InvalidOperationException("state '" + _stateName + "' already exists in layer '" + _layerName + "'");
            }

            bool willBeDefault = _makeDefault ?? (sm.defaultState == null);

            var item = new Dictionary<string, object>
            {
                { "op", "animator_add_state" },
                { "controllerPath", _resolvedCtrlPath },
                { "layerName", _layerName },
                { "layerIndex", _layerIndex },
                { "stateName", _stateName },
                { "motionAssetPath", _motionPath ?? "" },
                { "speed", _speed },
                { "writeDefaultValues", _writeDefaults },
                { "willBeDefault", willBeDefault },
            };
            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>> { item },
                BackupPath = null,
                Summary = "will add state '" + _stateName + "' to layer '" + _layerName + "'"
                        + (willBeDefault ? " (as default)" : "")
                        + (_motionPath != null ? " with motion " + _motionPath : ""),
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            var backupDir = BackupController();
            Undo.RegisterCompleteObjectUndo(_resolvedCtrl, "Yorimashi animator/add_state");

            var sm = _targetLayer.stateMachine;
            var newState = sm.AddState(_stateName);
            newState.speed = _speed;
            newState.writeDefaultValues = _writeDefaults;

            if (!string.IsNullOrEmpty(_motionPath))
            {
                var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(_motionPath);
                if (clip != null) newState.motion = clip;
            }

            bool becameDefault = false;
            bool wasEmptyLayer = sm.defaultState == null;
            if (_makeDefault.HasValue)
            {
                if (_makeDefault.Value) { sm.defaultState = newState; becameDefault = true; }
            }
            else if (wasEmptyLayer)
            {
                sm.defaultState = newState;
                becameDefault = true;
            }

            SaveController();

            // Verify
            bool verified = false;
            foreach (var cs in sm.states)
            {
                if (cs.state.name == _stateName) { verified = true; break; }
            }

            return new Dictionary<string, object>
            {
                { "verified", verified },
                { "backupPath", backupDir },
                { "stateName", _stateName },
                { "layerName", _layerName },
                { "becameDefault", becameDefault },
            };
        }
    }
}
