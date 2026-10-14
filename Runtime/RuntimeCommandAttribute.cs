// RuntimeCommandAttribute.cs —— 把一个静态方法暴露成 Python 可调用的命令。

using System;

namespace UnityRuntimeBridge
{
    /// <summary>
    /// 标记一个静态方法为 Python 可调用的命令。方法签名必须是：
    ///   public static object MethodName(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
    /// 返回值可以是任意对象（自动序列化为 JSON），也可以是 string（当作 JSON 字符串返回）。
    /// 返回 void 也允许，此时 data 为 null。
    ///
    /// 注册靠反射扫描，不需要在任何地方手动登记。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
    public sealed class RuntimeCommandAttribute : Attribute
    {
        /// <summary>命令名，建议用 "域.动作" 形式，例如 "player.add_item"。</summary>
        public string Name { get; }

        /// <summary>给人和 AI 看的说明，会出现在 runtime.list_commands 里。</summary>
        public string Description { get; }

        /// <summary>是否为只读命令（不修改游戏状态）。供 AI/网关判断是否需要用户二次确认。</summary>
        public bool ReadOnly { get; set; }

        public RuntimeCommandAttribute(string name, string description = "")
        {
            if (string.IsNullOrEmpty(name))
            {
                throw new ArgumentException("命令名不能为空", nameof(name));
            }
            Name = name.Trim();
            Description = description ?? "";
        }
    }
}
