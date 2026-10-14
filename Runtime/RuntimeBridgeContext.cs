// RuntimeBridgeContext.cs —— 命令的执行上下文、参数封装、处理器委托与注册信息。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityRuntimeBridge
{
    /// <summary>执行上下文。每条命令执行前创建一个，用于写日志、记录来源。</summary>
    public sealed class RuntimeBridgeContext
    {
        /// <summary>当前执行的命令名。</summary>
        public string Command { get; internal set; }

        /// <summary>请求 id（客户端用它匹配响应）。</summary>
        public int RequestId { get; internal set; }

        /// <summary>对端地址，例如 "127.0.0.1:53214"。</summary>
        public string Peer { get; internal set; }

        /// <summary>对端携带的令牌（未启用令牌时为空）。</summary>
        public string Token { get; internal set; }

        public void Log(string message)
        {
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix}[{Command}] {message}");
        }

        public void LogWarning(string message)
        {
            Debug.LogWarning($"{RuntimeBridgeInfo.LogPrefix}[{Command}] {message}");
        }

        public void LogError(string message)
        {
            Debug.LogError($"{RuntimeBridgeInfo.LogPrefix}[{Command}] {message}");
        }
    }

    /// <summary>
    /// 命令参数。包住请求里的 args 节点，按键取值，缺省返回调用方给的默认值。
    /// 不要求参数结构固定——每个命令自己决定读哪些键，因此新增玩法命令不需要改任何公共类。
    /// </summary>
    public sealed class RuntimeBridgeArgs
    {
        public static readonly RuntimeBridgeArgs Empty = new RuntimeBridgeArgs(null);

        /// <summary>原始参数节点（未提供 args 时为 null 节点）。</summary>
        public RtJsonValue Raw { get; }

        public RuntimeBridgeArgs(RtJsonValue raw)
        {
            Raw = raw ?? RtJsonValue.Null;
        }

        /// <summary>参数里是否提供了该键。</summary>
        public bool Has(string key) => Raw.Has(key);

        /// <summary>取原始节点，不存在返回 null。</summary>
        public RtJsonValue GetNode(string key) => Raw.Get(key);

        /// <summary>取节点，不存在直接抛异常。用于必填参数。</summary>
        public RtJsonValue Require(string key)
        {
            RtJsonValue node = Raw.Get(key);
            if (node == null)
            {
                throw new ArgumentException($"缺少必填参数 '{key}'");
            }
            return node;
        }

        /// <summary>参数里出现的所有键（便于写通用/调试命令）。</summary>
        public List<string> Keys()
        {
            var keys = new List<string>();
            foreach (RtJsonMember member in Raw.Members) keys.Add(member.Key);
            return keys;
        }

        // ---- 标量 ----

        public string GetString(string key, string defaultValue = null) => Raw.Get(key)?.AsString(defaultValue) ?? defaultValue;
        public int GetInt(string key, int defaultValue = 0) => Raw.Get(key)?.AsInt(defaultValue) ?? defaultValue;
        public long GetLong(string key, long defaultValue = 0) => Raw.Get(key)?.AsLong(defaultValue) ?? defaultValue;
        public float GetFloat(string key, float defaultValue = 0f) => Raw.Get(key)?.AsFloat(defaultValue) ?? defaultValue;
        public double GetDouble(string key, double defaultValue = 0d) => Raw.Get(key)?.AsDouble(defaultValue) ?? defaultValue;
        public bool GetBool(string key, bool defaultValue = false) => Raw.Get(key)?.AsBool(defaultValue) ?? defaultValue;

        /// <summary>读枚举，按名字匹配（忽略大小写）。值非法时返回默认值。</summary>
        public T GetEnum<T>(string key, T defaultValue) where T : struct
        {
            string raw = GetString(key);
            if (string.IsNullOrEmpty(raw)) return defaultValue;
            return Enum.TryParse(raw, true, out T parsed) ? parsed : defaultValue;
        }

        // ---- 数组 ----

        public float[] GetFloatArray(string key)
        {
            RtJsonValue node = Raw.Get(key);
            if (node == null || !node.IsArray) return null;
            var result = new float[node.Count];
            for (int i = 0; i < node.Count; i++) result[i] = node[i].AsFloat();
            return result;
        }

        public int[] GetIntArray(string key)
        {
            RtJsonValue node = Raw.Get(key);
            if (node == null || !node.IsArray) return null;
            var result = new int[node.Count];
            for (int i = 0; i < node.Count; i++) result[i] = node[i].AsInt();
            return result;
        }

        public string[] GetStringArray(string key)
        {
            RtJsonValue node = Raw.Get(key);
            if (node == null || !node.IsArray) return null;
            var result = new string[node.Count];
            for (int i = 0; i < node.Count; i++) result[i] = node[i].AsString();
            return result;
        }

        /// <summary>取 List&lt;string&gt;，不存在返回 null。</summary>
        public List<string> GetStringList(string key)
        {
            string[] array = GetStringArray(key);
            return array == null ? null : new List<string>(array);
        }

        // ---- Unity 值（既支持 {"x":..,"y":..} 也支持 [x,y]）----

        public Vector2 GetVector2(string key, Vector2 defaultValue)
        {
            float[] values = GetNumbers(key, 2);
            if (values == null) return defaultValue;
            return new Vector2(values[0], values[1]);
        }

        public Vector3 GetVector3(string key, Vector3 defaultValue)
        {
            float[] values = GetNumbers(key, 3);
            if (values == null) return defaultValue;
            return new Vector3(values[0], values[1], values[2]);
        }

        public Vector4 GetVector4(string key, Vector4 defaultValue)
        {
            float[] values = GetNumbers(key, 4);
            if (values == null) return defaultValue;
            return new Vector4(values[0], values[1], values[2], values[3]);
        }

        public Quaternion GetQuaternion(string key, Quaternion defaultValue)
        {
            float[] values = GetNumbers(key, 4);
            if (values == null) return defaultValue;
            return new Quaternion(values[0], values[1], values[2], values[3]);
        }

        /// <summary>取颜色。支持 "#RRGGBB"/"#RRGGBBAA" 字符串、{"r","g","b","a"} 对象或 [r,g,b,a] 数组（0~1）。</summary>
        public Color GetColor(string key, Color defaultValue)
        {
            RtJsonValue node = Raw.Get(key);
            if (node == null) return defaultValue;

            if (node.Kind == RtJsonKind.String)
            {
                return ColorUtility.TryParseHtmlString(node.AsString(), out Color parsed) ? parsed : defaultValue;
            }
            if (node.IsObject)
            {
                return new Color(
                    node["r"]?.AsFloat(defaultValue.r) ?? defaultValue.r,
                    node["g"]?.AsFloat(defaultValue.g) ?? defaultValue.g,
                    node["b"]?.AsFloat(defaultValue.b) ?? defaultValue.b,
                    node["a"]?.AsFloat(defaultValue.a) ?? defaultValue.a);
            }
            float[] values = GetNumbers(key, 4);
            return values == null ? defaultValue : new Color(values[0], values[1], values[2], values[3]);
        }

        /// <summary>统一取 n 个浮点数：对象按 x/y/z/w 或 r/g/b/a 取，数组按顺序取。</summary>
        private float[] GetNumbers(string key, int count)
        {
            RtJsonValue node = Raw.Get(key);
            if (node == null) return null;

            if (node.IsArray)
            {
                if (node.Count < count) return null;
                var result = new float[count];
                for (int i = 0; i < count; i++) result[i] = node[i].AsFloat();
                return result;
            }
            if (node.IsObject)
            {
                string[] names = count == 2
                    ? new[] { "x", "y" }
                    : count == 3 ? new[] { "x", "y", "z" } : new[] { "x", "y", "z", "w" };
                var result = new float[count];
                for (int i = 0; i < count; i++)
                {
                    RtJsonValue item = node[names[i]];
                    if (item == null) return null;
                    result[i] = item.AsFloat();
                }
                return result;
            }
            return null;
        }

        /// <summary>把原始参数原样序列化成 JSON 文本。</summary>
        public string ToJson() => Raw.ToJson();

        public override string ToString() => ToJson();
    }

    /// <summary>命令处理器。与 [RuntimeCommand] 标记的方法签名一致。</summary>
    public delegate object RuntimeCommandHandler(RuntimeBridgeContext ctx, RuntimeBridgeArgs args);

    /// <summary>已注册命令的元信息。</summary>
    public sealed class RuntimeCommandInfo
    {
        public string Name { get; }
        public string Description { get; }
        public bool ReadOnly { get; }

        /// <summary>实现该命令的类全名，便于在大型工程里定位。</summary>
        public string Provider { get; }

        public RuntimeCommandHandler Handler { get; }

        public RuntimeCommandInfo(string name, string description, bool readOnly,
            RuntimeCommandHandler handler, string provider = "")
        {
            Name = name;
            Description = description ?? "";
            ReadOnly = readOnly;
            Handler = handler;
            Provider = provider ?? "";
        }
    }
}
