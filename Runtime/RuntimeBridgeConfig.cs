// RuntimeBridgeConfig.cs —— 读取 StreamingAssets 下的 runtime_bridge.ini。
//
// 配置文件与 Python 客户端放在一起（StreamingAssets/unity-python-bridge-runtime/），
// 这样打包后整个目录会随游戏一起发布，现场不用再单独拷一份配置。
// 读不到文件时全部使用默认值，不影响游戏启动。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

namespace UnityRuntimeBridge
{
    /// <summary>运行时桥的配置。字段名与 ini 的键一一对应。</summary>
    public sealed class RuntimeBridgeConfig
    {
        /// <summary>监听端口。与 unity-python-bridge 的 21927 错开。</summary>
        public int Port = RuntimeBridgeInfo.DefaultPort;

        /// <summary>监听地址。默认只绑本机回环，局域网内其它机器连不上。</summary>
        public string BindAddress = "127.0.0.1";

        /// <summary>访问令牌。非空时请求必须带上同样的 token，否则拒绝。</summary>
        public string Token = "";

        /// <summary>游戏启动时自动开启服务器。</summary>
        public bool AutoStart = true;

        /// <summary>每帧执行命令的时间预算（毫秒），超出的留到下一帧，避免卡顿。</summary>
        public float FlushBudgetMs = 8f;

        /// <summary>命令队列积压上限，超过就立刻回错误，不继续堆积。</summary>
        public int MaxQueued = 64;

        /// <summary>是否把每条命令的名称与耗时打到日志里（排障用，正式版建议关闭）。</summary>
        public bool LogCommands;

        /// <summary>实际生效的 ini 路径（可能不存在）。</summary>
        public string IniPath;

        // ---- 单例与路径 ----

        private static RuntimeBridgeConfig _current;
        private static string _baseDir;

        /// <summary>当前生效的配置（首次访问时读取 ini 并缓存）。</summary>
        public static RuntimeBridgeConfig Current
        {
            get
            {
                if (_current == null) _current = Load();
                return _current;
            }
        }

        /// <summary>丢弃缓存，下次访问重新读 ini。</summary>
        public static void Reload()
        {
            _current = null;
            _baseDir = null;
        }

        /// <summary>
        /// 本工具的数据目录。优先 StreamingAssets（随包发布），
        /// 找不到则回退到 Assets 下同名目录（方便直接连源码一起用）。
        /// </summary>
        public static string BaseDir
        {
            get
            {
                if (_baseDir != null) return _baseDir;

                var candidates = new List<string>();
                TryAddCandidate(candidates, () => Application.streamingAssetsPath, RuntimeBridgeInfo.FolderName);
                TryAddCandidate(candidates, () => Application.dataPath, RuntimeBridgeInfo.FolderName);
                TryAddCandidate(candidates, () => Application.dataPath, "StreamingAssets", RuntimeBridgeInfo.FolderName);

                foreach (string candidate in candidates)
                {
                    if (Directory.Exists(candidate))
                    {
                        _baseDir = candidate;
                        return _baseDir;
                    }
                }

                _baseDir = candidates.Count > 0 ? candidates[0] : RuntimeBridgeInfo.FolderName;
                return _baseDir;
            }
        }

        private static void TryAddCandidate(List<string> target, Func<string> root, params string[] parts)
        {
            try
            {
                string basePath = root();
                if (string.IsNullOrEmpty(basePath)) return;
                string path = basePath;
                foreach (string part in parts) path = Path.Combine(path, part);
                target.Add(path);
            }
            catch (Exception)
            {
                // 某些平台取不到路径，忽略该候选
            }
        }

        /// <summary>ini 文件的完整路径。</summary>
        public static string IniFilePath => Path.Combine(BaseDir, RuntimeBridgeInfo.IniFileName);

        /// <summary>Python 客户端所在目录。</summary>
        public static string PythonDir => Path.Combine(BaseDir, "python");

        // ---- 加载 ----

