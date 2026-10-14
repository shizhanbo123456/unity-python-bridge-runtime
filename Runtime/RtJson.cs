// RtJson.cs —— 纯 C# 实现的 JSON 解析与序列化，不依赖 UnityEngine，也不依赖任何第三方库。
//
// 为什么自己写：Unity 内置的 JsonUtility 不支持 Dictionary、多态、任意层级嵌套，
// 且要求返回值必须是 [Serializable] 类，用来承载"开发者自定义参数"会很快撑不住。
// 这一层让命令的入参与返回值都可以是任意结构的 JSON。
//
// 线程安全：全部为无状态静态方法，可跨线程调用。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

namespace UnityRuntimeBridge
{
    /// <summary>JSON 节点类型。</summary>
    public enum RtJsonKind
    {
        Null,
        Bool,
        Number,
        String,
        Array,
        Object
    }

    /// <summary>JSON 解析或序列化失败。</summary>
    public class RtJsonException : Exception
    {
        public RtJsonException(string message) : base(message) { }
    }

    /// <summary>标记字段/属性不参与序列化。</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class RtJsonIgnoreAttribute : Attribute
    {
    }

    /// <summary>对象的一个成员（保持书写顺序）。</summary>
    public sealed class RtJsonMember
    {
        public string Key { get; }
        public RtJsonValue Value { get; }

        public RtJsonMember(string key, RtJsonValue value)
        {
            Key = key;
            Value = value;
        }
    }

    /// <summary>JSON 值节点。数组用 Items，对象用 Members，标量用 Number/Text/Bool。</summary>
    public sealed class RtJsonValue
    {
        private readonly double _number;
        private readonly bool _bool;
        private readonly string _text;
        private readonly List<RtJsonValue> _items;
        private readonly List<RtJsonMember> _members;

        public RtJsonKind Kind { get; }

        private RtJsonValue(RtJsonKind kind, double number, bool boolValue, string text,
            List<RtJsonValue> items, List<RtJsonMember> members)
        {
            Kind = kind;
            _number = number;
            _bool = boolValue;
            _text = text;
            _items = items;
            _members = members;
        }

        public static readonly RtJsonValue Null = new RtJsonValue(RtJsonKind.Null, 0, false, null, null, null);

        public static RtJsonValue Of(bool value) => new RtJsonValue(RtJsonKind.Bool, 0, value, null, null, null);
        public static RtJsonValue Of(double value) => new RtJsonValue(RtJsonKind.Number, value, false, null, null, null);
        public static RtJsonValue Of(string value) => value == null
            ? Null
            : new RtJsonValue(RtJsonKind.String, 0, false, value, null, null);

        public static RtJsonValue Array(List<RtJsonValue> items) =>
            new RtJsonValue(RtJsonKind.Array, 0, false, null, items ?? new List<RtJsonValue>(), null);

        public static RtJsonValue Object(List<RtJsonMember> members) =>
            new RtJsonValue(RtJsonKind.Object, 0, false, null, null, members ?? new List<RtJsonMember>());

        public bool IsNull => Kind == RtJsonKind.Null;
        public bool IsArray => Kind == RtJsonKind.Array;
        public bool IsObject => Kind == RtJsonKind.Object;

        /// <summary>数组长度或对象成员数；标量返回 0。</summary>
        public int Count
        {
            get
            {
                if (Kind == RtJsonKind.Array) return _items.Count;
                if (Kind == RtJsonKind.Object) return _members.Count;
                return 0;
            }
        }

        /// <summary>数组元素；非数组返回空列表。</summary>
        public IReadOnlyList<RtJsonValue> Items =>
            (IReadOnlyList<RtJsonValue>)_items ?? System.Array.Empty<RtJsonValue>();

        /// <summary>对象成员；非对象返回空列表。</summary>
        public IReadOnlyList<RtJsonMember> Members =>
            (IReadOnlyList<RtJsonMember>)_members ?? System.Array.Empty<RtJsonMember>();

        public RtJsonValue this[string key] => Get(key);

