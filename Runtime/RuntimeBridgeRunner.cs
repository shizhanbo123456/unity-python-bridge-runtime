// RuntimeBridgeRunner.cs —— 主线程执行队列。
//
// TCP 后台线程只负责收发数据，真正执行命令的委托会被投递到这里，
// 由 RuntimeBridgePump（挂在常驻物体上的 MonoBehaviour）在 Update 里逐帧 Flush，
// 保证所有 Unity API 都在主线程调用。

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace UnityRuntimeBridge
{
    /// <summary>主线程执行队列。线程安全。</summary>
    public static class RuntimeBridgeRunner
    {
        private static readonly Queue<Action> Queue = new Queue<Action>();
        private static readonly object Gate = new object();

        /// <summary>由后台线程调用：把一条命令投递到主线程。</summary>
        public static void Enqueue(Action action)
        {
            if (action == null) return;
            lock (Gate)
            {
                Queue.Enqueue(action);
            }
        }

        /// <summary>当前积压的命令数。</summary>
        public static int PendingCount
        {
            get
            {
                lock (Gate) return Queue.Count;
            }
        }

        public static void Clear()
        {
            lock (Gate) Queue.Clear();
        }

        /// <summary>
        /// 在主线程调用：按时间预算执行队列中的命令。
        /// 至少执行 1 条（保证单个慢命令也能跑完），超出预算就把剩下的留到下一帧，
        /// 避免一帧内堆积大量命令造成卡顿。
        /// </summary>
        /// <returns>本次执行的命令数。</returns>
        public static int Flush(float budgetMs)
        {
            int executed = 0;
            long start = Stopwatch.GetTimestamp();

            while (true)
            {
                Action action;
                lock (Gate)
                {
                    if (Queue.Count == 0) return executed;
                    action = Queue.Dequeue();
                }

                try
                {
                    action();
                }
                catch (Exception e)
                {
                    // 单个命令的异常不能影响队列后续执行
                    UnityEngine.Debug.LogError(
                        $"{RuntimeBridgeInfo.LogPrefix} 命令执行异常: {e}");
                }

                executed++;

                if (budgetMs > 0f && executed > 0)
                {
                    double elapsedMs = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                    if (elapsedMs >= budgetMs) return executed;
                }
            }
        }
    }
}
