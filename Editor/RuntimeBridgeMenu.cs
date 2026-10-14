// RuntimeBridgeMenu.cs —— 仅编辑器使用的辅助菜单（Editor 程序集，不会进 Player）。

#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;

namespace UnityRuntimeBridge.EditorTools
{
    /// <summary>编辑器辅助入口：查看配置、复制 Python 命令、在 Play 模式下开关服务器。</summary>
    public static class RuntimeBridgeMenu
    {
        private const string Root = "Tools/Unity Runtime Bridge/";

        [MenuItem(Root + "启动服务器（Play 模式）", false, 1)]
        private static void StartServer()
        {
            if (!RequirePlayMode("启动")) return;
            RuntimeBridgeServer.Start();
        }

        [MenuItem(Root + "停止服务器（Play 模式）", false, 2)]
        private static void StopServer()
        {
            if (!RequirePlayMode("停止")) return;
            RuntimeBridgeServer.Stop();
        }

        [MenuItem(Root + "打印配置与路径", false, 20)]
        private static void PrintConfig()
        {
            RuntimeBridgeConfig config = RuntimeBridgeConfig.Current;
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 配置: {config.Describe()}");
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 数据目录: {RuntimeBridgeConfig.BaseDir}");
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} ini 路径: {config.IniPath}");
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} Python 目录: {RuntimeBridgeConfig.PythonDir}");
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 规范 StreamingAssets 位置: "
                      + $"{RuntimeBridgeConfig.CanonicalDir}（存在={Directory.Exists(RuntimeBridgeConfig.CanonicalDir)}）");
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 工具自带 StreamingAssets: "
                      + $"{RuntimeBridgeConfig.BundledDir}（打包时由构建钩子挂进 Player）");
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 已注册命令: {RuntimeBridgeDispatcher.Count} 条");
        }

        [MenuItem(Root + "列出已注册命令", false, 21)]
        private static void ListCommands()
        {
            var sb = new System.Text.StringBuilder();
            foreach (RuntimeCommandInfo info in RuntimeBridgeDispatcher.AllCommands())
            {
                sb.AppendLine($"{(info.ReadOnly ? "[只读]" : "[写入]")} {info.Name} — {info.Description}");
            }
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 已注册 {RuntimeBridgeDispatcher.Count} 条命令:\n{sb}");
        }

        /// <summary>把可直接粘贴执行的命令放进剪贴板（PowerShell 写法，避免 &amp;&amp; 在 5.1 下不可用）。</summary>
        [MenuItem(Root + "复制 Python 客户端命令", false, 22)]
        private static void CopyPythonCommand()
        {
            string command = $"Set-Location '{RuntimeBridgeConfig.PythonDir}'; "
                             + $"python -m {RuntimeBridgeInfo.PythonPackage} list";
            EditorGUIUtility.systemCopyBuffer = command;
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 已复制到剪贴板：{command}");
        }

        private static bool RequirePlayMode(string action)
        {
            if (Application.isPlaying) return true;

            EditorUtility.DisplayDialog(
                "Unity Runtime Bridge",
                $"\"{action}服务器\" 只在 Play 模式下可用。\n\n"
                + "运行时桥面向运行时与打包后的游戏；编辑模式下的场景/Prefab 操作请用 unity-python-bridge。",
                "知道了");
            return false;
        }
    }
}
#endif