        public RtJsonValue this[int index]
        {
            get
            {
                if (Kind != RtJsonKind.Array) return null;
                return index >= 0 && index < _items.Count ? _items[index] : null;
            }
        }

        /// <summary>按 key 取成员，不存在返回 null。</summary>
        public RtJsonValue Get(string key)
        {
            if (Kind != RtJsonKind.Object || key == null) return null;
            string name = key.Trim();
            for (int i = 0; i < _members.Count; i++)
            {
                if (string.Equals(_members[i].Key, name, StringComparison.Ordinal)) return _members[i].Value;
            }
            return null;
        }

        public bool Has(string key) => Get(key) != null;

        // ---- 标量读取（宽松：字符串里写 "3" / "true" 也能读到）----

        public string AsString(string defaultValue = null)
        {
            switch (Kind)
            {
                case RtJsonKind.String: return _text;
                case RtJsonKind.Number: return FormatNumber(_number);
                case RtJsonKind.Bool: return _bool ? "true" : "false";
                default: return defaultValue;
            }
        }

        public double AsDouble(double defaultValue = 0)
        {
            switch (Kind)
            {
                case RtJsonKind.Number: return _number;
                case RtJsonKind.Bool: return _bool ? 1 : 0;
                case RtJsonKind.String:
                    return double.TryParse(_text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                        ? d
                        : defaultValue;
                default: return defaultValue;
            }
        }

        public float AsFloat(float defaultValue = 0) => (float)AsDouble(defaultValue);

        public int AsInt(int defaultValue = 0)
        {
            double d = AsDouble(double.NaN);
            if (double.IsNaN(d)) return defaultValue;
            if (d > int.MaxValue) return int.MaxValue;
            if (d < int.MinValue) return int.MinValue;
            return (int)d;
        }

        public long AsLong(long defaultValue = 0)
        {
            double d = AsDouble(double.NaN);
            if (double.IsNaN(d)) return defaultValue;
            return (long)d;
        }

        public bool AsBool(bool defaultValue = false)
        {
            switch (Kind)
            {
                case RtJsonKind.Bool: return _bool;
                case RtJsonKind.Number: return Math.Abs(_number) > double.Epsilon;
                case RtJsonKind.String:
                    string t = _text == null ? "" : _text.Trim().ToLowerInvariant();
                    if (t == "true" || t == "1" || t == "yes" || t == "on") return true;
                    if (t == "false" || t == "0" || t == "no" || t == "off" || t == "") return false;
                    return defaultValue;
                default: return defaultValue;
            }
        }

        internal static string FormatNumber(double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) return "null";
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15)
            {
                return ((long)d).ToString(CultureInfo.InvariantCulture);
            }
            return d.ToString("R", CultureInfo.InvariantCulture);
        }

        /// <summary>把本节点写回 JSON 文本。</summary>
        public string ToJson(bool pretty = false)
        {
            var writer = new RtJsonWriter(pretty, null);
            writer.WriteNode(this);
            return writer.ToString();
        }

