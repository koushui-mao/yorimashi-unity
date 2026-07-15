// Yorimashi hierarchy/set_active — M3-B2 真改动 tool
//
// 切换 GameObject 的 activeSelf (SetActive true/false)。
// 走 YorimashiWriteToolBase：默认 dry_run=true, scope guard 强制 path 白名单。
//
// 参数：
//   path       (string, required) — GameObject 路径
//   active     (bool, required)   — 目标状态 true=显示 false=隐藏
//   dry_run    (bool, optional, default true)
//
// 返回：
//   changes item: { op:"set_active", path, from, to }
//   applied:      { from, to, readback, verified }
//
// 备份：SetActive 只翻一个 bool, 备份就是 from 字段本身, 不落额外文件
// (但仍然 Undo.RecordObject 支持 Ctrl+Z)
//
// Sentinel: "M3-B2 SET ACTIVE"
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    internal class HierarchySetActiveTool : YorimashiWriteToolBase
    {
        public const string SentinelTag = "M3-B2 SET ACTIVE";

        public override string ToolName => "hierarchy/set_active";

        private GameObject _resolvedGo;
        private bool _oldActive;
        private bool _newActive;
        private string _targetPath;

        protected override WriteToolChanges BuildPreview(Dictionary<string, object> parsed)
        {
            if (!parsed.TryGetValue("path", out var pObj) || !(pObj is string path) || string.IsNullOrEmpty(path))
                throw new ArgumentException("missing required 'path' (string)");
            if (!parsed.TryGetValue("active", out var aObj) || !(aObj is bool active))
                throw new ArgumentException("missing required 'active' (bool)");

            var go = ResolveGameObjectPath(path);
            if (go == null)
                throw new InvalidOperationException("GameObject not found at path: " + path);

            _resolvedGo = go;
            _oldActive = go.activeSelf;
            _newActive = active;
            _targetPath = path;

            var item = new Dictionary<string, object>
            {
                { "op", "set_active" },
                { "path", path },
                { "from", _oldActive },
                { "to", _newActive },
            };

            return new WriteToolChanges
            {
                Items = new List<Dictionary<string, object>> { item },
                BackupPath = null,  // bool 翻转不需要外部备份文件
                Summary = "will set active on '" + path + "': " + _oldActive + " → " + _newActive,
            };
        }

        protected override Dictionary<string, object> ApplyChanges(
            Dictionary<string, object> parsed, WriteToolChanges preview)
        {
            if (_resolvedGo == null)
                throw new InvalidOperationException("preview state missing");

            Undo.RecordObject(_resolvedGo, "Yorimashi hierarchy/set_active " + _targetPath);
            _resolvedGo.SetActive(_newActive);
            EditorUtility.SetDirty(_resolvedGo);

            bool readback = _resolvedGo.activeSelf;
            bool verified = readback == _newActive;

            return new Dictionary<string, object>
            {
                { "from", _oldActive },
                { "to", _newActive },
                { "readback", readback },
                { "verified", verified },
            };
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
