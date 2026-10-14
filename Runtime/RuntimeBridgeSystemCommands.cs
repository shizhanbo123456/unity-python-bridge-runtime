// RuntimeBridgeSystemCommands.cs —— 桥自带的基础命令。
//
// 都用 "runtime." 前缀，与开发者自己的玩法命令区分开。
// 这些命令在任何环境下都可用（不引用 UnityEditor）。

using System;
using System.Collections.Generic;
using UnityEngine;

namespace UnityRuntimeBridge
{
    /// <summary>桥自带的基础命令。</summary>
    public static class RuntimeBridgeSystemCommands
    {
        [RuntimeCommand("runtime.ping", "连通性检测：确认服务器在线", ReadOnly = true)]
        public static object Ping(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            return new
            {
                pong = true,
                port = RuntimeBridgeServer.Port,
                bind = RuntimeBridgeServer.BindAddress,
                uptimeSeconds = Math.Round(RuntimeBridgeServer.UptimeSeconds, 3)
            };
        }

        [RuntimeCommand("runtime.version", "返回桥版本与运行环境信息", ReadOnly = true)]
        public static object Version(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            return new
            {
                bridge = RuntimeBridgeInfo.Version,
                unity = Application.unityVersion,
                platform = Application.platform.ToString(),
                isEditor = Application.isEditor,
                dataPath = Application.dataPath,
                streamingAssets = Application.streamingAssetsPath,
                commandCount = RuntimeBridgeDispatcher.Count
            };
        }

        [RuntimeCommand("runtime.config", "查看当前生效的配置（端口/地址/是否要令牌等）", ReadOnly = true)]
        public static object ConfigInfo(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            RuntimeBridgeConfig config = RuntimeBridgeConfig.Current;
            return new
            {
                port = config.Port,
                bind = config.BindAddress,
                autostart = config.AutoStart,
                requireToken = !string.IsNullOrEmpty(config.Token),
                flushBudgetMs = config.FlushBudgetMs,
                maxQueued = config.MaxQueued,
                logCommands = config.LogCommands,
                baseDir = RuntimeBridgeConfig.BaseDir,
                iniPath = config.IniPath,
                pythonDir = RuntimeBridgeConfig.PythonDir
            };
        }

        /// <summary>列出命令。可用 args.filter 按名称/说明做子串过滤。</summary>
        [RuntimeCommand("runtime.list_commands", "列出所有已注册命令（args.filter 可按名称或说明过滤）", ReadOnly = true)]
        public static object ListCommands(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            string filter = args.GetString("filter", "");
            bool hasFilter = !string.IsNullOrEmpty(filter);

            var items = new List<object>();
            foreach (RuntimeCommandInfo info in RuntimeBridgeDispatcher.AllCommands())
            {
                if (hasFilter
                    && info.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && (info.Description == null
                        || info.Description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    continue;
                }

                items.Add(new
                {
                    name = info.Name,
                    description = info.Description,
                    readOnly = info.ReadOnly,
                    provider = info.Provider
                });
            }

            return new
            {
                filter = hasFilter ? filter : null,
                count = items.Count,
                total = RuntimeBridgeDispatcher.Count,
                commands = items
            };
        }

        [RuntimeCommand("runtime.stats", "运行统计：连接数、已处理命令数、队列积压", ReadOnly = true)]
        public static object Stats(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            return new
            {
                running = RuntimeBridgeServer.IsRunning,
                port = RuntimeBridgeServer.Port,
                handledCommands = RuntimeBridgeServer.HandledCount,
                activeSessions = RuntimeBridgeServer.ActiveSessions,
                pendingCommands = RuntimeBridgeRunner.PendingCount,
                pumpAlive = RuntimeBridgeServer.PumpAlive,
                uptimeSeconds = Math.Round(RuntimeBridgeServer.UptimeSeconds, 3),
                lastError = RuntimeBridgeServer.LastError
            };
        }

        /// <summary>把参数原样返回，用来确认参数有没有正确传到 Unity 侧。</summary>
        [RuntimeCommand("runtime.echo", "把 args 原样返回，用于验证参数传递", ReadOnly = true)]
        public static object Echo(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            return new
            {
                command = ctx.Command,
                requestId = ctx.RequestId,
                keys = args.Keys(),
                args = args.Raw
            };
        }
    }
}