        public override string ToString() => ToJson();
    }

    /// <summary>JSON 文本入口：解析与序列化。</summary>
    public static class RtJson
    {
        public const int DefaultMaxDepth = 64;

        /// <summary>解析一行 JSON。失败抛 RtJsonException（含出错位置）。</summary>
        public static RtJsonValue Parse(string text, int maxDepth = DefaultMaxDepth)
        {
            if (!TryParse(text, out RtJsonValue value, out string error, maxDepth))
            {
                throw new RtJsonException(error);
            }
            return value;
        }

        public static bool TryParse(string text, out RtJsonValue value, out string error,
            int maxDepth = DefaultMaxDepth)
        {
            value = null;
            error = null;
            if (text == null)
            {
                error = "JSON 文本为 null";
                return false;
            }
            try
            {
                var parser = new RtJsonParser(text, maxDepth);
                value = parser.ParseDocument();
                return true;
            }
            catch (RtJsonException e)
            {
                error = e.Message;
                value = null;
                return false;
            }
        }

        /// <summary>把任意对象序列化为 JSON 文本。失败抛 RtJsonException。</summary>
        public static string Serialize(object value, bool pretty = false)
        {
            return Serialize(value, null, pretty);
        }

        /// <summary>
        /// 带自定义转换器的序列化。converter 返回 null 表示"我不处理，走默认反射逻辑"，
        /// 否则其返回值会被原样写入（必须是合法 JSON 片段）。Unity 类型由上层传入转换器处理。
        /// </summary>
        public static string Serialize(object value, Func<object, string> converter, bool pretty = false)
        {
            try
            {
                var writer = new RtJsonWriter(pretty, converter);
                writer.WriteValue(value);
                return writer.ToString();
            }
            catch (RtJsonException)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new RtJsonException($"序列化失败: {e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>转义为 JSON 字符串字面量（含首尾引号）。</summary>
        public static string Escape(string text)
        {
            var sb = new StringBuilder((text == null ? 0 : text.Length) + 2);
            AppendQuoted(sb, text);
            return sb.ToString();
        }

        // ---- 序列化实现 ----

        internal static void AppendQuoted(StringBuilder sb, string s)
        {
            sb.Append('"');
            if (s != null)
            {
                for (int i = 0; i < s.Length; i++)
                {
                    char c = s[i];
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
            }
            sb.Append('"');
        }
    }

    /// <summary>引用相等比较，用于序列化时的循环引用检测。</summary>
    internal sealed class RtReferenceComparer : IEqualityComparer<object>
    {
        public static readonly RtReferenceComparer Instance = new RtReferenceComparer();

        public new bool Equals(object x, object y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>把 .NET 对象写成 JSON 文本。</summary>
    internal sealed class RtJsonWriter
    {
        private const int MaxWriteDepth = 24;

        private readonly StringBuilder _sb = new StringBuilder(256);
        private readonly bool _pretty;
        private readonly Func<object, string> _converter;
        private readonly HashSet<object> _visiting = new HashSet<object>(RtReferenceComparer.Instance);
        private int _depth;

        public RtJsonWriter(bool pretty, Func<object, string> converter)
        {
            _pretty = pretty;
            _converter = converter;
        }

        public override string ToString() => _sb.ToString();

        public void WriteNode(RtJsonValue node)
        {
            if (node == null || node.Kind == RtJsonKind.Null)
            {
                _sb.Append("null");
                return;
            }
            switch (node.Kind)
            {
                case RtJsonKind.Bool:
                    _sb.Append(node.AsBool() ? "true" : "false");
                    break;
                case RtJsonKind.Number:
                    _sb.Append(RtJsonValue.FormatNumber(node.AsDouble()));
                    break;
                case RtJsonKind.String:
                    RtJson.AppendQuoted(_sb, node.AsString());
                    break;
                case RtJsonKind.Array:
                    WriteNodeArray(node.Items);
                    break;
                default:
                    WriteNodeObject(node.Members);
                    break;
            }
        }

        private void WriteNodeArray(IReadOnlyList<RtJsonValue> items)
        {
            if (items.Count == 0)
            {
                _sb.Append("[]");
                return;
            }
            _sb.Append('[');
            _depth++;
            for (int i = 0; i < items.Count; i++)
            {
                if (i > 0) _sb.Append(',');
                NewLineIndent();
                WriteNode(items[i]);
            }
            _depth--;
            NewLineIndent();
            _sb.Append(']');
        }

        private void WriteNodeObject(IReadOnlyList<RtJsonMember> members)
        {
            if (members.Count == 0)
            {
                _sb.Append("{}");
                return;
            }
            _sb.Append('{');
            _depth++;
            for (int i = 0; i < members.Count; i++)
            {
                if (i > 0) _sb.Append(',');
                NewLineIndent();
                RtJson.AppendQuoted(_sb, members[i].Key);
                _sb.Append(_pretty ? ": " : ":");
                WriteNode(members[i].Value);
            }
            _depth--;
            NewLineIndent();
            _sb.Append('}');
        }

        public void WriteValue(object value)
        {
            if (_converter != null)
            {
                string custom = _converter(value);
                if (custom != null)
                {
                    _sb.Append(custom);
                    return;
                }
            }

            if (value == null)
            {
                _sb.Append("null");
                return;
            }

            Type type = value.GetType();

            // 枚举要在 GetTypeCode 之前判断：GetTypeCode 会返回枚举的底层整型
            if (value is Enum)
            {
                RtJson.AppendQuoted(_sb, value.ToString());
                return;
            }

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean:
                    _sb.Append((bool)value ? "true" : "false");
                    return;
                case TypeCode.Char:
                case TypeCode.String:
                    RtJson.AppendQuoted(_sb, Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case TypeCode.DateTime:
                    RtJson.AppendQuoted(_sb, ((DateTime)value).ToString("o", CultureInfo.InvariantCulture));
                    return;
                case TypeCode.SByte:
                case TypeCode.Byte:
                case TypeCode.Int16:
                case TypeCode.UInt16:
                case TypeCode.Int32:
                case TypeCode.UInt32:
                case TypeCode.Int64:
                case TypeCode.UInt64:
                    // 整数必须写成数字字面量，加引号会让 Python 侧拿到字符串
                    _sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                case TypeCode.Single:
                    float f = (float)value;
                    _sb.Append(float.IsNaN(f) || float.IsInfinity(f)
                        ? "null"
                        : f.ToString("R", CultureInfo.InvariantCulture));
                    return;
                case TypeCode.Double:
                    _sb.Append(RtJsonValue.FormatNumber((double)value));
                    return;
                case TypeCode.Decimal:
                    _sb.Append(((decimal)value).ToString(CultureInfo.InvariantCulture));
                    return;
            }

            if (value is byte[] bytes)
            {
                RtJson.AppendQuoted(_sb, Convert.ToBase64String(bytes));
                return;
            }
            if (value is Guid guid)
            {
                RtJson.AppendQuoted(_sb, guid.ToString("D"));
                return;
            }
            if (value is Type t)
            {
                RtJson.AppendQuoted(_sb, t.FullName ?? t.Name);
                return;
            }
            if (value is Delegate del)
            {
                RtJson.AppendQuoted(_sb, del.Method != null ? del.Method.Name : del.GetType().Name);
                return;
            }
            if (value is IDictionary)
            {
                WriteDictionary((IDictionary)value);
                return;
            }
            if (value is IEnumerable enumerable)
            {
                WriteEnumerable(enumerable);
                return;
            }
            if (_depth >= MaxWriteDepth)
            {
                RtJson.AppendQuoted(_sb, $"<{type.Name}: 嵌套超过 {MaxWriteDepth} 层>");
                return;
            }

            WriteReflectedObject(value, type);
        }

        private void WriteDictionary(IDictionary dict)
        {
            if (dict.Count == 0)
            {
                _sb.Append("{}");
                return;
            }
            Enter(dict);
            _sb.Append('{');
            bool first = true;
            foreach (DictionaryEntry entry in dict)
            {
                if (!first) _sb.Append(',');
                first = false;
                NewLineIndent();
                RtJson.AppendQuoted(_sb, entry.Key == null ? "null" : Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                _sb.Append(_pretty ? ": " : ":");
                WriteValue(entry.Value);
            }
            Leave(dict);
            NewLineIndent();
            _sb.Append('}');
        }

        private void WriteEnumerable(IEnumerable enumerable)
        {
            Enter(enumerable);
            _sb.Append('[');
            bool first = true;
            foreach (object item in enumerable)
            {
                if (!first) _sb.Append(',');
                first = false;
                NewLineIndent();
                WriteValue(item);
            }
            Leave(enumerable);
            if (first)
            {
                _sb.Append(']');
                return;
            }
            NewLineIndent();
            _sb.Append(']');
        }

        private void WriteReflectedObject(object value, Type type)
        {
            Enter(value);
            var members = ObjectMembersCache.Get(type);

            _sb.Append('{');
            bool first = true;
            for (int i = 0; i < members.Count; i++)
            {
                object memberValue;
                try
                {
                    memberValue = members[i].Read(value);
                }
                catch (Exception)
                {
                    // 属性的 getter 抛异常（比如已销毁的 Unity 对象）——跳过该成员，不影响整体序列化
                    continue;
                }
                if (!first) _sb.Append(',');
                first = false;
                NewLineIndent();
                RtJson.AppendQuoted(_sb, members[i].Key);
                _sb.Append(_pretty ? ": " : ":");
                WriteValue(memberValue);
            }
            Leave(value);
            if (first)
            {
                _sb.Append('}');
                return;
            }
            NewLineIndent();
            _sb.Append('}');
        }

        private void Enter(object value)
        {
            if (value != null && value.GetType().IsClass)
            {
                if (!_visiting.Add(value))
                {
                    throw new RtJsonException($"序列化失败: 检测到循环引用（{value.GetType().Name}）");
                }
            }
            _depth++;
        }

        private void Leave(object value)
        {
            if (value != null && value.GetType().IsClass)
            {
                _visiting.Remove(value);
            }
            _depth--;
        }

        private void NewLineIndent()
        {
            if (!_pretty) return;
            _sb.Append('\n');
            for (int i = 0; i < _depth; i++) _sb.Append("  ");
        }
    }

    /// <summary>反射成员读取器：一个类型的字段/属性只解析一次并缓存。</summary>
    internal sealed class RtObjectMember
    {
        public string Key;
        public Func<object, object> Read;
    }

    internal static class ObjectMembersCache
    {
        private static readonly Dictionary<Type, List<RtObjectMember>> Cache =
            new Dictionary<Type, List<RtObjectMember>>();

        public static List<RtObjectMember> Get(Type type)
        {
            lock (Cache)
            {
                if (Cache.TryGetValue(type, out List<RtObjectMember> cached)) return cached;
                var list = Build(type);
                Cache[type] = list;
                return list;
            }
        }

        private static List<RtObjectMember> Build(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
            var result = new List<RtObjectMember>();
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (FieldInfo field in type.GetFields(flags))
            {
                if (field.IsStatic) continue;
                if (field.IsDefined(typeof(RtJsonIgnoreAttribute), true)) continue;
                if (!names.Add(field.Name)) continue;
                FieldInfo captured = field;
                result.Add(new RtObjectMember
                {
                    Key = field.Name,
                    Read = target => captured.GetValue(target)
                });
            }

            foreach (PropertyInfo property in type.GetProperties(flags))
            {
                if (!property.CanRead) continue;
                if (property.GetIndexParameters().Length > 0) continue;
                if (property.GetGetMethod() == null) continue;
                if (property.IsDefined(typeof(RtJsonIgnoreAttribute), true)) continue;
                if (!names.Add(property.Name)) continue;
                PropertyInfo captured = property;
                result.Add(new RtObjectMember
                {
                    Key = property.Name,
                    Read = target => captured.GetValue(target, null)
                });
            }

            return result;
        }
    }

    /// <summary>递归下降 JSON 解析器。</summary>
    internal sealed class RtJsonParser
    {
        private readonly string _s;
        private readonly int _maxDepth;
        private int _i;

        public RtJsonParser(string text, int maxDepth)
        {
            _s = text;
            _maxDepth = maxDepth <= 0 ? RtJson.DefaultMaxDepth : maxDepth;
        }

        public int Position => _i;

        public RtJsonValue ParseDocument()
        {
            SkipWhitespace();
            RtJsonValue value = ParseValue(0);
            SkipWhitespace();
            if (_i < _s.Length)
            {
                throw Error($"JSON 末尾存在多余字符 '{_s[_i]}'");
            }
            return value;
        }

        private RtJsonValue ParseValue(int depth)
        {
            if (depth > _maxDepth) throw Error($"JSON 嵌套超过 {_maxDepth} 层");
            if (_i >= _s.Length) throw Error("JSON 意外结束");

            char c = _s[_i];
            switch (c)
            {
                case '{': return ParseObject(depth);
                case '[': return ParseArray(depth);
                case '"': return RtJsonValue.Of(ParseString());
                case 't': Expect("true"); return RtJsonValue.Of(true);
                case 'f': Expect("false"); return RtJsonValue.Of(false);
                case 'n': Expect("null"); return RtJsonValue.Null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ParseNumber();
                    throw Error($"不认识的字符 '{c}'");
            }
        }

        private RtJsonValue ParseObject(int depth)
        {
            _i++; // '{'
            var members = new List<RtJsonMember>();
            SkipWhitespace();
            if (Peek() == '}') { _i++; return RtJsonValue.Object(members); }

            while (true)
            {
                SkipWhitespace();
                if (Peek() != '"') throw Error("对象的键必须是字符串");
                string key = ParseString();
                SkipWhitespace();
                if (Peek() != ':') throw Error("对象的键之后缺少 ':'");
                _i++;
                SkipWhitespace();
                members.Add(new RtJsonMember(key, ParseValue(depth + 1)));
                SkipWhitespace();
                char next = Peek();
                if (next == ',') { _i++; continue; }
                if (next == '}') { _i++; return RtJsonValue.Object(members); }
                throw Error("对象成员之后需要 ',' 或 '}'");
            }
        }

        private RtJsonValue ParseArray(int depth)
        {
            _i++; // '['
            var items = new List<RtJsonValue>();
            SkipWhitespace();
            if (Peek() == ']') { _i++; return RtJsonValue.Array(items); }

            while (true)
            {
                SkipWhitespace();
                items.Add(ParseValue(depth + 1));
                SkipWhitespace();
                char next = Peek();
                if (next == ',') { _i++; continue; }
                if (next == ']') { _i++; return RtJsonValue.Array(items); }
                throw Error("数组元素之后需要 ',' 或 ']'");
            }
        }

        private string ParseString()
        {
            _i++; // 开引号
            var sb = new StringBuilder();
            while (true)
            {
                if (_i >= _s.Length) throw Error("字符串未闭合");
                char c = _s[_i++];
                if (c == '"') return sb.ToString();
                if (c != '\\')
                {
                    sb.Append(c);
                    continue;
                }
                if (_i >= _s.Length) throw Error("转义序列未结束");
                char esc = _s[_i++];
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
                        if (_i + 4 > _s.Length) throw Error("\\u 转义不完整");
                        string hex = _s.Substring(_i, 4);
                        if (!ushort.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ushort code))
                        {
                            throw Error($"非法的 \\u 转义 '{hex}'");
                        }
                        sb.Append((char)code);
                        _i += 4;
                        break;
                    default:
                        throw Error($"不支持的转义 '\\{esc}'");
                }
            }
        }

        private RtJsonValue ParseNumber()
        {
            int start = _i;
            if (Peek() == '-') _i++;
            while (_i < _s.Length && IsNumberChar(_s[_i])) _i++;
            string token = _s.Substring(start, _i - start);
            if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                throw Error($"非法数字 '{token}'");
            }
            return RtJsonValue.Of(value);
        }

        private static bool IsNumberChar(char c)
        {
            return (c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-';
        }

        private void Expect(string literal)
        {
            if (_i + literal.Length > _s.Length || string.CompareOrdinal(_s, _i, literal, 0, literal.Length) != 0)
            {
                throw Error($"期望 '{literal}'");
            }
            _i += literal.Length;
        }

        private char Peek() => _i < _s.Length ? _s[_i] : '\0';

        private void SkipWhitespace()
        {
            while (_i < _s.Length)
            {
                char c = _s[_i];
                if (c == ' ' || c == '\t' || c == '\n' || c == '\r') { _i++; continue; }
                break;
            }
        }

        private RtJsonException Error(string message) => new RtJsonException($"{message}（位置 {_i}）");
    }
}
