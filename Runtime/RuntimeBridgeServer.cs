// RuntimeBridgeServer.cs —— TCP 服务器。
//
// 协议：单行 JSON，UTF-8，换行分隔。
//   请求: {"id":1,"cmd":"player.add_item","args":{"itemId":1001},"token":""}
//   成功: {"id":1,"ok":true,"data":{...}}
//   失败: {"id":1,"ok":false,"error":"..."}
//
// 线程模型：后台线程负责 Accept 与收发，命令统一投递到主线程队列执行
// （见 RuntimeBridgeRunner / RuntimeBridgePump），保证 Unity API 只在主线程被调用。
//
// 本文件不引用 UnityEditor，Editor 与打包后的 Player 用的是同一份代码。

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace UnityRuntimeBridge
{
    /// <summary>运行时桥服务器。</summary>
    public static class RuntimeBridgeServer
    {
        private static TcpListener _listener;
        private static Thread _acceptThread;
        private static volatile bool _running;
        private static int _activeSessions;
        private static long _handledCount;
        private static long _startTimestamp;
        private static GameObject _pumpObject;
        private static RuntimeBridgePump _pump;

        /// <summary>服务器是否在运行。</summary>
        public static bool IsRunning => _running;

        /// <summary>当前监听端口。</summary>
        public static int Port { get; private set; } = RuntimeBridgeInfo.DefaultPort;

        /// <summary>当前监听地址。</summary>
        public static string BindAddress { get; private set; } = "127.0.0.1";

        /// <summary>最近一次启动失败的原因（成功时为 null）。</summary>
        public static string LastError { get; private set; }

        /// <summary>已处理命令总数。</summary>
        public static long HandledCount => Interlocked.Read(ref _handledCount);

        /// <summary>当前连接数。</summary>
        public static int ActiveSessions => Volatile.Read(ref _activeSessions);

        /// <summary>主线程泵是否存活（不存活时命令不会被执行）。</summary>
        public static bool PumpAlive => _pump != null;

        /// <summary>已运行秒数。</summary>
        public static double UptimeSeconds
        {
            get
            {
                if (!_running) return 0d;
                return (Stopwatch.GetTimestamp() - _startTimestamp) / (double)Stopwatch.Frequency;
            }
        }

        /// <summary>当前生效的配置。</summary>
        public static RuntimeBridgeConfig Config => RuntimeBridgeConfig.Current;

        /// <summary>启动服务器。重复调用只会打印 Warning，不抛异常。</summary>
        public static void Start()
        {
            if (_running)
            {
                Debug.LogWarning($"{RuntimeBridgeInfo.LogPrefix} 服务器已在运行（{BindAddress}:{Port}），忽略重复启动");
                return;
            }

            RuntimeBridgeConfig config = RuntimeBridgeConfig.Current;
            Port = config.Port;
            BindAddress = string.IsNullOrEmpty(config.BindAddress) ? "127.0.0.1" : config.BindAddress.Trim();
            LastError = null;

            if (!TryResolveBindAddress(BindAddress, out IPAddress address))
            {
                LastError = $"无法解析监听地址 \"{BindAddress}\"";
                Debug.LogError($"{RuntimeBridgeInfo.LogPrefix} {LastError}");
                return;
            }

            try
            {
                _listener = new TcpListener(address, Port);
                _listener.Start();
            }
            catch (Exception e)
            {
                LastError = $"监听 {BindAddress}:{Port} 失败: {e.Message}（端口可能被占用）";
                Debug.LogError($"{RuntimeBridgeInfo.LogPrefix} {LastError}");
                try { _listener?.Stop(); } catch (Exception) { /* 忽略 */ }
                _listener = null;
                return;
            }

            _running = true;
            _startTimestamp = Stopwatch.GetTimestamp();
            Interlocked.Exchange(ref _handledCount, 0);
            EnsurePump();

            _acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "UnityRuntimeBridge-Accept"
            };
            _acceptThread.Start();

            if (!IsLoopbackAddress(BindAddress))
            {
                Debug.LogWarning($"{RuntimeBridgeInfo.LogPrefix} 监听地址 {BindAddress} 不是回环地址，"
                                 + "局域网内其它机器也能连接，请自行评估安全风险");
            }
            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} v{RuntimeBridgeInfo.Version} 服务器已启动，"
                      + $"监听 {BindAddress}:{Port}；{config.Describe()}");
        }

        /// <summary>停止服务器。重复调用只会打印 Warning。</summary>
        public static void Stop()
        {
            if (!_running)
            {
                Debug.LogWarning($"{RuntimeBridgeInfo.LogPrefix} 服务器未在运行，忽略重复停止");
                return;
            }

            _running = false;
            try { _listener?.Stop(); } catch (Exception) { /* 已释放 */ }
            _listener = null;
            RuntimeBridgeRunner.Clear();
            DestroyPump();

            Debug.Log($"{RuntimeBridgeInfo.LogPrefix} 服务器已停止");
        }

        /// <summary>游戏启动时按 ini 的 autostart 决定是否自动开服。</summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            try
            {
                if (!RuntimeBridgeConfig.Current.AutoStart)
                {
                    Debug.Log($"{RuntimeBridgeInfo.LogPrefix} ini 里 autostart=false，未自动启动；"
                              + "需要时在代码里调用 RuntimeBridgeServer.Start()");
                    return;
                }
                Start();
            }
            catch (Exception e)
            {
                Debug.LogError($"{RuntimeBridgeInfo.LogPrefix} 自动启动失败: {e}");
            }
        }

        // ---- 内部实现 ----

        private static void EnsurePump()
        {
            if (_pumpObject != null) return;

            _pumpObject = new GameObject("[UnityRuntimeBridge]")
            {
                hideFlags = HideFlags.HideAndDontSave
            };
            UnityEngine.Object.DontDestroyOnLoad(_pumpObject);
            _pump = _pumpObject.AddComponent<RuntimeBridgePump>();
        }

        private static void DestroyPump()
        {
            GameObject target = _pumpObject;
            _pumpObject = null;
            _pump = null;
            if (target == null) return;

            try { UnityEngine.Object.Destroy(target); }
            catch (Exception) { /* 退出流程中可能已不可销毁 */ }
        }

        private static void AcceptLoop()
        {
            try
            {
                while (_running)
                {
                    TcpClient client;
                    try
                    {
                        client = _listener.AcceptTcpClient();
                    }
                    catch (SocketException) { break; }
                    catch (ObjectDisposedException) { break; }
                    catch (InvalidOperationException) { break; }
                    catch (ThreadAbortException) { break; }
                    catch (Exception e)
                    {
                        Debug.LogError($"{RuntimeBridgeInfo.LogPrefix} 接受连接异常: {e.Message}");
                        break;
                    }

                    Interlocked.Increment(ref _activeSessions);
                    var thread = new Thread(() => HandleClient(client))
                    {
                        IsBackground = true,
                        Name = "UnityRuntimeBridge-Session"
                    };
                    thread.Start();
                }
            }
            finally
            {
                // 非预期退出（例如监听被外部关闭）时把状态收干净，避免"显示运行中但实际连不上"
                if (_running)
                {
                    _running = false;
                    try { _listener?.Stop(); } catch (Exception) { /* 忽略 */ }
                    _listener = null;
                    Debug.LogError($"{RuntimeBridgeInfo.LogPrefix} 接受连接循环意外退出，服务器已停止");
                }
            }
        }

        /// <summary>一次客户端会话。读请求 → 投递主线程 → 回包。</summary>
        private static void HandleClient(TcpClient client)
        {
            var session = new Session();
            string peer = "unknown";
            try
            {
                peer = client.Client?.RemoteEndPoint?.ToString() ?? "unknown";
            }
            catch (Exception)
            {
                // 取不到对端地址不影响服务
            }

            try
            {
                NetworkStream stream = client.GetStream();
                session.Reader = new StreamReader(stream, new UTF8Encoding(false));
                session.Writer = new StreamWriter(stream, new UTF8Encoding(false))
                {
                    AutoFlush = true,
                    NewLine = "\n"
                };

                string line;
                while (_running && (line = session.Reader.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;

                    if (line.Length > RuntimeBridgeInfo.MaxRequestLineLength)
                    {
                        session.Write(Envelope.Error(0,
                            $"请求行过长（{line.Length} 字符，上限 {RuntimeBridgeInfo.MaxRequestLineLength}）"));
                        break;
                    }

                    ProcessLine(line, peer, session);
                }
            }
            catch (IOException)
            {
                // 客户端断开（脚本被中断、超时）——正常现象
            }
            catch (SocketException)
            {
                // 连接被强制关闭——正常现象
            }
            catch (ObjectDisposedException)
            {
                // 服务器停止时连接被关闭
            }
            catch (Exception e)
            {
                Debug.LogWarning($"{RuntimeBridgeInfo.LogPrefix} 会话异常: {e.Message}");
            }
            finally
            {
                Interlocked.Decrement(ref _activeSessions);
                session.Close();
                try { client.Close(); } catch (Exception) { /* 忽略 */ }
            }
        }

        private static void ProcessLine(string line, string peer, Session session)
        {
            int requestId = 0;
            try
            {
                if (!RtJson.TryParse(line, out RtJsonValue root, out string parseError))
                {
                    session.Write(Envelope.Error(0, $"JSON 解析失败: {parseError}"));
                    return;
                }

                requestId = root["id"]?.AsInt(0) ?? 0;
                string command = root["cmd"]?.AsString();
                if (string.IsNullOrEmpty(command))
                {
                    session.Write(Envelope.Error(requestId, "请求缺少 cmd 字段"));
                    return;
                }

                RuntimeBridgeConfig config = RuntimeBridgeConfig.Current;

                string token = root["token"]?.AsString() ?? "";
                if (!string.IsNullOrEmpty(config.Token)
                    && !string.Equals(config.Token, token, StringComparison.Ordinal))
                {
                    session.Write(Envelope.Error(requestId, "令牌校验失败（请检查 ini 里的 token 与调用方是否一致）"));
                    return;
                }

                int pending = RuntimeBridgeRunner.PendingCount;
                if (pending >= config.MaxQueued)
                {
                    session.Write(Envelope.Error(requestId,
                        $"命令队列已积压 {pending} 条（上限 {config.MaxQueued}），请降低调用频率或稍后重试"));
                    return;
                }

                // args 是任意结构，原样交给命令自己解释；同时兼容 params 这个别名
                RtJsonValue argsNode = root["args"] ?? root["params"];
                var args = new RuntimeBridgeArgs(argsNode);

                int id = requestId;
                string name = command;
                RuntimeBridgeRunner.Enqueue(() => RunOnMainThread(id, name, args, peer, token, session));
            }
            catch (Exception e)
            {
                session.Write(Envelope.Error(requestId, e.Message));
            }
        }

        private static void RunOnMainThread(int id, string command, RuntimeBridgeArgs args,
            string peer, string token, Session session)
        {
            var context = new RuntimeBridgeContext
            {
                Command = command,
                RequestId = id,
                Peer = peer,
                Token = token
            };

            long started = Stopwatch.GetTimestamp();
            try
            {
                object data = RuntimeBridgeDispatcher.Execute(command, args, context);

                string dataJson;
                try
                {
                    dataJson = RuntimeBridgeJson.Serialize(data);
                }
                catch (RtJsonException e)
                {
                    session.Write(Envelope.Error(id, $"返回值序列化失败: {e.Message}"));
                    return;
                }

                session.Write(Envelope.Success(id, dataJson));
            }
            catch (Exception e)
            {
                // 详细堆栈进 Console，给客户端只回可读的消息
                Debug.LogWarning($"{RuntimeBridgeInfo.LogPrefix} 命令 {command} 执行失败: {e}");
                session.Write(Envelope.Error(id, e.Message));
            }
            finally
            {
                Interlocked.Increment(ref _handledCount);

                if (RuntimeBridgeConfig.Current.LogCommands)
                {
                    double elapsedMs = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
                    Debug.Log($"{RuntimeBridgeInfo.LogPrefix} {command} 耗时 "
                              + elapsedMs.ToString("F1", CultureInfo.InvariantCulture) + "ms");
                }
            }
        }

        private static bool TryResolveBindAddress(string text, out IPAddress address)
        {
            address = IPAddress.Loopback;
            if (string.IsNullOrEmpty(text)) return true;

            string trimmed = text.Trim();
            switch (trimmed.ToLowerInvariant())
            {
                case "localhost":
                case "loopback":
                    address = IPAddress.Loopback;
                    return true;
                case "any":
                case "0.0.0.0":
                case "*":
                    address = IPAddress.Any;
                    return true;
            }
            return IPAddress.TryParse(trimmed, out address);
        }

        private static bool IsLoopbackAddress(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            string trimmed = text.Trim().ToLowerInvariant();
            return trimmed == "localhost" || trimmed == "loopback" || trimmed == "::1"
                   || trimmed.StartsWith("127.", StringComparison.Ordinal);
        }

        /// <summary>一条连接的写入封装：主线程回包与线程收尾之间用锁互斥，避免写到已关闭的流。</summary>
        private sealed class Session
        {
            private readonly object _gate = new object();
            private bool _closed;

            public StreamReader Reader;
            public StreamWriter Writer;

            public void Write(string line)
            {
                lock (_gate)
                {
                    if (_closed || Writer == null) return;
                    try
                    {
                        Writer.WriteLine(line);
                    }
                    catch (Exception)
                    {
                        // 对端已断开，后续不再尝试写入
                        _closed = true;
                    }
                }
            }

            public void Close()
            {
                lock (_gate)
                {
                    _closed = true;
                    try { Writer?.Dispose(); } catch (Exception) { /* 忽略 */ }
                    try { Reader?.Dispose(); } catch (Exception) { /* 忽略 */ }
                    Writer = null;
                    Reader = null;
                }
            }
        }

        private static class Envelope
        {
            public static string Success(int id, string dataJson)
            {
                string payload = string.IsNullOrEmpty(dataJson) ? "null" : dataJson;
                return $"{{\"id\":{id},\"ok\":true,\"data\":{payload}}}";
            }

            public static string Error(int id, string message)
            {
                string escaped = RtJson.Escape(message ?? "");
                return $"{{\"id\":{id},\"ok\":false,\"error\":{escaped}}}";
            }
        }
    }
}
