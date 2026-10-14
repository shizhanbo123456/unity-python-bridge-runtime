"""模拟 Unity 侧服务器：没有 Unity 也能把客户端和 CLI 调通。

它复刻的是 RuntimeBridgeServer.cs 的协议与响应格式，因此可以放心用它做联调与自测：

    # 终端 A
    python scripts/mock_runtime_server.py
    # 终端 B
    python -m unity_rt_bridge list
    python -m unity_rt_bridge call sample.scene_info

真实行为以 C# 侧为准；本文件只用于在没有游戏时验证 Python 这一端。
"""

import argparse
import json
import socketserver
import sys
import threading

DEFAULT_PORT = 21928
VERSION = "0.1.0-mock"


class _Handler(socketserver.StreamRequestHandler):
    def handle(self):
        while True:
            line = self.rfile.readline()
            if not line:
                return
            text = line.decode("utf-8").strip()
            if not text:
                continue
            response = self.server.process(text)
            payload = json.dumps(response, ensure_ascii=False) + "\n"
            try:
                self.wfile.write(payload.encode("utf-8"))
            except OSError:
                return


class MockUnityServer(socketserver.ThreadingTCPServer):
    allow_reuse_address = True
    daemon_threads = True

    def __init__(self, address, token=""):
        socketserver.ThreadingTCPServer.__init__(self, address, _Handler)
        self.token = token
        self.handled = 0

    # ---- 协议 ----

    def process(self, text):
        request_id = 0
        try:
            request = json.loads(text)
        except ValueError as error:
            return _error(request_id, "JSON 解析失败: %s" % error)

        if not isinstance(request, dict):
            return _error(request_id, "请求必须是 JSON 对象")

        request_id = request.get("id") or 0
        command = request.get("cmd")
        if not command:
            return _error(request_id, "请求缺少 cmd 字段")

        if self.token and request.get("token") != self.token:
            return _error(request_id, "令牌校验失败（请检查 ini 里的 token 与调用方是否一致）")

        args = request.get("args")
        if args is None:
            args = request.get("params")
        if not isinstance(args, dict):
            args = {}

        handler = COMMANDS.get(command)
        if handler is None:
            return _error(request_id, "未知命令: %s（已注册 %d 条，可用 runtime.list_commands 查看）"
                          % (command, len(COMMANDS)))

        try:
            data = handler(args, request_id)
        except MockCommandError as error:
            return _error(request_id, str(error))

        self.handled += 1
        return {"id": request_id, "ok": True, "data": data}


class MockCommandError(Exception):
    """模拟命令自身的失败（对应 C# 侧命令抛异常）。"""


def _error(request_id, message):
    return {"id": request_id, "ok": False, "error": message}


# ---- 模拟命令 ----

def _ping(args, request_id):
    return {"pong": True, "port": None, "bind": "127.0.0.1", "uptimeSeconds": 1.5}


def _version(args, request_id):
    return {
        "bridge": VERSION,
        "unity": "2022.3.62f2",
        "platform": "WindowsPlayer",
        "isEditor": False,
        "commandCount": len(COMMANDS),
    }


def _config(args, request_id):
    return {
        "port": DEFAULT_PORT,
        "bind": "127.0.0.1",
        "autostart": True,
        "requireToken": False,
        "flushBudgetMs": 8.0,
        "maxQueued": 64,
        "logCommands": False,
        "baseDir": "<mock>",
        "iniPath": "<mock>/runtime_bridge.ini",
        "pythonDir": "<mock>/python",
    }


def _list_commands(args, request_id):
    needle = (args.get("filter") or "").lower()
    items = []
    for name in sorted(COMMANDS):
        readonly, description = COMMAND_META[name]
        if needle and needle not in name.lower() and needle not in description.lower():
            continue
        items.append({
            "name": name,
            "description": description,
            "readOnly": readonly,
            "provider": "Mock.MockCommands",
        })
    return {"filter": args.get("filter"), "count": len(items), "total": len(COMMANDS), "commands": items}


def _stats(args, request_id):
    return {
        "running": True,
        "port": DEFAULT_PORT,
        "handledCommands": 0,
        "activeSessions": 1,
        "pendingCommands": 0,
        "pumpAlive": True,
        "uptimeSeconds": 1.5,
        "lastError": None,
    }


def _echo(args, request_id):
    return {"command": "runtime.echo", "requestId": request_id, "keys": sorted(args.keys()), "args": args}


def _scene_info(args, request_id):
    return {
        "scene": "MockScene",
        "buildIndex": 0,
        "rootCount": 3,
        "frameCount": 120,
        "time": 4.0,
        "timeScale": 1.0,
        "screen": {"width": 1920, "height": 1080},
        "isPlaying": True,
    }


def _set_position(args, request_id):
    if not args.get("name"):
        raise MockCommandError("缺少必填参数 name")
    return {"name": args["name"], "position": args.get("position") or {"x": 0.0, "y": 0.0, "z": 0.0}}


def _fail(args, request_id):
    raise MockCommandError(args.get("message") or "这是模拟的失败")


COMMANDS = {
    "runtime.ping": _ping,
    "runtime.version": _version,
    "runtime.config": _config,
    "runtime.list_commands": _list_commands,
    "runtime.stats": _stats,
    "runtime.echo": _echo,
    "sample.scene_info": _scene_info,
    "sample.set_position": _set_position,
    "mock.fail": _fail,
}

COMMAND_META = {
    "runtime.ping": (True, "连通性检测：确认服务器在线"),
    "runtime.version": (True, "返回桥版本与运行环境信息"),
    "runtime.config": (True, "查看当前生效的配置（端口/地址/是否要令牌等）"),
    "runtime.list_commands": (True, "列出所有已注册命令（args.filter 可按名称或说明过滤）"),
    "runtime.stats": (True, "运行统计：连接数、已处理命令数、队列积压"),
    "runtime.echo": (True, "把 args 原样返回，用于验证参数传递"),
    "sample.scene_info": (True, "读取当前场景与运行状态（示例）"),
    "sample.set_position": (False, "按名字把场景物体移到指定位置（示例）"),
    "mock.fail": (False, "故意失败，用来测试错误路径"),
}


def start_in_thread(port=0, token=""):
    """在后台线程启动模拟服务器，返回 (server, thread, 实际端口)。"""
    server = MockUnityServer(("127.0.0.1", port), token=token)
    thread = threading.Thread(target=server.serve_forever, kwargs={"poll_interval": 0.1})
    thread.daemon = True
    thread.start()
    return server, thread, server.server_address[1]


def main(argv=None):
    parser = argparse.ArgumentParser(description="模拟 Unity 侧服务器，用于在没有游戏时联调 Python 客户端。")
    parser.add_argument("--port", type=int, default=DEFAULT_PORT, help="监听端口，默认 %d；填 0 表示随机端口" % DEFAULT_PORT)
    parser.add_argument("--token", default="", help="要求客户端携带的令牌，默认不校验")
    args = parser.parse_args(argv)

    server, thread, port = start_in_thread(port=args.port, token=args.token)
    print("模拟服务器已启动，监听 127.0.0.1:%d（%d 条命令%s）"
          % (port, len(COMMANDS), "，需要令牌" if args.token else ""))
    print("客户端可以直接用：python -m unity_rt_bridge --port %d list" % port)
    if thread:
        thread.join()
    return 0


if __name__ == "__main__":
    sys.exit(main())
