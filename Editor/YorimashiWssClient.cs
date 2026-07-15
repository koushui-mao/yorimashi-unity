// Yorimashi WSS Client — M1-B2
// M1-B1 起点：wss 客户端 + envelope round-trip PASS。
// M1-B2 增量：
//   - 收到 direction=req 且 method=tools/list → 回 tools list result
//   - 收到 direction=req 且 method=tools/call → 主线程调 registry → 回 result 或 error
//   - JSON-RPC 2.0 error 语义：-32601 method not found, -32602 invalid params, -32603 internal
//   - 后台 receive loop 里 fire-and-forget dispatch 一个 async 任务处理请求（不阻塞 receive）
//
// Sentinel: "M1-B2 WSSCLIENT"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Yorimashi.Modder.Editor
{
    public enum WssStatus
    {
        Disconnected,
        Connecting,
        Connected,
        Reconnecting,
        Failed,
    }

    public class YorimashiWssClient : IDisposable
    {
        public const string SentinelTag = "M1-C5 WSSCLIENT";

        // Public state
        public WssStatus Status { get; private set; } = WssStatus.Disconnected;
        public string SessionId { get; private set; }
        public string LastError { get; private set; }

        // Events
        public event Action<WssStatus> OnStatusChanged;
        public event Action<string> OnLog;
        public event Action<Envelope> OnEnvelope;

        // Config
        private readonly Uri _uri;
        private readonly TimeSpan _connectTimeout = TimeSpan.FromSeconds(5);

        // Runtime
        private ClientWebSocket _socket;
        private CancellationTokenSource _cts;
        private Task _receiveTask;
        private long _correlationCounter;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private bool _pumpRegistered;

        public YorimashiWssClient(string url)
        {
            if (string.IsNullOrEmpty(url)) throw new ArgumentException("url is empty");
            _uri = new Uri(url);
            SessionId = "unity_" + Guid.NewGuid().ToString("N").Substring(0, 8);
            RegisterPump();
            // Ensure tool registry is warm before the first inbound req arrives.
            YorimashiToolRegistry.EnsureBooted();
        }

        // ---- Public API ---------------------------------------------------

        public async void Connect()
        {
            if (Status == WssStatus.Connecting || Status == WssStatus.Connected)
            {
                Log("[connect] already " + Status + ", ignore");
                return;
            }
            SetStatus(WssStatus.Connecting);
            LastError = null;
            _socket = new ClientWebSocket();
            _cts = new CancellationTokenSource();

            try
            {
                using (var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token))
                {
                    connectCts.CancelAfter(_connectTimeout);
                    // Extract ?token=xxx from URL and put into Authorization: Bearer header
                    // so the server's legacy string-token check passes. Query token itself
                    // is stripped from the actual wss URI before Connect() (some servers
                    // reject unknown query params).
                    var (cleanUri, bearerToken) = StripTokenFromUri(_uri);
                    if (!string.IsNullOrEmpty(bearerToken))
                    {
                        _socket.Options.SetRequestHeader("Authorization", "Bearer " + bearerToken);
                        Log("[connect] using Bearer token from URL (len=" + bearerToken.Length + ")");
                    }
                    await _socket.ConnectAsync(cleanUri, connectCts.Token);
                }
                SetStatus(WssStatus.Connected);
                Log("[connect] handshake ok → " + _uri);
                _receiveTask = Task.Run(() => ReceiveLoop(_cts.Token));
                await SendPingAsync();
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Log("[connect] FAIL " + e.GetType().Name + ": " + e.Message);
                SetStatus(WssStatus.Failed);
                TryCleanup();
            }
        }

        public async void Disconnect()
        {
            if (Status == WssStatus.Disconnected) return;
            Log("[disconnect] closing...");
            try
            {
                _cts?.Cancel();
                if (_socket != null && (_socket.State == WebSocketState.Open || _socket.State == WebSocketState.CloseReceived))
                {
                    await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "client disconnect", CancellationToken.None);
                }
            }
            catch (Exception e)
            {
                Log("[disconnect] error " + e.Message);
            }
            finally
            {
                TryCleanup();
                SetStatus(WssStatus.Disconnected);
                Log("[disconnect] done");
            }
        }

        public async Task SendPingAsync()
        {
            if (Status != WssStatus.Connected) throw new InvalidOperationException("not connected");
            var cid = System.Threading.Interlocked.Increment(ref _correlationCounter).ToString();
            var mcp = "{\"jsonrpc\":\"2.0\",\"id\":" + cid + ",\"method\":\"yorimashi/ping\",\"params\":{}}";
            var env = new Envelope
            {
                v = Envelope.Version,
                sessionId = SessionId,
                direction = "req",
                correlationId = cid,
                mcpJson = mcp,
            };
            Log("[send] req cid=" + cid + " method=yorimashi/ping");
            await SendEnvelopeAsync(env);
        }

        // ---- Receive loop -------------------------------------------------

        private async Task ReceiveLoop(CancellationToken ct)
        {
            var buf = new byte[64 * 1024];
            var accum = new System.IO.MemoryStream();
            try
            {
                while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
                {
                    accum.SetLength(0);
                    WebSocketReceiveResult result;
                    do
                    {
                        result = await _socket.ReceiveAsync(new ArraySegment<byte>(buf), ct);
                        if (result.MessageType == WebSocketMessageType.Close)
                        {
                            Log("[recv] server close code=" + result.CloseStatus + " reason=" + result.CloseStatusDescription);
                            return;
                        }
                        accum.Write(buf, 0, result.Count);
                    } while (!result.EndOfMessage);

                    var text = Encoding.UTF8.GetString(accum.GetBuffer(), 0, (int)accum.Length);
                    Envelope env;
                    try
                    {
                        env = YorimashiEnvelope.Decode(text);
                    }
                    catch (Exception e)
                    {
                        Log("[recv] decode fail: " + e.Message + " text=" + Truncate(text, 120));
                        continue;
                    }
                    Log("[recv] " + env.direction + " cid=" + (env.correlationId ?? "-") + " bytes=" + text.Length);
                    var envCopy = env;
                    Post(() => OnEnvelope?.Invoke(envCopy));

                    if (env.direction == "req")
                    {
                        // Fire-and-forget: don't block receive loop while handler runs.
                        _ = HandleInboundRequestAsync(envCopy, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception e)
            {
                LastError = e.Message;
                Log("[recv] loop exit " + e.GetType().Name + ": " + e.Message);
                SetStatus(WssStatus.Failed);
            }
        }

        // ---- Request handling --------------------------------------------

        private async Task HandleInboundRequestAsync(Envelope reqEnv, CancellationToken ct)
        {
            try
            {
                var parser = new MiniJsonParser(reqEnv.mcpJson);
                var mcp = parser.ParseObject();
                mcp.TryGetValue("method", out var methodObj);
                var method = methodObj as string ?? "";
                object idObj = null;
                mcp.TryGetValue("id", out idObj);
                var idJson = FormatId(idObj);

                if (method == "tools/list")
                {
                    var resultJson = BuildToolsListResult();
                    await SendResponseAsync(reqEnv, idJson, resultJson: resultJson);
                    return;
                }
                if (method == "tools/call")
                {
                    // params.name + params.arguments
                    mcp.TryGetValue("params", out var pObj);
                    if (!(pObj is Dictionary<string, object> pDict))
                    {
                        await SendErrorAsync(reqEnv, idJson, -32602, "params must be object");
                        return;
                    }
                    pDict.TryGetValue("name", out var nameObj);
                    var toolName = nameObj as string;
                    if (string.IsNullOrEmpty(toolName))
                    {
                        await SendErrorAsync(reqEnv, idJson, -32602, "params.name required");
                        return;
                    }
                    // Extract raw params.arguments JSON substring (mcp.params.arguments)
                    // via top-level re-parse to get raw span.
                    string argsRaw = null;
                    if (parser.TryGetRawSpan("params", out var paramsRaw))
                    {
                        try
                        {
                            var pParser = new MiniJsonParser(paramsRaw);
                            pParser.ParseObject();
                            pParser.TryGetRawSpan("arguments", out argsRaw);
                        }
                        catch { }
                    }
                    Log("[req] tools/call " + toolName + " args=" + Truncate(argsRaw ?? "null", 100));
                    try
                    {
                        var toolResult = await YorimashiToolRegistry.DispatchAsync(toolName, argsRaw, ct);
                        var contentJson = BuildContentResult(toolResult);
                        await SendResponseAsync(reqEnv, idJson, resultJson: contentJson);
                    }
                    catch (ToolNotFoundException)
                    {
                        await SendErrorAsync(reqEnv, idJson, -32601, "tool not found: " + toolName);
                    }
                    catch (Exception e)
                    {
                        Log("[req] tool handler threw " + e.GetType().Name + ": " + e.Message);
                        await SendErrorAsync(reqEnv, idJson, -32603, e.GetType().Name + ": " + e.Message);
                    }
                    return;
                }

                // Unknown method
                await SendErrorAsync(reqEnv, idJson, -32601, "method not found: " + method);
            }
            catch (Exception e)
            {
                Log("[req] handler outer exception " + e.GetType().Name + ": " + e.Message);
            }
        }

        private static string FormatId(object idObj)
        {
            if (idObj == null) return "null";
            if (idObj is double d)
            {
                if (d == Math.Floor(d) && !double.IsInfinity(d))
                    return ((long)d).ToString();
                return d.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (idObj is string s) return YorimashiEnvelope.EncodeString(s);
            return "null";
        }

        private string BuildToolsListResult()
        {
            var list = YorimashiToolRegistry.ListTools();
            var sb = new StringBuilder();
            sb.Append("{\"tools\":[");
            for (int i = 0; i < list.Count; i++)
            {
                if (i > 0) sb.Append(',');
                var t = list[i];
                sb.Append("{\"name\":").Append(YorimashiEnvelope.EncodeString(t.name));
                sb.Append(",\"description\":").Append(YorimashiEnvelope.EncodeString(t.description ?? ""));
                sb.Append(",\"inputSchema\":").Append(t.inputSchemaJson ?? "{}");
                sb.Append('}');
            }
            sb.Append("]}");
            return sb.ToString();
        }

        private string BuildContentResult(string toolResultJson)
        {
            // MCP content wrapper (single text entry). Server can peel this open.
            var sb = new StringBuilder();
            sb.Append("{\"content\":[{\"type\":\"text\",\"text\":");
            sb.Append(YorimashiEnvelope.EncodeString(toolResultJson));
            sb.Append("}],\"isError\":false}");
            return sb.ToString();
        }

        private Task SendResponseAsync(Envelope reqEnv, string idJson, string resultJson)
        {
            var mcp = "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"result\":" + resultJson + "}";
            var env = new Envelope
            {
                v = Envelope.Version,
                sessionId = reqEnv.sessionId,
                direction = "res",
                correlationId = reqEnv.correlationId,
                mcpJson = mcp,
            };
            Log("[send] res cid=" + (reqEnv.correlationId ?? "-") + " bytes=" + resultJson.Length);
            return SendEnvelopeAsync(env);
        }

        private Task SendErrorAsync(Envelope reqEnv, string idJson, int code, string message)
        {
            var errJson = "{\"code\":" + code + ",\"message\":" + YorimashiEnvelope.EncodeString(message) + "}";
            var mcp = "{\"jsonrpc\":\"2.0\",\"id\":" + idJson + ",\"error\":" + errJson + "}";
            var env = new Envelope
            {
                v = Envelope.Version,
                sessionId = reqEnv.sessionId,
                direction = "res",
                correlationId = reqEnv.correlationId,
                mcpJson = mcp,
            };
            Log("[send] error cid=" + (reqEnv.correlationId ?? "-") + " code=" + code + " msg=" + message);
            return SendEnvelopeAsync(env);
        }

        private async Task SendEnvelopeAsync(Envelope env)
        {
            var text = YorimashiEnvelope.Encode(env);
            var buf = Encoding.UTF8.GetBytes(text);
            await _sendLock.WaitAsync(_cts.Token);
            try
            {
                await _socket.SendAsync(new ArraySegment<byte>(buf), WebSocketMessageType.Text, true, _cts.Token);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        // ---- Main-thread pump --------------------------------------------

        private void RegisterPump()
        {
            if (_pumpRegistered) return;
            EditorApplication.update += Pump;
            _pumpRegistered = true;
        }

        private void UnregisterPump()
        {
            if (!_pumpRegistered) return;
            EditorApplication.update -= Pump;
            _pumpRegistered = false;
        }

        private void Pump()
        {
            int max = 32;
            while (max-- > 0 && _mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); } catch (Exception e) { UnityEngine.Debug.LogException(e); }
            }
        }

        private void Post(Action action) => _mainThreadQueue.Enqueue(action);

        // ---- helpers -----------------------------------------------------

        private void SetStatus(WssStatus s)
        {
            Status = s;
            Post(() => OnStatusChanged?.Invoke(s));
        }

        private void Log(string line)
        {
            Post(() => OnLog?.Invoke("[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + line));
        }

        private static (Uri clean, string token) StripTokenFromUri(Uri uri)
        {
            if (string.IsNullOrEmpty(uri.Query)) return (uri, null);
            string q = uri.Query.TrimStart('?');
            string token = null;
            var keptParts = new System.Collections.Generic.List<string>();
            foreach (var part in q.Split('&'))
            {
                if (part.StartsWith("token=", StringComparison.OrdinalIgnoreCase))
                {
                    token = Uri.UnescapeDataString(part.Substring(6));
                }
                else if (part.Length > 0)
                {
                    keptParts.Add(part);
                }
            }
            var builder = new UriBuilder(uri) { Query = string.Join("&", keptParts) };
            return (builder.Uri, token);
        }

        private static string Truncate(string s, int n) => (s == null || s.Length <= n) ? s : s.Substring(0, n) + "...";

        private void TryCleanup()
        {
            try { _socket?.Dispose(); } catch { }
            _socket = null;
            try { _cts?.Dispose(); } catch { }
            _cts = null;
            _receiveTask = null;
        }

        public void Dispose()
        {
            try { Disconnect(); } catch { }
            UnregisterPump();
        }
    }
}
