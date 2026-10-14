// RuntimeBridgeStreamingAssetsHook.cs —— 构建时把工具自带的 StreamingAssets 内容挂进 Player。
//
// 为什么需要这个：
//   Unity 只认「Assets/StreamingAssets」这一个特例目录，且全工程只能有一个。
//   本工具是按 unity-python-bridge 的约定整包拷进 Assets 的，于是它的 StreamingAssets
//   实际位于 Assets/unity-python-bridge-runtime/StreamingAssets/unity-python-bridge-runtime/。
//   **这个位置不会被 Unity 复制进 Player**——编辑器里一切正常，打包后 Python 与 ini 直接消失。
//
//   所以这里用官方的 BuildPlayerProcessor，在构建前把该目录挂到 Player 的
//   StreamingAssets/unity-python-bridge-runtime 下。使用者不需要手动拷两份。
//
// 本文件在 Editor 程序集里，不进 Player。

#if UNITY_EDITOR
using System.IO;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace UnityRuntimeBridge.EditorTools
{
    /// <summary>把工具目录内的 StreamingAssets 内容补挂进 Player。</summary>
    public sealed class RuntimeBridgeStreamingAssetsHook : BuildPlayerProcessor
    {
        public override void PrepareForBuild(BuildPlayerContext buildPlayerContext)
        {
            string canonical = RuntimeBridgeConfig.CanonicalDir;
            if (!string.IsNullOrEmpty(canonical) && Directory.Exists(canonical))
            {
                // 规范位置已经有内容，必须跳过：官方文档明确说明，
                // 若目标路径在工程里已存在，构建后期会抛异常，所以这里不能重复挂。
                Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 构建：StreamingAssets 已存在 {canonical}，无需补挂");
                return;
            }

            string bundled = RuntimeBridgeConfig.BundledDir;
            if (string.IsNullOrEmpty(bundled) || !Directory.Exists(bundled))
            {
                Debug.LogWarning(
                    $"{RuntimeBridgeInfo.LogPrefix} 构建：既没有 Assets/StreamingAssets/{RuntimeBridgeInfo.FolderName}，"
                    + "也没找到工具自带的 StreamingAssets 内容。打包后的游戏将读不到 runtime_bridge.ini 与 Python 客户端，"
                    + "会退化成默认配置（端口 21928、无令牌）。");
                return;
            }

            buildPlayerContext.AddAdditionalPathToStreamingAssets(bundled, RuntimeBridgeInfo.FolderName);
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 构建：已把 {bundled} 挂进 Player 的 "
                      + $"StreamingAssets/{RuntimeBridgeInfo.FolderName}");
        }
    }
}
#endif
