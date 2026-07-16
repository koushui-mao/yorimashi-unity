// Yorimashi animator/add_layer — M3-E-P5 真改动 asset tool
//
// 加一个 layer 到 AnimatorController 末尾。
//
// 参数:
//   controllerPath (string, required)
//   layerName      (string, required)
//   defaultWeight  (number, optional, default 1.0, range 0-1)
//   blendingMode   (string, optional, "Override"|"Additive", default "Override")
//   dry_run
//
// 关键: AnimatorControllerLayer 的 stateMachine 需要新建, 且必须 AddObjectToAsset
//       到 controller, 否则保存后丢失。
//
// Sentinel: "M3-E-P5 ANIMATOR ADD LAYER"
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal class AnimatorAddLayerTool : AnimatorWriteToolBase
    {
        public const string SentinelTag = "M3-E-P5 ANIMATOR ADD LAYER";
        public override string ToolName => "animator/add_layer";

        private string _layerName;
        private float _weight;
        private AnimatorLayerBlendingMode _blending;

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            ResolveController(parsed);

            if (!parsed.TryGetValue("layerName", out var lnObj) || !(lnObj is string ln) || string.IsNullOrEmpty(ln))
                throw new ArgumentException("missing required 'layerName'");
            _layerName = ln;

            _weight = 1f;
            if (parsed.TryGetValue("defaultWeight", out var wObj))
            {
                if (wObj is float wf) _weight = wf;
                else if (wObj is double wd) _weight = (float)wd;
                else if (wObj is int wi) _weight = (float)wi;
            }
            if (_weight < 0f || _weight > 1f)
                throw new ArgumentException("defaultWeight must be in [0,1], got " + _weight);

            _blending = AnimatorLayerBlendingMode.Override;
            if (parsed.TryGetValue("blendingMode", out var bmObj) && bmObj is string bm)
            {
                if (!Enum.TryParse(bm, ignoreCase: true, out _blending))
                    throw new ArgumentException("blendingMode must be Override or Additive, got: " + bm);
            }

            // 冲突: 同名 layer
            foreach (var L in _resolvedCtrl.layers)
            {
                if (L.name == _layerName)
                    throw new InvalidOperationException("layer '" + _layerName + "' already exists");
            }

            var item = new Dictionary<string, object>
            {
                { "op", "animator_add_layer" },
                { "controllerPath", _resolvedCtrlPath },
                { "layerName", _layerName },
                { "defaultWeight", _weight },
                { "blendingMode", _blending.ToString() },
                { "insertAtIndex", _resolvedCtrl.layers.Length },
            };
            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>> { item },
                BackupPath = null,
                Summary = "will add layer '" + _layerName + "' at index " + _resolvedCtrl.layers.Length,
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            var backupDir = BackupController();
            Undo.RegisterCompleteObjectUndo(_resolvedCtrl, "Yorimashi animator/add_layer");

            // 创建 state machine 并挂到 controller asset
            var sm = new AnimatorStateMachine
            {
                name = _layerName,
                hideFlags = HideFlags.HideInHierarchy,
            };
            AssetDatabase.AddObjectToAsset(sm, _resolvedCtrl);

            var newLayer = new AnimatorControllerLayer
            {
                name = _layerName,
                defaultWeight = _weight,
                blendingMode = _blending,
                stateMachine = sm,
            };
            _resolvedCtrl.AddLayer(newLayer);
            SaveController();

            // Verify: 最后一层是不是新加的
            var layers = _resolvedCtrl.layers;
            bool verified = layers.Length > 0 && layers[layers.Length - 1].name == _layerName;

            return new Dictionary<string, object>
            {
                { "verified", verified },
                { "backupPath", backupDir },
                { "layerName", _layerName },
                { "layerIndex", layers.Length - 1 },
            };
        }
    }
}
