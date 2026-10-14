// RuntimeBridgeDispatcher.cs —— 命令注册表与分发。
//
// 首次执行时反射扫描所有已加载程序集，把带 [RuntimeCommand] 的静态方法登记进来。
// 新增命令 = 写一个静态方法 + 打特性，不需要改本文件，也不需要注册代码。

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace UnityRuntimeBridge
{
    /// <summary>命令注册表。线程安全（执行在主线程，扫描一次性完成）。</summary>
    public static class RuntimeBridgeDispatcher
    {
        private static readonly Dictionary<string, RuntimeCommandInfo> Commands =
            new Dictionary<string, RuntimeCommandInfo>(StringComparer.Ordinal);

        private static readonly object Gate = new object();
        private static bool _initialized;

        /// <summary>确保已扫描过。第一次调用会反射遍历全部程序集。</summary>
        public static void EnsureInitialized()
        {
            if (_initialized) return;
            lock (Gate)
            {
                if (_initialized) return;
                Scan();
                _initialized = true;
            }
        }

        /// <summary>清空并重新扫描（改了命令代码后想立刻生效可用，正常流程不需要）。</summary>
        public static void Rescan()
        {
            lock (Gate)
            {
                Commands.Clear();
                Scan();
                _initialized = true;
            }
        }

        /// <summary>执行命令。必须在主线程调用（由 RuntimeBridgeRunner 保证）。</summary>
        public static object Execute(string command, RuntimeBridgeArgs args, RuntimeBridgeContext ctx)
        {
            EnsureInitialized();

            if (string.IsNullOrEmpty(command) || !Commands.TryGetValue(command, out RuntimeCommandInfo info))
            {
                throw new KeyNotFoundException(
                    $"未知命令: {command}（已注册 {Commands.Count} 条，可用 runtime.list_commands 查看）");
            }

            return info.Handler(ctx ?? new RuntimeBridgeContext(), args ?? RuntimeBridgeArgs.Empty);
        }

        public static bool TryGet(string name, out RuntimeCommandInfo info)
        {
            EnsureInitialized();
            return Commands.TryGetValue(name, out info);
        }

        public static int Count
        {
            get
            {
                EnsureInitialized();
                return Commands.Count;
            }
        }

        /// <summary>全部已注册命令（按名称排序）。</summary>
        public static List<RuntimeCommandInfo> AllCommands()
        {
            EnsureInitialized();
            var list = new List<RuntimeCommandInfo>(Commands.Values);
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return list;
        }

        private static void Scan()
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            int registered = 0;

            foreach (Assembly assembly in assemblies)
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException e)
                {
                    // 个别程序集（插件、编辑器程序集）可能加载不全，跳过解析不了的类型
                    var loaded = new List<Type>();
                    foreach (Type t in e.Types)
                    {
                        if (t != null) loaded.Add(t);
                    }
                    types = loaded.ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (Type type in types)
                {
                    if (type == null || !type.IsClass) continue;

                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Public | BindingFlags.Static);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (MethodInfo method in methods)
                    {
                        RuntimeCommandAttribute attribute;
                        try
                        {
                            attribute = method.GetCustomAttribute<RuntimeCommandAttribute>();
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                        if (attribute == null) continue;

                        if (!TryBuildHandler(method, out RuntimeCommandHandler handler, out string error))
                        {
                            Debug.LogWarning(
                                $"{RuntimeBridgeInfo.LogPrefix} 忽略命令 {type.Name}.{method.Name}: {error}");
                            continue;
                        }

                        if (Commands.TryGetValue(attribute.Name, out RuntimeCommandInfo existing))
                        {
                            Debug.LogWarning(
                                $"{RuntimeBridgeInfo.LogPrefix} 命令名重复: \"{attribute.Name}\" 已被 "
                                + $"{existing.Provider} 注册，忽略 {type.FullName}.{method.Name}");
                            continue;
                        }

                        Commands[attribute.Name] = new RuntimeCommandInfo(
                            attribute.Name, attribute.Description, attribute.ReadOnly, handler, type.FullName);
                        registered++;
                    }
                }
            }

            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 已注册 {registered} 条命令（扫描 {assemblies.Length} 个程序集）");
        }

        private static bool TryBuildHandler(MethodInfo method, out RuntimeCommandHandler handler, out string error)
        {
            handler = null;
            error = null;

            if (method.ContainsGenericParameters)
            {
                error = "不支持泛型方法";
                return false;
            }

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length != 2
                || parameters[0].ParameterType != typeof(RuntimeBridgeContext)
                || parameters[1].ParameterType != typeof(RuntimeBridgeArgs))
            {
                error = "签名必须是 (RuntimeBridgeContext ctx, RuntimeBridgeArgs args)";
                return false;
            }

            // 返回值是 object 或引用类型时可以直接建委托（快路径）
            try
            {
                handler = (RuntimeCommandHandler)method.CreateDelegate(typeof(RuntimeCommandHandler));
                return true;
            }
            catch (Exception)
            {
                // void / 值类型返回值 / 其它原因 → 退回反射调用
            }

            try
            {
                handler = (ctx, args) => method.Invoke(null, new object[] { ctx, args });
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                return false;
            }
        }
    }
}
