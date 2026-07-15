// Yorimashi Envelope — M1-B1
// C# 对齐 server/mcp_wss_frame.py 的 envelope v=1 语义。
//
// 只做 encode/decode + 基本 shape 校验，不做 JSON-RPC 深度校验（服务器侧会兜底）。
// 用 Unity 内建的 JsonUtility 处理不了嵌套动态字段（mcp 里方法名/参数千变万化），
// 所以自己用一个极简的 JSON writer 手写 envelope 骨架，mcp 子对象在传入前
// 已经是 JSON 字符串，直接嵌入。
//
// Sentinel: "M1-B1 ENVELOPE" (checked by verify script).
using System;
using System.Text;

namespace Yorimashi.Modder.Editor
{
    /// <summary>
    /// Represents a decoded envelope. mcpJson is the raw JSON string of the "mcp" sub-object.
    /// </summary>
    public struct Envelope
    {
        public int v;
        public string sessionId;
        public string direction; // req | res | notif
        public string correlationId; // may be null
        public string mcpJson; // raw JSON of mcp sub-object

        public const int Version = 1;
        public const string SentinelTag = "M1-B2 ENVELOPE";
    }

    public static class YorimashiEnvelope
    {
        /// <summary>
        /// Serialize an envelope to JSON text. mcpJson MUST already be a valid JSON object literal.
        /// </summary>
        public static string Encode(Envelope env)
        {
            if (string.IsNullOrEmpty(env.sessionId))
                throw new ArgumentException("session_id must be non-empty");
            if (env.direction != "req" && env.direction != "res" && env.direction != "notif")
                throw new ArgumentException("direction must be req|res|notif, got " + env.direction);
            if (string.IsNullOrEmpty(env.mcpJson))
                throw new ArgumentException("mcpJson must be non-empty");

            var sb = new StringBuilder(256);
            sb.Append('{');
            sb.Append("\"v\":").Append(env.v == 0 ? Envelope.Version : env.v).Append(',');
            sb.Append("\"session_id\":").Append(EncodeString(env.sessionId)).Append(',');
            sb.Append("\"direction\":").Append(EncodeString(env.direction)).Append(',');
            sb.Append("\"correlation_id\":");
            if (env.correlationId == null) sb.Append("null");
            else sb.Append(EncodeString(env.correlationId));
            sb.Append(',');
            sb.Append("\"mcp\":").Append(env.mcpJson);
            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>
        /// Parse envelope JSON. Extracts v/session_id/direction/correlation_id and the raw
        /// mcp sub-object as a JSON string (mcpJson).
        /// </summary>
        public static Envelope Decode(string text)
        {
            if (string.IsNullOrEmpty(text))
                throw new FormatException("envelope text is empty");

            // Use MiniJson-style tokenizing to be safe on nested objects/arrays in mcp.
            var parser = new MiniJsonParser(text);
            var root = parser.ParseObject();

            var env = new Envelope();
            if (!root.TryGetValue("v", out var vTok) || !(vTok is double vNum))
                throw new FormatException("envelope missing v");
            env.v = (int)vNum;
            if (env.v != Envelope.Version)
                throw new FormatException("envelope version mismatch: expected " + Envelope.Version + ", got " + env.v);

            if (!root.TryGetValue("session_id", out var sidTok) || !(sidTok is string sid))
                throw new FormatException("envelope missing session_id");
            env.sessionId = sid;

            if (!root.TryGetValue("direction", out var dirTok) || !(dirTok is string dir))
                throw new FormatException("envelope missing direction");
            if (dir != "req" && dir != "res" && dir != "notif")
                throw new FormatException("envelope direction invalid: " + dir);
            env.direction = dir;

            if (root.TryGetValue("correlation_id", out var corrTok))
            {
                if (corrTok == null) env.correlationId = null;
                else if (corrTok is string cs) env.correlationId = cs;
                else throw new FormatException("correlation_id must be string or null");
            }

            // mcp sub-object: re-extract as raw JSON substring using span from parser.
            if (!parser.TryGetRawSpan("mcp", out var mcpRaw))
                throw new FormatException("envelope missing mcp");
            env.mcpJson = mcpRaw;

            return env;
        }

        internal static string EncodeString(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < 0x20)
                            sb.AppendFormat("\\u{0:X4}", (int)ch);
                        else
                            sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
