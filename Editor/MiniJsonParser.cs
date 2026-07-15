// Yorimashi MiniJsonParser — M1-B1
// 精简的 JSON tokenizer，用来支持 YorimashiEnvelope.Decode:
//   - 解析 envelope 顶层对象为 Dictionary<string, object>
//   - 支持提取指定字段的原始 JSON 子串（用于 mcp 子对象透传）
//
// 只支持 UTF-16 输入（Unity 内部 string 天然是 UTF-16）。不追求 RFC 7159 完备，
// 只覆盖 envelope 会出现的场景：object / array / string / number / bool / null。
// 更严格的解析放在服务端 Python 侧做。
//
// Sentinel: "M1-B1 MINIJSON"
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Yorimashi.Modder.Editor
{
    internal class MiniJsonParser
    {
        public const string SentinelTag = "M1-B1 MINIJSON";

        private readonly string _text;
        private int _pos;
        // key -> (startPos, endPos) for raw-span extraction on top-level object.
        private readonly Dictionary<string, (int, int)> _topLevelSpans =
            new Dictionary<string, (int, int)>(StringComparer.Ordinal);
        private bool _topLevelParsed;

        public MiniJsonParser(string text)
        {
            _text = text ?? throw new ArgumentNullException(nameof(text));
            _pos = 0;
        }

        public Dictionary<string, object> ParseObject()
        {
            SkipWs();
            var result = ParseObjectImpl(recordTopLevel: !_topLevelParsed);
            _topLevelParsed = true;
            return result;
        }

        public bool TryGetRawSpan(string key, out string raw)
        {
            if (!_topLevelParsed)
            {
                // Force parse to populate spans.
                _pos = 0;
                ParseObject();
            }
            if (_topLevelSpans.TryGetValue(key, out var span))
            {
                raw = _text.Substring(span.Item1, span.Item2 - span.Item1);
                return true;
            }
            raw = null;
            return false;
        }

        // ---- internals ---------------------------------------------------

        private Dictionary<string, object> ParseObjectImpl(bool recordTopLevel)
        {
            Expect('{');
            var obj = new Dictionary<string, object>(StringComparer.Ordinal);
            SkipWs();
            if (Peek() == '}')
            {
                _pos++;
                return obj;
            }
            while (true)
            {
                SkipWs();
                var key = ParseString();
                SkipWs();
                Expect(':');
                SkipWs();
                int valueStart = _pos;
                var value = ParseValue();
                int valueEnd = _pos;
                obj[key] = value;
                if (recordTopLevel)
                {
                    _topLevelSpans[key] = (valueStart, valueEnd);
                }
                SkipWs();
                var c = Peek();
                if (c == ',') { _pos++; continue; }
                if (c == '}') { _pos++; return obj; }
                throw new FormatException("expected ',' or '}' at pos " + _pos);
            }
        }

        private List<object> ParseArray()
        {
            Expect('[');
            var arr = new List<object>();
            SkipWs();
            if (Peek() == ']')
            {
                _pos++;
                return arr;
            }
            while (true)
            {
                SkipWs();
                arr.Add(ParseValue());
                SkipWs();
                var c = Peek();
                if (c == ',') { _pos++; continue; }
                if (c == ']') { _pos++; return arr; }
                throw new FormatException("expected ',' or ']' at pos " + _pos);
            }
        }

        private object ParseValue()
        {
            SkipWs();
            var c = Peek();
            if (c == '{') return ParseObjectImpl(recordTopLevel: false);
            if (c == '[') return ParseArray();
            if (c == '"') return ParseString();
            if (c == 't' || c == 'f') return ParseBool();
            if (c == 'n') { ParseKeyword("null"); return null; }
            if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
            throw new FormatException("unexpected char '" + c + "' at pos " + _pos);
        }

        private string ParseString()
        {
            Expect('"');
            var sb = new StringBuilder();
            while (true)
            {
                if (_pos >= _text.Length) throw new FormatException("unterminated string");
                var c = _text[_pos++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    if (_pos >= _text.Length) throw new FormatException("bad escape");
                    var esc = _text[_pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_pos + 4 > _text.Length) throw new FormatException("bad \\u escape");
                            var hex = _text.Substring(_pos, 4);
                            _pos += 4;
                            sb.Append((char)ushort.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                            break;
                        default: throw new FormatException("unknown escape \\" + esc);
                    }
                }
                else
                {
                    sb.Append(c);
                }
            }
        }

        private object ParseNumber()
        {
            int start = _pos;
            if (Peek() == '-') _pos++;
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')
                    _pos++;
                else
                    break;
            }
            var span = _text.Substring(start, _pos - start);
            return double.Parse(span, CultureInfo.InvariantCulture);
        }

        private bool ParseBool()
        {
            if (Peek() == 't') { ParseKeyword("true"); return true; }
            ParseKeyword("false"); return false;
        }

        private void ParseKeyword(string kw)
        {
            if (_pos + kw.Length > _text.Length || _text.Substring(_pos, kw.Length) != kw)
                throw new FormatException("expected " + kw + " at pos " + _pos);
            _pos += kw.Length;
        }

        private void SkipWs()
        {
            while (_pos < _text.Length)
            {
                var c = _text[_pos];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') _pos++;
                else break;
            }
        }

        private char Peek()
        {
            if (_pos >= _text.Length) throw new FormatException("unexpected end of input");
            return _text[_pos];
        }

        private void Expect(char c)
        {
            if (_pos >= _text.Length || _text[_pos] != c)
                throw new FormatException("expected '" + c + "' at pos " + _pos);
            _pos++;
        }
    }
}
