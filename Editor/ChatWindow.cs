// Yorimashi ChatWindow — M1-B2 + M5a Chat
// URL + Connect/Disconnect + Send Ping + Show Tools + Log + Chat tab.
// Sentinel: "M5a CHATWINDOW"
using System;
using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Yorimashi.Modder.Editor
{
    public class ChatWindow : EditorWindow
    {
        private const string MenuPath = "Window/Yorimashi Modder";
        public const string SentinelTag = "M5a CHATWINDOW";
        private const string PrefKeyUrl = "Yorimashi.Modder.WssUrl.v3";
        private const string PrefKeyTab = "Yorimashi.Modder.Tab.v1";
        // 默认走 caddy TLS 反代 → 内网 :19871 (wss router)
        // 19871 端口本身不对公网开放 (ufw 挡)，只能走 https 443 → 反代进去。
        private const string DefaultUrl = "wss://yorimashi.koushui.online/hub/plugin?token=yoric50849e4034f3622dab2e12525";
        private const int MaxLogLines = 500;
        private static readonly string[] TabNames = new[] { "Chat", "Legacy" };

        [MenuItem(MenuPath)]
        public static void ShowWindow()
        {
            var w = GetWindow<ChatWindow>("Yorimashi Modder");
            w.minSize = new Vector2(520f, 400f);
        }

        // UI state
        private string _url;
        private Vector2 _logScroll;
        private Vector2 _chatScroll;
        private readonly List<string> _logLines = new List<string>();
        private YorimashiWssClient _client;
        private WssStatus _status = WssStatus.Disconnected;
        private int _tab = 0;

        // M5a Chat state
        private readonly List<ChatMessageView> _chatMessages = new List<ChatMessageView>();
        private string _chatInput = "";
        private string _chatConversationId;
        private bool _chatBusy = false;   // 一轮进行中禁 send
        private ChatMessageView _currentAssistantBubble;   // 正在流的 assistant 消息

        private void OnEnable()
        {
            _url = EditorPrefs.GetString(PrefKeyUrl, DefaultUrl);
            _tab = EditorPrefs.GetInt(PrefKeyTab, 0);
            _chatConversationId = "c-" + Guid.NewGuid().ToString("N").Substring(0, 8);
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

            // M5a: tab 切换
            var newTab = GUILayout.Toolbar(_tab, TabNames, GUILayout.Height(24));
            if (newTab != _tab)
            {
                _tab = newTab;
                EditorPrefs.SetInt(PrefKeyTab, _tab);
            }
            EditorGUILayout.Space(4);

            if (_tab == 0)
                DrawChatTab();
            else
                DrawLegacyTab();
        }

        private void DrawLegacyTab()
        {
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

        // ================================================================
        // M5a Chat Tab
        // ================================================================
        private void DrawChatTab()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var color = GUI.color;
                GUI.color = StatusColor(_status);
                EditorGUILayout.LabelField("● " + _status, EditorStyles.miniBoldLabel, GUILayout.Width(120));
                GUI.color = color;
                EditorGUILayout.LabelField("会话: " + (_chatConversationId ?? "-"), EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_status == WssStatus.Connected || _status == WssStatus.Connecting))
                {
                    if (GUILayout.Button("Connect", GUILayout.Height(20), GUILayout.Width(80)))
                    {
                        Connect();
                    }
                }
                if (GUILayout.Button("清空", GUILayout.Height(20), GUILayout.Width(50)))
                {
                    _chatMessages.Clear();
                    _chatConversationId = "c-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    _currentAssistantBubble = null;
                    _chatBusy = false;
                }
            }
            EditorGUILayout.Space(4);

            // 消息列表
            var listHeight = Mathf.Max(position.height - 200f, 200f);
            using (var scroll = new EditorGUILayout.ScrollViewScope(_chatScroll, GUILayout.Height(listHeight)))
            {
                _chatScroll = scroll.scrollPosition;
                foreach (var m in _chatMessages)
                    DrawChatMessage(m);
                // 输入或流式增加内容后自动滚到底
                if (Event.current.type == EventType.Repaint)
                    _chatScroll.y = float.MaxValue;
            }

            EditorGUILayout.Space(4);

            // 输入框
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(_chatBusy))
                {
                    _chatInput = EditorGUILayout.TextArea(_chatInput ?? "",
                        GUILayout.MinHeight(48), GUILayout.ExpandWidth(true));
                }
                using (new EditorGUILayout.VerticalScope(GUILayout.Width(90)))
                {
                    var canSend = !_chatBusy
                                   && _status == WssStatus.Connected
                                   && !string.IsNullOrWhiteSpace(_chatInput);
                    using (new EditorGUI.DisabledScope(!canSend))
                    {
                        var oldBg = GUI.backgroundColor;
                        GUI.backgroundColor = new Color(0.35f, 0.65f, 0.95f);
                        if (GUILayout.Button(_chatBusy ? "..." : "发送", GUILayout.Height(48)))
                        {
                            SendChat();
                        }
                        GUI.backgroundColor = oldBg;
                    }
                }
            }

            if (_status != WssStatus.Connected)
            {
                EditorGUILayout.HelpBox("wss 未连接。切到 Legacy tab 点 Connect,或点上方 Connect 按钮.", MessageType.Info);
            }
        }

        private void DrawChatMessage(ChatMessageView m)
        {
            var isUser = m.Role == "user";
            var isTool = m.Role == "tool";
            using (new EditorGUILayout.HorizontalScope())
            {
                if (isUser) GUILayout.FlexibleSpace();
                var style = new GUIStyle(EditorStyles.helpBox)
                {
                    padding = new RectOffset(10, 10, 8, 8),
                    alignment = TextAnchor.UpperLeft,
                    wordWrap = true,
                    richText = true,
                };
                if (isUser) style.normal.textColor = new Color(0.85f, 0.95f, 1f);
                if (isTool) style.normal.textColor = new Color(0.8f, 0.8f, 0.4f);

                var maxWidth = Mathf.Max(200f, position.width * 0.75f);
                var prefix = isUser ? "🧑 " : isTool ? "🔧 " : "🤖 ";
                var body = string.IsNullOrEmpty(m.Text) ? "..." : m.Text;
                GUILayout.Box(prefix + body, style, GUILayout.MaxWidth(maxWidth));
                if (!isUser) GUILayout.FlexibleSpace();
            }
            EditorGUILayout.Space(2);
        }

        private void SendChat()
        {
            if (_client == null || _status != WssStatus.Connected)
            {
                AddLog("[chat] wss 未连接");
                return;
            }
            var text = (_chatInput ?? "").Trim();
            if (string.IsNullOrEmpty(text)) return;

            _chatMessages.Add(new ChatMessageView { Role = "user", Text = text });
            _chatBusy = true;
            _chatInput = "";

            _ = _client.SendChatSendAsync(_chatConversationId, text);
            Repaint();
        }

        private void HandleChatDelta(Dictionary<string, object> p)
        {
            if (_currentAssistantBubble == null)
            {
                _currentAssistantBubble = new ChatMessageView { Role = "assistant", Text = "" };
                _chatMessages.Add(_currentAssistantBubble);
            }
            var delta = p != null && p.TryGetValue("delta", out var dv) ? dv as Dictionary<string, object> : null;
            if (delta != null && delta.TryGetValue("content", out var cv) && cv is string cs)
            {
                _currentAssistantBubble.Text += cs;
            }
        }

        private void HandleToolCall(Dictionary<string, object> p)
        {
            var name = p != null && p.TryGetValue("name", out var nv) ? nv as string : "?";
            var args = p != null && p.TryGetValue("arguments", out var av) ? av : null;
            var argsStr = args == null ? "{}" : args.ToString();
            _chatMessages.Add(new ChatMessageView {
                Role = "tool",
                Text = name + "\n    " + argsStr,
            });
            // 新的 assistant 气泡下一批 delta 起
            _currentAssistantBubble = null;
        }

        private void HandleToolResult(Dictionary<string, object> p)
        {
            // 简单在最后一个 tool 气泡后附结果摘要
            var result = p != null && p.TryGetValue("result", out var rv) ? rv : null;
            var resultStr = result == null ? "(null)" : result.ToString();
            if (resultStr.Length > 200) resultStr = resultStr.Substring(0, 200) + "…";
            _chatMessages.Add(new ChatMessageView {
                Role = "tool",
                Text = "→ " + resultStr,
            });
        }

        private void HandleChatDone(Dictionary<string, object> p)
        {
            var finish = p != null && p.TryGetValue("finish_reason", out var fv) ? fv as string : null;
            if (finish == "error")
            {
                var err = p.TryGetValue("error", out var ev) ? ev as string : "unknown";
                _chatMessages.Add(new ChatMessageView { Role = "assistant", Text = "❌ 错误: " + err });
            }
            _chatBusy = false;
            _currentAssistantBubble = null;
        }

        // ChatMessageView 内部使用的气泡数据
        [Serializable]
        internal class ChatMessageView
        {
            public string Role;
            public string Text;
        }

        // ================================================================
        // (原有内容继续)
        // ================================================================

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
            {
                AddLog("[env] direction=" + env.direction + " cid=" + (env.correlationId ?? "-") + " mcp=" + Truncate(env.mcpJson, 80));
                // M5a: notif direction 里 method=chat.* 转到 chat UI
                if (env.direction == "notif" && !string.IsNullOrEmpty(env.mcpJson))
                {
                    DispatchChatNotif(env.mcpJson);
                }
            };
            _client.Connect();
            AddLog("[ui] connect requested → " + _url);
        }

        // M5a: 解析 notif mcp 里的 method / params 分派 chat.* handler
        private void DispatchChatNotif(string mcpJson)
        {
            try
            {
                var mcp = new MiniJsonParser(mcpJson).ParseObject();
                var method = mcp != null && mcp.TryGetValue("method", out var mv) ? mv as string : null;
                if (string.IsNullOrEmpty(method) || !method.StartsWith("chat.")) return;
                var paramsObj = mcp.TryGetValue("params", out var pv) ? pv as Dictionary<string, object> : null;
                if (paramsObj == null) return;

                EditorApplication.delayCall += () =>
                {
                    switch (method)
                    {
                        case "chat.delta": HandleChatDelta(paramsObj); break;
                        case "chat.tool_call": HandleToolCall(paramsObj); break;
                        case "chat.tool_result": HandleToolResult(paramsObj); break;
                        case "chat.done": HandleChatDone(paramsObj); break;
                    }
                    Repaint();
                };
            }
            catch (Exception e)
            {
                AddLog("[chat] dispatch notif failed: " + e.Message);
            }
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
            public const string Text = "0.7.0";
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
