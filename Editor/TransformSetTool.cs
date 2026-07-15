// Yorimashi transform/set — M3-E TRANSFORM SET
//
// 设置 GameObject 的 Transform（position/rotation/scale）。
// 走 YorimashiWriteToolBase：默认 dry_run=true, scope guard 强制 path 白名单。
//
// 参数：
//   path      (string, required)             — GameObject 路径
//   position  (Vector3 obj {x,y,z}, optional) — 目标位置
//   rotation  (Vector3 obj {x,y,z}, optional) — Euler 角度（度），可选
//   scale     (Vector3 obj {x,y,z}, optional) — 缩放，必须 > 0
//   space     (string, "local"|"world", optional, default "local")
//   dry_run   (bool, optional, default true)
//
// 返回：
//   changes item: { op:"transform_set", path, space, from, to }
//   applied:      { position_written, rotation_written, scale_written, readback, verified, epsilon }
//
// 备份：Transform 三向量翻转，备份就是 from 字段本身，不落额外文件
// Undo.RecordObject 支持 Ctrl+Z
//
// Sentinel: "M3-E TRANSFORM SET"
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal class TransformSetTool : YorimashiWriteToolBase
    {
        public const string SentinelTag = "M3-E TRANSFORM SET";
        private const float VerifyEpsilon = 0.0001f;

        public override string ToolName => "transform/set";

        private Transform _tf;
        private string _space;
        private string _targetPath;

        // 目标值 (nullable = "该字段不改")
        private Vector3? _newPos;
        private Vector3? _newRot;
        private Vector3? _newScale;

        // 原始值（备份）
        private Vector3 _oldPos;
        private Vector3 _oldRot;
        private Vector3 _oldScale;

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            if (!parsed.TryGetValue("path", out var pObj) || !(pObj is string path) || string.IsNullOrEmpty(path))
                throw new ArgumentException("missing required 'path' (string)");

            _space = "local";
            if (parsed.TryGetValue("space", out var spObj) && spObj is string sp)
            {
                if (sp != "local" && sp != "world")
                    throw new ArgumentException("'space' must be 'local' or 'world'");
                _space = sp;
            }

            var go = ResolveGameObjectPath(path);
            if (go == null)
                throw new InvalidOperationException("GameObject not found at path: " + path);

            _tf = go.transform;
            _targetPath = path;

            // 记录 old（快照，dry_run 也用）
            if (_space == "local")
            {
                _oldPos = _tf.localPosition;
                _oldRot = _tf.localEulerAngles;
                _oldScale = _tf.localScale;
            }
            else
            {
                _oldPos = _tf.position;
                _oldRot = _tf.eulerAngles;
                _oldScale = _tf.localScale; // world scale 不可写；scale 一律走 local
            }

            _newPos = ExtractVector3(parsed, "position");
            _newRot = ExtractVector3(parsed, "rotation");
            _newScale = ExtractVector3(parsed, "scale");

            if (_newScale.HasValue)
            {
                var s = _newScale.Value;
                if (s.x <= 0f || s.y <= 0f || s.z <= 0f)
                    throw new ArgumentException(
                        "'scale' components must be > 0 (got " + s.x + "," + s.y + "," + s.z + ")");
            }

            if (!_newPos.HasValue && !_newRot.HasValue && !_newScale.HasValue)
                throw new ArgumentException("at least one of 'position', 'rotation', 'scale' required");

            var fromV3 = new Dictionary<string, object>
            {
                { "position", V3ToDict(_oldPos) },
                { "rotation", V3ToDict(_oldRot) },
                { "scale",    V3ToDict(_oldScale) },
            };
            var toV3 = new Dictionary<string, object>
            {
                { "position", V3ToDict(_newPos ?? _oldPos) },
                { "rotation", V3ToDict(_newRot ?? _oldRot) },
                { "scale",    V3ToDict(_newScale ?? _oldScale) },
            };

            var item = new Dictionary<string, object>
            {
                { "op", "transform_set" },
                { "path", path },
                { "space", _space },
                { "from", fromV3 },
                { "to", toV3 },
            };

            var summary = "will set transform on '" + path + "' (" + _space + "): "
                + (_newPos.HasValue ? "pos " : "")
                + (_newRot.HasValue ? "rot " : "")
                + (_newScale.HasValue ? "scale " : "");

            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>> { item },
                BackupPath = null,
                Summary = summary.Trim(),
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            if (_tf == null)
                throw new InvalidOperationException("preview state missing");

            Undo.RecordObject(_tf, "Yorimashi transform/set " + _targetPath);

            bool posWritten = false;
            bool rotWritten = false;
            bool scaleWritten = false;

            if (_newPos.HasValue)
            {
                if (_space == "local") _tf.localPosition = _newPos.Value;
                else _tf.position = _newPos.Value;
                posWritten = true;
            }
            if (_newRot.HasValue)
            {
                if (_space == "local") _tf.localEulerAngles = _newRot.Value;
                else _tf.eulerAngles = _newRot.Value;
                rotWritten = true;
            }
            if (_newScale.HasValue)
            {
                _tf.localScale = _newScale.Value;
                scaleWritten = true;
            }

            EditorUtility.SetDirty(_tf);

            // readback
            Vector3 rbPos, rbRot, rbScale;
            if (_space == "local")
            {
                rbPos = _tf.localPosition;
                rbRot = _tf.localEulerAngles;
                rbScale = _tf.localScale;
            }
            else
            {
                rbPos = _tf.position;
                rbRot = _tf.eulerAngles;
                rbScale = _tf.localScale;
            }

            bool verified = true;
            if (_newPos.HasValue && !ApproxEqual(rbPos, _newPos.Value)) verified = false;
            if (_newRot.HasValue && !ApproxEqualAngle(rbRot, _newRot.Value)) verified = false;
            if (_newScale.HasValue && !ApproxEqual(rbScale, _newScale.Value)) verified = false;

            return new Dictionary<string, object>
            {
                { "position_written", posWritten },
                { "rotation_written", rotWritten },
                { "scale_written",    scaleWritten },
                { "readback", new Dictionary<string, object>
                    {
                        { "position", V3ToDict(rbPos) },
                        { "rotation", V3ToDict(rbRot) },
                        { "scale",    V3ToDict(rbScale) },
                    }
                },
                { "verified", verified },
                { "epsilon", (double)VerifyEpsilon },
            };
        }

        // ------ helpers ------

        private static Vector3? ExtractVector3(Dictionary<string, object> parsed, string key)
        {
            if (!parsed.TryGetValue(key, out var obj) || obj == null) return null;
            if (!(obj is Dictionary<string, object> d))
                throw new ArgumentException("'" + key + "' must be an object with x/y/z");
            float x = GetFloat(d, "x", 0f, key);
            float y = GetFloat(d, "y", 0f, key);
            float z = GetFloat(d, "z", 0f, key);
            return new Vector3(x, y, z);
        }

        private static float GetFloat(Dictionary<string, object> d, string k, float def, string parentKey)
        {
            if (!d.TryGetValue(k, out var v) || v == null) return def;
            if (v is double dv) return (float)dv;
            if (v is float fv) return fv;
            if (v is int iv) return (float)iv;
            if (v is long lv) return (float)lv;
            throw new ArgumentException("'" + parentKey + "." + k + "' must be a number");
        }

        private static Dictionary<string, object> V3ToDict(Vector3 v)
        {
            return new Dictionary<string, object>
            {
                { "x", (double)v.x },
                { "y", (double)v.y },
                { "z", (double)v.z },
            };
        }

        private static bool ApproxEqual(Vector3 a, Vector3 b)
        {
            return Mathf.Abs(a.x - b.x) < VerifyEpsilon
                && Mathf.Abs(a.y - b.y) < VerifyEpsilon
                && Mathf.Abs(a.z - b.z) < VerifyEpsilon;
        }

        // Euler 角度模 360 比较（避免 0 vs 360 假 diff）
        private static bool ApproxEqualAngle(Vector3 a, Vector3 b)
        {
            return AngleClose(a.x, b.x) && AngleClose(a.y, b.y) && AngleClose(a.z, b.z);
        }

        private static bool AngleClose(float a, float b)
        {
            float d = Mathf.DeltaAngle(a, b);
            return Mathf.Abs(d) < 0.01f;
        }

        private static GameObject ResolveGameObjectPath(string path)
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
