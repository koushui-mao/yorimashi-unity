// Yorimashi animator/list_layers — M3-E-P5 只读 tool
//
// 列出 AnimatorController asset 的完整结构:
//   - layers: name / weight / blending / defaultState
//   - states (per layer): name / motion (asset path if any) / speed / writeDefaults
//   - transitions (per state): src → dst + conditions
//   - parameters: name / type / defaultValue
//
// 参数:
//   assetPath (string, required) - "Assets/.../XXX.controller"
//   OR
//   avatarPath (string, optional) - scene GO path, 会去 Animator.runtimeAnimatorController 拿
//
// 只读, 不改动任何东西, 无 scope guard。
//
// Sentinel: "M3-E-P5 ANIMATOR LIST"
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal static class AnimatorListTool
    {
        public const string SentinelTag = "M3-E-P5 ANIMATOR LIST";
        public const string ToolName = "animator/list_layers";

        public static string Execute(string paramsJson)
        {
            try
            {
                var parsed = string.IsNullOrEmpty(paramsJson) || paramsJson == "null"
                    ? new Dictionary<string, object>()
                    : new MiniJsonParser(paramsJson).ParseObject();

                AnimatorController ctrl = null;
                string resolvedPath = "";

                if (parsed.TryGetValue("assetPath", out var apObj) && apObj is string ap && !string.IsNullOrEmpty(ap))
                {
                    ctrl = AssetDatabase.LoadAssetAtPath<AnimatorController>(ap);
                    resolvedPath = ap;
                }
                else if (parsed.TryGetValue("avatarPath", out var avObj) && avObj is string av && !string.IsNullOrEmpty(av))
                {
                    var go = ResolveScenePath(av);
                    if (go == null)
                        return "{\"error\":" + YorimashiEnvelope.EncodeString("GameObject not found: " + av) + "}";
                    var animator = go.GetComponent<Animator>();
                    if (animator == null)
                        return "{\"error\":" + YorimashiEnvelope.EncodeString("no Animator on " + av) + "}";
                    ctrl = animator.runtimeAnimatorController as AnimatorController;
                    if (ctrl != null) resolvedPath = AssetDatabase.GetAssetPath(ctrl);
                }
                else
                {
                    return "{\"error\":\"missing required 'assetPath' or 'avatarPath'\"}";
                }

                if (ctrl == null)
                    return "{\"error\":" + YorimashiEnvelope.EncodeString("no AnimatorController found") + "}";

                return BuildJson(ctrl, resolvedPath);
            }
            catch (Exception e)
            {
                return "{\"error\":" + YorimashiEnvelope.EncodeString(e.Message) + "}";
            }
        }

        private static string BuildJson(AnimatorController ctrl, string assetPath)
        {
            var sb = new StringBuilder(2048);
            sb.Append('{');
            sb.Append("\"assetPath\":").Append(YorimashiEnvelope.EncodeString(assetPath));
            sb.Append(",\"controllerName\":").Append(YorimashiEnvelope.EncodeString(ctrl.name));

            // parameters
            sb.Append(",\"parameters\":[");
            var pars = ctrl.parameters;
            for (int i = 0; i < pars.Length; i++)
            {
                var p = pars[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"name\":").Append(YorimashiEnvelope.EncodeString(p.name));
                sb.Append(",\"type\":").Append(YorimashiEnvelope.EncodeString(p.type.ToString()));
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        sb.Append(",\"default\":").Append(p.defaultFloat.ToString("R", System.Globalization.CultureInfo.InvariantCulture)); break;
                    case AnimatorControllerParameterType.Int:
                        sb.Append(",\"default\":").Append(p.defaultInt); break;
                    case AnimatorControllerParameterType.Bool:
                    case AnimatorControllerParameterType.Trigger:
                        sb.Append(",\"default\":").Append(p.defaultBool ? "true" : "false"); break;
                }
                sb.Append('}');
            }
            sb.Append(']');

            // layers
            sb.Append(",\"layers\":[");
            var layers = ctrl.layers;
            for (int i = 0; i < layers.Length; i++)
            {
                var L = layers[i];
                if (i > 0) sb.Append(',');
                sb.Append('{');
                sb.Append("\"index\":").Append(i);
                sb.Append(",\"name\":").Append(YorimashiEnvelope.EncodeString(L.name));
                sb.Append(",\"weight\":").Append(L.defaultWeight.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                sb.Append(",\"blendingMode\":").Append(YorimashiEnvelope.EncodeString(L.blendingMode.ToString()));

                var sm = L.stateMachine;
                if (sm != null)
                {
                    var defState = sm.defaultState;
                    sb.Append(",\"defaultState\":").Append(defState != null ? YorimashiEnvelope.EncodeString(defState.name) : "null");

                    // states
                    sb.Append(",\"states\":[");
                    for (int j = 0; j < sm.states.Length; j++)
                    {
                        var cs = sm.states[j];
                        if (j > 0) sb.Append(',');
                        sb.Append('{');
                        sb.Append("\"name\":").Append(YorimashiEnvelope.EncodeString(cs.state.name));
                        sb.Append(",\"speed\":").Append(cs.state.speed.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                        sb.Append(",\"writeDefaultValues\":").Append(cs.state.writeDefaultValues ? "true" : "false");
                        if (cs.state.motion != null)
                            sb.Append(",\"motionAssetPath\":").Append(YorimashiEnvelope.EncodeString(AssetDatabase.GetAssetPath(cs.state.motion) ?? ""));
                        // transitions
                        sb.Append(",\"transitions\":[");
                        for (int t = 0; t < cs.state.transitions.Length; t++)
                        {
                            var tr = cs.state.transitions[t];
                            if (t > 0) sb.Append(',');
                            sb.Append('{');
                            sb.Append("\"toState\":").Append(YorimashiEnvelope.EncodeString(tr.destinationState != null ? tr.destinationState.name : "(exit)"));
                            sb.Append(",\"hasExitTime\":").Append(tr.hasExitTime ? "true" : "false");
                            sb.Append(",\"exitTime\":").Append(tr.exitTime.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                            sb.Append(",\"duration\":").Append(tr.duration.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                            sb.Append(",\"conditions\":[");
                            for (int k = 0; k < tr.conditions.Length; k++)
                            {
                                var c = tr.conditions[k];
                                if (k > 0) sb.Append(',');
                                sb.Append('{');
                                sb.Append("\"parameter\":").Append(YorimashiEnvelope.EncodeString(c.parameter));
                                sb.Append(",\"mode\":").Append(YorimashiEnvelope.EncodeString(c.mode.ToString()));
                                sb.Append(",\"threshold\":").Append(c.threshold.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                                sb.Append('}');
                            }
                            sb.Append("]}");
                        }
                        sb.Append(']');
                        sb.Append('}');
                    }
                    sb.Append(']');
                }
                sb.Append('}');
            }
            sb.Append(']');

            sb.Append('}');
            return sb.ToString();
        }

        internal static GameObject ResolveScenePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var parts = path.Split('/');
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
