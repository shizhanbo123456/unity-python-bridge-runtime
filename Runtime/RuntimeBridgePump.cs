// RuntimeBridgePump.cs —— 主线程心跳。
//
// 服务器启动时会在场景里建一个隐藏的常驻物体挂上本组件，由它在 Update 里
// 驱动命令队列。打包后没有 EditorApplication.update，这是唯一的主线程驱动方式。

using UnityEngine;

namespace UnityRuntimeBridge
{
    /// <summary>由 RuntimeBridgeServer 自动创建，不需要手动挂载。</summary>
    public sealed class RuntimeBridgePump : MonoBehaviour
    {
        private void Update()
        {
            RuntimeBridgeRunner.Flush(RuntimeBridgeConfig.Current.FlushBudgetMs);
        }

        private void OnApplicationQuit()
        {
            RuntimeBridgeServer.Stop();
        }
    }
}
