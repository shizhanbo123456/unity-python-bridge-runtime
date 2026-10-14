// RuntimeBridgeJson.cs —— 把 Unity 的值翻译成 JSON，作为 RtJson 的转换器。
//
// RtJson 只管通用 .NET 类型；Unity 类型（Vector3 / Color / GameObject / Transform …）
// 在这里特殊处理：
//   · 数学结构按字段展开，与 Python 侧的 dict 直接对应；
//   · 场景对象只输出引用信息，不递归展开（否则 Transform 会把整棵场景拖出来）。

using System;
using System.Text;
using UnityEngine;

namespace UnityRuntimeBridge
{
    /// <summary>原样嵌入的 JSON 文本，序列化时不会再转义或加引号。</summary>
    public sealed class RawJson
    {
        public string Text { get; }

        public RawJson(string json)
        {
            Text = string.IsNullOrEmpty(json) ? "null" : json;
        }
    }

    /// <summary>命令返回值的序列化入口。</summary>
    public static class RuntimeBridgeJson
    {
        /// <summary>把返回值序列化为一行 JSON 文本。</summary>
        public static string Serialize(object value, bool pretty = false)
        {
            return RtJson.Serialize(value, ConvertUnityType, pretty);
        }

        /// <summary>包住一段已构造好的 JSON 文本，让其原样嵌入（不做二次转义）。</summary>
        public static RawJson Raw(string json) => new RawJson(json);

        /// <summary>返回 null 表示"交给 RtJson 的默认反射逻辑处理"。</summary>
        private static string ConvertUnityType(object value)
        {
            if (value == null) return null;

            switch (value)
            {
                case RawJson raw:
                    return raw.Text;

                // 开发者直接返回一个 JSON 节点时，直接把它写出去
                case RtJsonValue node:
                    return node.ToJson();

                case Vector2 v2:
                    return $"{{\"x\":{Num(v2.x)},\"y\":{Num(v2.y)}}}";
                case Vector3 v3:
                    return $"{{\"x\":{Num(v3.x)},\"y\":{Num(v3.y)},\"z\":{Num(v3.z)}}}";
                case Vector4 v4:
                    return $"{{\"x\":{Num(v4.x)},\"y\":{Num(v4.y)},\"z\":{Num(v4.z)},\"w\":{Num(v4.w)}}}";
                case Quaternion q:
                    return $"{{\"x\":{Num(q.x)},\"y\":{Num(q.y)},\"z\":{Num(q.z)},\"w\":{Num(q.w)}}}";
                case Color c:
                    return $"{{\"r\":{Num(c.r)},\"g\":{Num(c.g)},\"b\":{Num(c.b)},\"a\":{Num(c.a)},"
                           + $"\"hex\":\"{ColorUtility.ToHtmlStringRGBA(c)}\"}}";
                case Color32 c32:
                    return $"{{\"r\":{c32.r},\"g\":{c32.g},\"b\":{c32.b},\"a\":{c32.a},"
                           + $"\"hex\":\"{ColorUtility.ToHtmlStringRGBA(c32)}\"}}";
                case Rect rect:
                    return $"{{\"x\":{Num(rect.x)},\"y\":{Num(rect.y)},"
                           + $"\"width\":{Num(rect.width)},\"height\":{Num(rect.height)}}}";
                case Bounds bounds:
                    return $"{{\"center\":{ConvertUnityType(bounds.center)},\"size\":{ConvertUnityType(bounds.size)}}}";
                case LayerMask mask:
                    return ((int)mask).ToString();
            }

            // Unity 对象（GameObject / Component / ScriptableObject / 材质 / 贴图 …）只给引用信息。
            // 注意：Unity 重载了 == ，已销毁的对象在这里会判为 null。
            if (value is UnityEngine.Object unityObject)
            {
                return DescribeUnityObject(unityObject);
            }

            return null;
        }

        private static string DescribeUnityObject(UnityEngine.Object target)
        {
            if (target == null) return "{\"$ref\":\"Destroyed\"}";

            var sb = new StringBuilder("{\"$ref\":");
            RtJson.AppendQuoted(sb, target.GetType().Name);
            sb.Append(",\"name\":");
            RtJson.AppendQuoted(sb, target.name);
            sb.Append(",\"instanceId\":").Append(target.GetInstanceID());

            try
            {
                if (target is GameObject go)
                {
                    sb.Append(",\"activeSelf\":").Append(go.activeSelf ? "true" : "false");
                    sb.Append(",\"path\":");
                    RtJson.AppendQuoted(sb, go.transform != null ? HierarchyPath(go.transform) : target.name);
                }
                else if (target is Component component)
                {
                    sb.Append(",\"enabled\":").Append(component is Behaviour behaviour && behaviour.enabled ? "true" : "false");
                    sb.Append(",\"path\":");
                    RtJson.AppendQuoted(sb, HierarchyPath(component.transform));
                }
            }
            catch (Exception)
            {
                // 对象正在销毁等边界情况，只返回已有的基础信息
            }

            sb.Append('}');
            return sb.ToString();
        }

        /// <summary>拼出 "Root/Child/Leaf" 形式的层级路径。</summary>
        private static string HierarchyPath(Transform transform)
        {
            var sb = new StringBuilder(transform.name);
            Transform parent = transform.parent;
            while (parent != null)
            {
                sb.Insert(0, '/').Insert(0, parent.name);
                parent = parent.parent;
            }
            return sb.ToString();
        }

        private static string Num(float value)
        {
            return RtJsonValue.FormatNumber(value);
        }
    }
}
