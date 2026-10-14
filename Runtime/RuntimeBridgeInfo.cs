// RuntimeBridgeInfo.cs —— 常量与版本号。改版本号只改这里。

namespace UnityRuntimeBridge
{
    /// <summary>运行时桥的全局常量。</summary>
    public static class RuntimeBridgeInfo
    {
        /// <summary>桥版本号（runtime.version 命令会返回它）。</summary>
        public const string Version = "0.1.0";

        /// <summary>所有日志统一加这个前缀，方便在 Console 里过滤。</summary>
        public const string LogPrefix = "[UnityRuntimeBridge]";

        /// <summary>默认监听端口。故意与 unity-python-bridge 的 21927 错开，避免两者冲突。</summary>
        public const int DefaultPort = 21928;

        /// <summary>本工具在 StreamingAssets 下的目录名（同时也是 C# 代码所在目录名）。</summary>
        public const string FolderName = "unity-python-bridge-runtime";

        /// <summary>配置文件名。</summary>
        public const string IniFileName = "runtime_bridge.ini";

        /// <summary>Python 包名（python -m unity_rt_bridge）。</summary>
        public const string PythonPackage = "unity_rt_bridge";

        /// <summary>单行请求的最大长度，超过直接拒绝，防止畸形客户端把内存打满。</summary>
        public const int MaxRequestLineLength = 4 * 1024 * 1024;
    }
}