        public static RuntimeBridgeConfig Load()
        {
            var config = new RuntimeBridgeConfig();
            string path = IniFilePath;
            config.IniPath = path;

            if (!File.Exists(path))
            {
                Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 未找到 {path}，使用默认配置（端口 {config.Port}）");
                return config;
            }

            try
            {
                var sections = RuntimeBridgeIni.Parse(File.ReadAllText(path));
                if (sections.TryGetValue("server", out Dictionary<string, string> server))
                {
                    config.Port = ReadPort(server, config.Port);
                    config.BindAddress = ReadString(server, "bind", config.BindAddress);
                    config.Token = ReadString(server, "token", config.Token);
                    config.AutoStart = ReadBool(server, "autostart", config.AutoStart);
                    config.LogCommands = ReadBool(server, "log_commands", config.LogCommands);
                    config.FlushBudgetMs = Clamp(ReadFloat(server, "flush_budget_ms", config.FlushBudgetMs), 0f, 1000f);
                    config.MaxQueued = Math.Max(1, ReadInt(server, "max_queued", config.MaxQueued));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{RuntimeBridgeInfo.LogPrefix} 解析 {path} 失败，使用默认配置: {e.Message}");
            }

            return config;
        }

        private static int ReadPort(Dictionary<string, string> section, int fallback)
        {
            if (!section.TryGetValue("port", out string raw)) return fallback;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int port) && port > 0 && port <= 65535)
            {
                return port;
            }
            Debug.LogWarning($"{RuntimeBridgeInfo.LogPrefix} ini 中的 port=\"{raw}\" 无效，回退到 {fallback}");
            return fallback;
        }

        private static string ReadString(Dictionary<string, string> section, string key, string fallback)
        {
            return section.TryGetValue(key, out string raw) ? raw : fallback;
        }

        private static bool ReadBool(Dictionary<string, string> section, string key, bool fallback)
        {
            if (!section.TryGetValue(key, out string raw)) return fallback;
            switch (raw.Trim().ToLowerInvariant())
            {
                case "true":
                case "1":
                case "yes":
                case "on":
                    return true;
                case "false":
                case "0":
                case "no":
                case "off":
                    return false;
                default:
                    Debug.LogWarning($"{RuntimeBridgeInfo.LogPrefix} ini 中的 {key}=\"{raw}\" 不是布尔值，回退到 {fallback}");
                    return fallback;
            }
        }

        private static int ReadInt(Dictionary<string, string> section, string key, int fallback)
        {
            if (section.TryGetValue(key, out string raw)
                && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                return value;
            }
            return fallback;
        }

        private static float ReadFloat(Dictionary<string, string> section, string key, float fallback)
        {
            if (section.TryGetValue(key, out string raw)
                && float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
            {
                return value;
            }
            return fallback;
        }

        private static float Clamp(float value, float min, float max)
        {
            return value < min ? min : (value > max ? max : value);
        }

        /// <summary>一行摘要，写日志用。</summary>
        public string Describe()
        {
            return $"端口={Port} 监听={BindAddress} 自动启动={AutoStart} 令牌={(string.IsNullOrEmpty(Token) ? "无" : "有")} "
                   + $"帧预算={FlushBudgetMs}ms 队列上限={MaxQueued} 记录命令={LogCommands} ini={IniPath}";
        }
    }

    /// <summary>极简 ini 解析（只支持 [section]、key = value、; 或 # 行内注释）。</summary>
    internal static class RuntimeBridgeIni
    {
        public static Dictionary<string, Dictionary<string, string>> Parse(string text)
        {
            var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (text == null) return sections;

            string current = "";
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line[0] == ';' || line[0] == '#') continue;

                if (line[0] == '[' && line[line.Length - 1] == ']')
                {
                    current = line.Substring(1, line.Length - 2).Trim();
                    if (!sections.ContainsKey(current)) sections[current] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    continue;
                }

                int eq = line.IndexOf('=');
                if (eq <= 0) continue;

                string key = line.Substring(0, eq).Trim();
                string value = StripComment(line.Substring(eq + 1)).Trim();
                if (key.Length == 0) continue;

                if (!sections.TryGetValue(current, out Dictionary<string, string> section))
                {
                    section = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    sections[current] = section;
                }
                section[key] = value;
            }
            return sections;
        }

        /// <summary>去掉值后面的 ; 或 # 注释（谁先出现以谁为准）。</summary>
        private static string StripComment(string value)
        {
            int semicolon = value.IndexOf(';');
            int hash = value.IndexOf('#');
            int cut = -1;
            if (semicolon >= 0 && hash >= 0) cut = Math.Min(semicolon, hash);
            else if (semicolon >= 0) cut = semicolon;
            else if (hash >= 0) cut = hash;
            return cut >= 0 ? value.Substring(0, cut) : value;
        }
    }
}
