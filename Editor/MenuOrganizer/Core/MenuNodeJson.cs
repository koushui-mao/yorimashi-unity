// MenuNodeJson — MenuNode <-> JSON 序列化 (M4-B, 纯逻辑层)
// 服务端能完整测. 支持 tool 层双向传输 tree 结构.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Yorimashi.Modder.Editor.MenuOrganizer
{
    public static class MenuNodeJson
    {
        public const string SentinelTag = "M4-B MENU NODE JSON";

        /// <summary>MenuNode 深度序列化为 JSON.</summary>
        public static string Serialize(MenuNode node)
        {
            var sb = new StringBuilder(256);
            SerializeInto(sb, node);
            return sb.ToString();
        }

        static void SerializeInto(StringBuilder sb, MenuNode node)
        {
            if (node == null) { sb.Append("null"); return; }
            sb.Append('{');
            sb.Append("\"name\":").Append(JsonQ(node.Name ?? ""));
            sb.Append(",\"type\":").Append((int)node.Type);
            sb.Append(",\"parameterName\":").Append(JsonQ(node.ParameterName ?? ""));
            sb.Append(",\"parameterValue\":").Append(
                node.ParameterValue.ToString("R", CultureInfo.InvariantCulture));
            if (node.SubParameterNames != null && node.SubParameterNames.Count > 0)
            {
                sb.Append(",\"subParameters\":[");
                for (int i = 0; i < node.SubParameterNames.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(JsonQ(node.SubParameterNames[i] ?? ""));
                }
                sb.Append(']');
            }
            if (!string.IsNullOrEmpty(node.IconGuid))
                sb.Append(",\"iconGuid\":").Append(JsonQ(node.IconGuid));
            if (!string.IsNullOrEmpty(node.SubMenuAssetGuid))
                sb.Append(",\"subMenuAssetGuid\":").Append(JsonQ(node.SubMenuAssetGuid));
            if (node.IsAddBucket) sb.Append(",\"isAddBucket\":true");
            if (!node.AutoMore) sb.Append(",\"autoMore\":false");
            if (node.Children != null && node.Children.Count > 0)
            {
                sb.Append(",\"children\":[");
                for (int i = 0; i < node.Children.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    SerializeInto(sb, node.Children[i]);
                }
                sb.Append(']');
            }
            sb.Append('}');
        }

        /// <summary>从 Dictionary&lt;string,object&gt; (已 parse 的 JSON) 反序列化.</summary>
        public static MenuNode Deserialize(Dictionary<string, object> raw)
        {
            if (raw == null) return null;
            var n = new MenuNode();
            if (raw.TryGetValue("name", out var name) && name is string ns) n.Name = ns;
            if (raw.TryGetValue("type", out var tv))
            {
                if (tv is int ti) n.Type = (MenuControlType)ti;
                else if (tv is long tl) n.Type = (MenuControlType)tl;
                else if (tv is double td) n.Type = (MenuControlType)(int)td;
                else if (tv is float tf) n.Type = (MenuControlType)(int)tf;
                else if (tv is string tsv && int.TryParse(tsv, out var tii)) n.Type = (MenuControlType)tii;
            }
            if (raw.TryGetValue("parameterName", out var pn) && pn is string pns) n.ParameterName = pns;
            if (raw.TryGetValue("parameterValue", out var pv))
            {
                if (pv is double pd) n.ParameterValue = (float)pd;
                else if (pv is float pf) n.ParameterValue = pf;
                else if (pv is int pi) n.ParameterValue = pi;
                else if (pv is long pl) n.ParameterValue = pl;
                else if (pv is string ps && float.TryParse(ps, NumberStyles.Any, CultureInfo.InvariantCulture, out var pff))
                    n.ParameterValue = pff;
            }
            if (raw.TryGetValue("subParameters", out var sps) && sps is List<object> spl)
            {
                foreach (var s in spl) n.SubParameterNames.Add(s?.ToString() ?? "");
            }
            if (raw.TryGetValue("iconGuid", out var ig) && ig is string igs) n.IconGuid = igs;
            if (raw.TryGetValue("subMenuAssetGuid", out var sg) && sg is string sgs) n.SubMenuAssetGuid = sgs;
            if (raw.TryGetValue("isAddBucket", out var ab) && ab is bool abb) n.IsAddBucket = abb;
            if (raw.TryGetValue("autoMore", out var am) && am is bool amb) n.AutoMore = amb;
            if (raw.TryGetValue("children", out var ch) && ch is List<object> chl)
            {
                foreach (var c in chl)
                {
                    if (c is Dictionary<string, object> cd)
                        n.Children.Add(Deserialize(cd));
                }
            }
            return n;
        }

        static string JsonQ(string s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (var ch in s)
            {
                switch (ch)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (ch < ' ') sb.Append("\\u").Append(((int)ch).ToString("x4"));
                        else sb.Append(ch);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
