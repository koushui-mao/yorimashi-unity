// Yorimashi ChatWindow — M1-B2
// URL + Connect/Disconnect + Send Ping + Show Tools + Log.
// Sentinel: "M1-B2 CHATWINDOW"
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    public class ChatWindow : EditorWindow
    {
        private const string MenuPath = "Window/Yorimashi Modder";
        public const string SentinelTag = "M1-B2 CHATWINDOW";
        private const string PrefKeyUrl = "Yorimashi.Modder.WssUrl.v3";
        // 默认走 caddy TLS 反代 → 内网 :19871 (wss router)
        // 19871 端口本身不对公网开放 (ufw 挡)，只能走 https 443 → 反代进去。
        private const string DefaultUrl = "wss://yorimashi.koushui.online/hub/plugin?token=yoric50849e4034f3622dab2e12525";
        private const int MaxLogLines = 500;

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            var w = GetWindow<ChatWindow>("Yorimashi Modder");
            w.minSize = new Vector2(480f, 320f);
        }

        // UI state
        private string _url;
        private Vector2 _logScroll;
        private readonly List<string> _logLines = new List<string>();
        private YorimashiWssClient _client;
        private WssStatus _status = WssStatus.Disconnected;

        private void OnEnable()
        {
            _url = EditorPrefs.GetString(PrefKeyUrl, DefaultUrl);
            AddLog("[init] Yorimashi Modder " + Version.Text + " ready. Sentinel=" + SentinelTag);
        }

        private void OnDisable()
        {
            _client?.Dispose();
            _client = null;
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("Yorimashi Modder  v" + Version.Text, EditorStyles.boldLabel);
            DrawUpdateBanner();
            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Server URL:", GUILayout.Width(80));
                var newUrl = EditorGUILayout.TextField(_url);
                if (newUrl != _url)
                {
                    _url = newUrl;
                    EditorPrefs.SetString(PrefKeyUrl, _url);
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Status:", GUILayout.Width(80));
                var color = GUI.color;
                GUI.color = StatusColor(_status);
                EditorGUILayout.LabelField(_status.ToString(), EditorStyles.boldLabel);
                GUI.color = color;
            }

            EditorGUILayout.Space();

            using (new EditorGUILayout.HorizontalScope())
            {
                var canConnect = _status == WssStatus.Disconnected || _status == WssStatus.Failed;
                using (new EditorGUI.DisabledScope(!canConnect))
                {
                    if (GUILayout.Button("Connect", GUILayout.Height(24)))
                    {
                        Connect();
                    }
                }

                var canDisconnect = _status == WssStatus.Connected || _status == WssStatus.Connecting;
                using (new EditorGUI.DisabledScope(!canDisconnect))
                {
                    if (GUILayout.Button("Disconnect", GUILayout.Height(24)))
                    {
                        _client?.Disconnect();
                    }
                }

                using (new EditorGUI.DisabledScope(_status != WssStatus.Connected))
                {
                    if (GUILayout.Button("Send Ping", GUILayout.Height(24)))
                    {
                        _ = _client?.SendPingAsync();
                    }
                }

                if (GUILayout.Button("Clear Log", GUILayout.Height(24), GUILayout.Width(90)))
                {
                    _logLines.Clear();
                }

                if (GUILayout.Button("检查更新", GUILayout.Height(24), GUILayout.Width(90)))
                {
                    UpdateChecker.CheckNow();
                    AddLog("[update] 手动检查已触发，查询 https://yorimashi.koushui.online/dist/latest.json ...");
                    // 轮询等结果最多 15s (HTTP 通常 <2s；Editor 不阻塞)
                    var deadline = EditorApplication.timeSinceStartup + 15.0;
                    EditorApplication.CallbackFunction pollCb = null;
                    pollCb = () =>
                    {
                        var v = UpdateChecker.LatestVersion;
                        var err = UpdateChecker.LastError;
                        if (!string.IsNullOrEmpty(err))
                        {
                            AddLog("[update] 检查失败: " + err);
                            EditorApplication.update -= pollCb;
                        }
                        else if (!string.IsNullOrEmpty(v))
                        {
                            if (UpdateChecker.HasUpdate)
                                AddLog($"[update] 发现新版本 v{v}（当前 v{Version.Text}），红条已弹。");
                            else
                                AddLog($"[update] 已是最新 v{Version.Text}（服务器 latest={v}）。");
                            EditorApplication.update -= pollCb;
                        }
                        else if (EditorApplication.timeSinceStartup > deadline)
                        {
                            AddLog("[update] 15s 内未收到响应，见 Console 或稍后重试。");
                            EditorApplication.update -= pollCb;
                        }
                    };
                    EditorApplication.update += pollCb;
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Show Tools", GUILayout.Height(20)))
                {
                    var tools = YorimashiToolRegistry.ListTools();
                    AddLog("[tools] registered " + tools.Count + " tool(s):");
                    foreach (var t in tools)
                    {
                        AddLog("  - " + t.name + " — " + t.description);
                    }
                }

                // M3-T2 Self Test：一键跑 4 个只读 tool，报告落桌面
                // 不需要 wss 连接、不需要 Python、不上网
                var selfTestColor = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.4f, 0.7f, 1.0f);
                if (GUILayout.Button("Run Self Test", GUILayout.Height(20)))
                {
                    RunSelfTest();
                }
                GUI.backgroundColor = selfTestColor;
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Log:", EditorStyles.miniBoldLabel);
            using (var scroll = new EditorGUILayout.ScrollViewScope(_logScroll, GUILayout.MinHeight(180)))
            {
                _logScroll = scroll.scrollPosition;
                foreach (var line in _logLines)
                {
                    EditorGUILayout.SelectableLabel(line, GUILayout.Height(16));
                }
            }
        }

        private void RunSelfTest()
        {
            AddLog("[selftest] 开始跑 M3-T2 只读 tool 自测（4 步）...");
            try
            {
                var summary = SelfTestRunner.Run(AddLog);
                // 弹一个原生对话框，好友一眼看到结果 + 打开报告文件夹按钮
                var msg = "结果: " + summary.PassCount + " PASS / " + summary.FailCount + " FAIL"
                          + "\n\nzip: " + summary.ZipPath
                          + "\n\n把 zip 发给依代作者即可。";
                bool openFolder = EditorUtility.DisplayDialog(
                    "Yorimashi Self Test 完成",
                    msg,
                    "打开报告文件夹",
                    "关闭");
                if (openFolder)
                {
                    EditorUtility.RevealInFinder(summary.ZipPath);
                }
            }
            catch (System.Exception ex)
            {
                AddLog("[selftest] ❌ Self Test 异常: " + ex.GetType().Name + ": " + ex.Message);
                EditorUtility.DisplayDialog(
                    "Yorimashi Self Test 异常",
                    ex.GetType().Name + ": " + ex.Message + "\n\n看 Chat Log 里的 [selftest] 行。",
                    "OK");
            }
        }

        private void Connect()
        {
            _client?.Dispose();
            _client = new YorimashiWssClient(_url);
            _client.OnStatusChanged += OnStatus;
            _client.OnLog += AddLog;
            _client.OnEnvelope += env =>
                AddLog("[env] direction=" + env.direction + " cid=" + (env.correlationId ?? "-") + " mcp=" + Truncate(env.mcpJson, 80));
            _client.Connect();
            AddLog("[ui] connect requested → " + _url);
        }

        private void OnStatus(WssStatus s)
        {
            _status = s;
            AddLog("[status] " + s);
            Repaint();
        }

        private void AddLog(string line)
        {
            _logLines.Add(line);
            if (_logLines.Count > MaxLogLines)
                _logLines.RemoveRange(0, _logLines.Count - MaxLogLines);
            _logScroll.y = float.MaxValue;
            Repaint();
        }

        private static string Truncate(string s, int n) => (s == null || s.Length <= n) ? s : s.Substring(0, n) + "...";

        private static Color StatusColor(WssStatus s)
        {
            switch (s)
            {
                case WssStatus.Connected: return new Color(0.4f, 0.9f, 0.4f);
                case WssStatus.Connecting: case WssStatus.Reconnecting: return new Color(0.9f, 0.8f, 0.3f);
                case WssStatus.Failed: return new Color(0.95f, 0.4f, 0.4f);
                default: return Color.white;
            }
        }

        internal static class Version
        {
            // 保持与 package.json 手动同步。UpdateChecker 会在启动时比对私有 registry 最新版。
            public const string Text = "0.4.0-m3e.10";
            public const string PackageName = "com.yorimashi.modder";
            public const string RegistryUrl = "https://yorimashi.koushui.online/registry/";
        }

        // -------- 更新条 UI (v0.5.0: 冷启动更新) --------
        // 三态: (1) 未下载新版 (2) 下载中 (3) 已下载, 等下次开 Unity 生效
        private void DrawUpdateBanner()
        {
            var pending = UpdateChecker.PendingVersion;
            if (!string.IsNullOrEmpty(pending))
            {
                // 状态 3: 已下载, 等冷启动
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    var oldColor = GUI.color;
                    GUI.color = new Color(0.4f, 0.75f, 0.4f);
                    EditorGUILayout.LabelField(
                        $"✅ 已下载 v{pending} — 下次开 Unity 时自动生效 (无需操作)",
                        EditorStyles.boldLabel,
                        GUILayout.ExpandWidth(true));
                    GUI.color = oldColor;
                }
                EditorGUILayout.Space(2);
                return;
            }

            if (UpdateChecker.IsDownloading)
            {
                // 状态 2: 下载中
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    var oldColor = GUI.color;
                    GUI.color = new Color(0.35f, 0.55f, 0.85f);
                    EditorGUILayout.LabelField(
                        "⏬ 后台下载新版本中...",
                        EditorStyles.boldLabel,
                        GUILayout.ExpandWidth(true));
                    GUI.color = oldColor;
                }
                EditorGUILayout.Space(2);
                return;
            }

            if (UpdateChecker.HasUpdate)
            {
                // 状态 1: 检测到新版, 未下载
                var latest = UpdateChecker.LatestVersion;
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    var oldColor = GUI.color;
                    GUI.color = new Color(1f, 0.65f, 0.3f);
                    EditorGUILayout.LabelField(
                        $"发现新版 v{latest}（当前 v{Version.Text}）",
                        EditorStyles.boldLabel,
                        GUILayout.ExpandWidth(true));
                    GUI.color = oldColor;
                    if (GUILayout.Button("下载更新", GUILayout.Width(90), GUILayout.Height(22)))
                    {
                        UpdateChecker.StartDownload();
                    }
                    if (GUILayout.Button("×", GUILayout.Width(22), GUILayout.Height(22)))
                    {
                        UnityEditor.SessionState.EraseString("Yorimashi.UpdateChecker.LatestVersion");
                    }
                }
                EditorGUILayout.Space(2);
                return;
            }

            // 无更新时不显示任何东西
        }

        private static void DrawBanner(string msg, Color tint)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var old = GUI.color;
                GUI.color = tint;
                EditorGUILayout.LabelField(msg, EditorStyles.boldLabel);
                GUI.color = old;
            }
            EditorGUILayout.Space(2);
        }
    }
}
