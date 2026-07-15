// Yorimashi Tool Registry — M1-B2
// 静态注册表 + dispatcher。给 wss 层调用：dispatch("scene/create_cube", paramsJson)。
//
// 关键约束：所有 tool handler 必须能安全跑在主线程。
// 后台 receive loop 用 Dispatch 会把 handler 压入 UI pump 队列，用 TaskCompletionSource
// 桥回后台线程等结果，这样 handler 本体可以直接调 UnityEditor API 而无需再 marshal。
//
// Sentinel: "M1-B2 REGISTRY"
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;

namespace Yorimashi.Modder.Editor
{
    /// <summary>
    /// Handler signature: takes params JSON (may be null), returns result JSON.
    /// Handler runs on Unity main thread (Editor context).
    /// </summary>
    public delegate string ToolHandler(string paramsJson);

    public struct ToolInfo
    {
        public string name;
        public string description;
        public string inputSchemaJson; // JSON schema literal
    }

    public static class YorimashiToolRegistry
    {
        public const string SentinelTag = "M1-B2 REGISTRY";

        private static readonly Dictionary<string, (ToolInfo info, ToolHandler handler)> _tools
            = new Dictionary<string, (ToolInfo, ToolHandler)>(StringComparer.Ordinal);

        // Main-thread pump: pending callbacks queued by background threads.
        private static readonly ConcurrentQueue<Action> _mainThreadQueue = new ConcurrentQueue<Action>();
        private static bool _pumpRegistered;
        private static readonly object _bootLock = new object();
        private static bool _booted;

        /// <summary>
        /// Ensure the registry has been populated with built-in tools and the
        /// main-thread pump is registered on EditorApplication.update.
        /// Safe to call multiple times; only runs once.
        /// </summary>
        public static void EnsureBooted()
        {
            lock (_bootLock)
            {
                if (_booted) return;
                RegisterPump();
                YorimashiTools.RegisterAll();
                _booted = true;
            }
        }

        public static void Register(ToolInfo info, ToolHandler handler)
        {
            if (string.IsNullOrEmpty(info.name)) throw new ArgumentException("tool name empty");
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            _tools[info.name] = (info, handler);
        }

        public static List<ToolInfo> ListTools()
        {
            EnsureBooted();
            var list = new List<ToolInfo>(_tools.Count);
            foreach (var kv in _tools) list.Add(kv.Value.info);
            return list;
        }

        /// <summary>
        /// M3-T2-Self-Test：主线程同步调用 tool handler。
        /// 用于 ChatWindow 里的 Self Test 按钮 / 未来其他 Editor 内工具直接调 tool，
        /// 不需要走 wss → dispatch async → pump 一整圈。
        ///
        /// **必须**在主线程调用（Editor UI callback 天然是主线程，安全）。
        /// tool 未注册 → 抛 ToolNotFoundException。
        /// tool handler 抛异常 → 原样上抛（caller 自己 try/catch）。
        /// </summary>
        public static string InvokeOnMainThread(string toolName, string paramsJson)
        {
            EnsureBooted();
            if (!_tools.TryGetValue(toolName, out var entry))
                throw new ToolNotFoundException(toolName);
            return entry.handler(paramsJson) ?? "null";
        }

        /// <summary>
        /// Marshal handler onto main thread, return result JSON via awaitable Task.
        /// Called from background wss receive loop.
        /// </summary>
        public static Task<string> DispatchAsync(string toolName, string paramsJson, CancellationToken ct)
        {
            EnsureBooted();
            if (!_tools.TryGetValue(toolName, out var entry))
            {
                return Task.FromException<string>(new ToolNotFoundException(toolName));
            }
            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = entry.handler;
            _mainThreadQueue.Enqueue(() =>
            {
                if (ct.IsCancellationRequested)
                {
                    tcs.TrySetCanceled(ct);
                    return;
                }
                try
                {
                    var result = handler(paramsJson);
                    tcs.TrySetResult(result ?? "null");
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
            });
            return tcs.Task;
        }

        // ---- main-thread pump ----

        private static void RegisterPump()
        {
            if (_pumpRegistered) return;
            EditorApplication.update += Pump;
            _pumpRegistered = true;
        }

        private static void Pump()
        {
            // Cap per-frame drain to avoid one-frame stalls.
            int max = 32;
            while (max-- > 0 && _mainThreadQueue.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { UnityEngine.Debug.LogException(e); }
            }
        }
    }

    public class ToolNotFoundException : Exception
    {
        public ToolNotFoundException(string name) : base("Tool not found: " + name) { }
    }
}
