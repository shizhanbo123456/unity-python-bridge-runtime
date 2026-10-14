"""Unity Runtime Bridge 的 Python 客户端。仅使用标准库。

用法：
    from unity_rt_bridge import RuntimeBridgeClient

    with RuntimeBridgeClient() as client:               # 参数默认读 runtime_bridge.ini
        print(client.ping())
        print(client.call("player.add_item", itemId=1001, count=3))
        print(client.call("sample.set_position", {"name": "Player"}, position=[1, 0, 2]))

协议：TCP + 单行 JSON（UTF-8，\\n 分隔）
    请求  {"id":1,"cmd":"...","args":{...},"token":"..."}
    成功  {"id":1,"ok":true,"data":...}
    失败  {"id":1,"ok":false,"error":"..."}

注意：一个实例内部是一条长连接，不要跨线程共享同一实例。
"""

import json
import socket
import time

from .config import load_settings

DEFAULT_TIMEOUT = 10.0
MAX_LINE_BYTES = 16 * 1024 * 1024


class RuntimeBridgeError(Exception):
    """连接失败，或 Unity 侧返回 ok=false。"""


class RuntimeBridgeClient(object):
    def __init__(self, host=None, port=None, token=None, timeout=None, ini_path=None):
        settings = load_settings(ini_path)
        self.host = host or settings.host
        self.port = int(port) if port else settings.port
        self.token = settings.token if token is None else token
        self.timeout = float(timeout) if timeout else settings.timeout
        self.ini_path = settings.ini_path

        self._socket = None
        self._buffer = b""
        self._next_id = 1

    # ---- 连接管理 ----

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc_value, traceback):
        self.close()
        return False

    def connect(self):
        """建立连接（已连接则直接返回）。"""
        if self._socket is not None:
            return self
        try:
            self._socket = socket.create_connection((self.host, self.port), self.timeout)
            self._socket.settimeout(self.timeout)
        except OSError as error:
            self._socket = None
            raise RuntimeBridgeError(_connect_hint(self.host, self.port, error))
        return self

    def close(self):
        """关闭连接。可以重复调用。"""
        if self._socket is None:
            return
        try:
            self._socket.close()
        except OSError:
            pass
        self._socket = None
        self._buffer = b""

    def is_connected(self):
        return self._socket is not None

    # ---- 调用 ----

    def call(self, command, args=None, **kwargs):
        """调用命令并返回 data。失败抛 RuntimeBridgeError。

        args 传字典，kwargs 是同一件事的语法糖；两者都传时 kwargs 优先。
        """
        envelope = self.call_raw(command, args, **kwargs)
        if not envelope.get("ok"):
            raise RuntimeBridgeError(
                "%s 调用失败: %s" % (command, envelope.get("error", "未知错误"))
            )
        return envelope.get("data")

    def call_raw(self, command, args=None, **kwargs):
        """调用命令并返回完整响应（含 ok/error），不抛"业务失败"异常。"""
        if not command:
            raise ValueError("command 不能为空")

        payload_args = {}
        if args:
            if not isinstance(args, dict):
                raise TypeError("args 必须是字典")
            payload_args.update(args)
        if kwargs:
            payload_args.update(kwargs)

        self.connect()

        request_id = self._next_id
        self._next_id += 1

        request = {"id": request_id, "cmd": command, "args": payload_args}
        if self.token:
            request["token"] = self.token

        line = json.dumps(request, ensure_ascii=False, separators=(",", ":")) + "\n"
        self._send(line.encode("utf-8"))
        raw = self._read_line()

        try:
            envelope = json.loads(raw.decode("utf-8"))
        except (UnicodeDecodeError, ValueError) as error:
            raise RuntimeBridgeError("响应不是合法 JSON: %s" % error)

        if not isinstance(envelope, dict):
            raise RuntimeBridgeError("响应不是 JSON 对象: %r" % (envelope,))
        if envelope.get("id") != request_id:
            raise RuntimeBridgeError(
                "响应 id 不匹配（发的是 %s，收到 %s）——连接状态可能已错乱，建议重新连接"
                % (request_id, envelope.get("id"))
            )
        return envelope

    # ---- 常用封装 ----

    def ping(self):
        return self.call("runtime.ping")

    def version(self):
        return self.call("runtime.version")

    def stats(self):
        return self.call("runtime.stats")

    def config(self):
        return self.call("runtime.config")

    def echo(self, **kwargs):
        return self.call("runtime.echo", **kwargs)

    def list_commands(self, filter=None):
        """返回 {"count":..,"total":..,"commands":[{name,description,readOnly,provider},..]}。"""
        return self.call("runtime.list_commands", {"filter": filter} if filter else None)

    def command_names(self):
        """只取命令名列表，便于快速看有哪些可调。"""
        data = self.list_commands() or {}
        return [item.get("name") for item in data.get("commands", [])]

    def wait_until_ready(self, timeout=30.0, interval=0.5):
        """轮询直到服务器可用（游戏刚启动时很有用）。成功返回 True，超时返回 False。"""
        deadline = time.monotonic() + max(0.1, timeout)
        while time.monotonic() < deadline:
            try:
                self.ping()
                return True
            except RuntimeBridgeError:
                self.close()
                time.sleep(interval)
        return False

    # ---- 底层收发 ----

    def _send(self, payload):
        try:
            self._socket.sendall(payload)
        except OSError as error:
            self.close()
            raise RuntimeBridgeError("发送请求失败（连接已断开？）: %s" % error)

    def _read_line(self):
        """读到 \\n 为止，返回不含换行的一行字节。"""
        while b"\n" not in self._buffer:
            try:
                chunk = self._socket.recv(65536)
            except socket.timeout:
                self.close()
                raise RuntimeBridgeError(
                    "等待响应超时（%.1fs）。可能原因：命令在主线程卡住、帧预算把命令推到了后续帧，"
                    "或游戏窗口失去响应。" % self.timeout
                )
            except OSError as error:
                self.close()
                raise RuntimeBridgeError("读取响应失败: %s" % error)

            if not chunk:
                self.close()
                raise RuntimeBridgeError(
                    "连接被服务器关闭。可能原因：游戏已退出、场景重载，或服务器被 Stop() 了。"
                )
            self._buffer += chunk
            if len(self._buffer) > MAX_LINE_BYTES:
                self.close()
                raise RuntimeBridgeError("响应超过 %d 字节上限，已断开" % MAX_LINE_BYTES)

        line, self._buffer = self._buffer.split(b"\n", 1)
        return line


def _connect_hint(host, port, error):
    return (
        "连接 %s:%s 失败: %s\n"
        "排查清单：\n"
        "  1. 游戏（或 Unity Play 模式）是否已经启动并运行到 Bridge 自动开服之后；\n"
        "  2. 端口是否一致——以游戏侧 StreamingAssets/unity-python-bridge-runtime/"
        "runtime_bridge.ini 的 [server] port 为准；\n"
        "  3. 若 ini 里 autostart=false，需要在游戏代码里显式调用 RuntimeBridgeServer.Start()；\n"
        "  4. 是否设了 token，而客户端没有带上同样的 token。"
        % (host, port, error)
    )
