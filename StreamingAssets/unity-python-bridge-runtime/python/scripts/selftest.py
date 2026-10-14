"""端到端自测：起一个模拟服务器，把客户端与 CLI 的关键路径都跑一遍。

    python scripts/selftest.py

不需要 Unity，也不需要装任何第三方包。全部通过时退出码为 0。
"""

import json
import os
import shutil
import socket
import subprocess
import sys
import tempfile
import time

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
PYTHON_DIR = os.path.dirname(SCRIPT_DIR)
sys.path.insert(0, PYTHON_DIR)
sys.path.insert(0, SCRIPT_DIR)

import mock_runtime_server  # noqa: E402
from unity_rt_bridge import RuntimeBridgeClient, RuntimeBridgeError  # noqa: E402

PASSED = []
FAILED = []


def check(condition, name, detail=""):
    if condition:
        PASSED.append(name)
    else:
        FAILED.append("%s  %s" % (name, detail))
        print("FAIL  %s  %s" % (name, detail))


def expect_raises(name, exception_type, action, must_contain=None):
    try:
        action()
    except exception_type as error:
        if must_contain and must_contain not in str(error):
            check(False, name, "异常消息里没有 %r：%s" % (must_contain, error))
        else:
            check(True, name)
        return
    except Exception as error:  # noqa: BLE001
        check(False, name, "抛的是 %s 而不是 %s：%s" % (type(error).__name__, exception_type.__name__, error))
        return
    check(False, name, "期望抛 %s，但没有抛" % exception_type.__name__)


def run_cli(args, port):
    command = [sys.executable, "-m", "unity_rt_bridge", "--port", str(port)] + args
    env = dict(os.environ)
    env["PYTHONIOENCODING"] = "utf-8"     # 让子进程输出编码确定，便于本测试按 UTF-8 解码
    process = subprocess.run(
        command,
        cwd=PYTHON_DIR,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        encoding="utf-8",
        errors="replace",
        env=env,
    )
    return process.returncode, process.stdout, process.stderr


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def check_deployed_layout():
    """模拟"拷到工程 Assets/StreamingAssets 下"的部署形态。

    把整份 unity-python-bridge-runtime 目录复制到临时目录，把 ini 里的端口改成一个
    不常见的值，然后不带 --port 跑 CLI：如果它能连上，说明 ini 发现逻辑（往上找两级）
    在真实部署布局下是成立的。这是整套安装方式里最容易出错的一环。
    """
    import unity_rt_bridge
    from unity_rt_bridge import find_ini

    base_dir = os.path.dirname(PYTHON_DIR)

    # 0) 先确认"仓库自身 / 打包产物"这两种形态下，ini 都能被自动找到。
    #    两者布局一致（<base>/runtime_bridge.ini 与 <base>/python/...），靠的都是相对路径。
    source_ini = find_ini()
    check(source_ini is not None and os.path.dirname(os.path.abspath(str(source_ini))) == os.path.abspath(base_dir),
          "部署布局：源码目录下自动定位到上层 ini",
          "find_ini()=%s base=%s" % (source_ini, base_dir))

    probe_port = free_port()
    server, _thread, _bound = mock_runtime_server.start_in_thread(port=probe_port)

    try:
        with tempfile.TemporaryDirectory(prefix="rtbridge-deploy-") as workspace:
            deployed = os.path.join(workspace, "unity-python-bridge-runtime")
            shutil.copytree(base_dir, deployed,
                            ignore=shutil.ignore_patterns("__pycache__", "*.pyc"))

            ini_path = os.path.join(deployed, "runtime_bridge.ini")
            with open(ini_path, "r", encoding="utf-8") as handle:
                text = handle.read()

            replaced = text.replace("port = 21928", "port = %d" % probe_port)
            check(replaced != text, "部署布局：ini 里能找到 [server] port 行")
            with open(ini_path, "w", encoding="utf-8") as handle:
                handle.write(replaced)

            deployed_python = os.path.join(deployed, "python")

            # 1) 直接问 config 模块它找到了哪个 ini、读到什么端口
            code = (
                "import sys; sys.path.insert(0, r'%s');"
                "from unity_rt_bridge import find_ini, load_settings;"
                "print(find_ini()); print(load_settings().port)" % deployed_python
            )
            process = subprocess.run([sys.executable, "-c", code],
                                     stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                     encoding="utf-8", errors="replace")
            lines = [line for line in (process.stdout or "").splitlines() if line.strip()]
            check(len(lines) >= 2 and lines[0].endswith("runtime_bridge.ini"),
                  "部署布局：find_ini 找到上游目录的 ini",
                  repr(process.stdout) + repr(process.stderr))
            check(len(lines) >= 2 and lines[1].strip() == str(probe_port),
                  "部署布局：端口来自 ini 而不是硬编码默认值", repr(lines))
            check(len(lines) >= 1 and not lines[0].startswith(base_dir),
                  "部署布局：读的是拷贝后的那份 ini", repr(lines[:1]))

            # 2) 真的连一次：mock 监听 ini 里那个端口，CLI 不带 --port
            process = subprocess.run(
                [sys.executable, "-m", "unity_rt_bridge", "ping"],
                cwd=deployed_python,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                encoding="utf-8",
                errors="replace",
                env=dict(os.environ, PYTHONIOENCODING="utf-8"),
            )
            check(process.returncode == 0 and "pong" in (process.stdout or ""),
                  "部署布局：CLI 不带 --port 也能按 ini 连上",
                  "code=%s out=%s err=%s" % (process.returncode, process.stdout, process.stderr))

            # 3) 客户端库同样能连上（不带任何连接参数，但要显式指定 ini 位置）
            with RuntimeBridgeClient(port=probe_port, timeout=5) as client:
                check(client.ping().get("pong") is True, "部署布局：客户端可正常调用")
    finally:
        server.shutdown()

    check(unity_rt_bridge.__version__ != "", "包版本号可读")


def main():
    server, _thread, port = mock_runtime_server.start_in_thread(port=0)
    print("模拟服务器端口: %d\n" % port)

    try:
        # ---- 客户端基础调用 ----
        with RuntimeBridgeClient(host="127.0.0.1", port=port, timeout=5) as client:
            ping = client.ping()
            check(ping.get("pong") is True, "ping 返回 pong=true", repr(ping))

            version = client.version()
            check("bridge" in version and "unity" in version, "version 返回桥与引擎版本", repr(version))

            names = client.command_names()
            check("runtime.ping" in names, "command_names 能列出命令", repr(names))
            check("runtime.list_commands" in names, "command_names 包含系统命令")

            listing = client.list_commands(filter="echo")
            check(listing.get("count") == 1, "list_commands 支持过滤", repr(listing))

            # kwargs 形式
            echoed = client.call("runtime.echo", alpha=1, beta="two")
            check(echoed.get("args") == {"alpha": 1, "beta": "two"}, "kwargs 参数透传", repr(echoed))

            # 字典形式 + 嵌套
            nested = client.call("runtime.echo", {"player": {"name": "Bob", "level": 3}, "items": [1, 2]})
            check(nested.get("args")["player"]["name"] == "Bob", "嵌套参数透传", repr(nested))
            check(nested.get("args")["items"] == [1, 2], "数组参数透传", repr(nested))

            # 同一连接上多次调用（id 递增、连接复用）
            first = client.call_raw("runtime.echo", {"n": 1})
            second = client.call_raw("runtime.echo", {"n": 2})
            check(second["id"] == first["id"] + 1, "长连接上 id 递增", "%s -> %s" % (first["id"], second["id"]))
            check(client.is_connected(), "多次调用后连接保持")

            # 读取游戏侧配置
            config = client.config()
            check(config.get("port") == mock_runtime_server.DEFAULT_PORT, "config 返回端口", repr(config))

            # 业务失败：抛 RuntimeBridgeError，且能拿到 call_raw 的 ok=False
            expect_raises("命令内部报错会抛异常", RuntimeBridgeError,
                          lambda: client.call("mock.fail"), must_contain="模拟的失败")
            raw = client.call_raw("mock.fail")
            check(raw.get("ok") is False, "call_raw 保留 ok=false", repr(raw))

            # 未知命令
            expect_raises("未知命令报错并给出提示", RuntimeBridgeError,
                          lambda: client.call("no.such_command"), must_contain="未知命令")

            # 必填参数缺失（模拟命令的校验）
            expect_raises("缺少必填参数报错", RuntimeBridgeError,
                          lambda: client.call("sample.set_position", {}), must_contain="name")

            # 参数校验
            expect_raises("args 非字典时报错", TypeError, lambda: client.call("runtime.echo", "not-a-dict"))

        # ---- 令牌 ----
        token_server, _t, token_port = mock_runtime_server.start_in_thread(port=0, token="s3cret")
        try:
            expect_raises("令牌不匹配被拒绝", RuntimeBridgeError,
                          lambda: RuntimeBridgeClient(port=token_port, timeout=5).call("runtime.ping"),
                          must_contain="令牌")
            with RuntimeBridgeClient(port=token_port, token="s3cret", timeout=5) as client:
                check(client.ping().get("pong") is True, "带正确令牌可以调用")
        finally:
            token_server.shutdown()

        # ---- 连不上时的提示 ----
        dead_port = free_port()
        expect_raises("连不上时给出排查清单", RuntimeBridgeError,
                      lambda: RuntimeBridgeClient(port=dead_port, timeout=2).ping(),
                      must_contain="排查清单")
        check(RuntimeBridgeClient(port=dead_port, timeout=0.3).wait_until_ready(timeout=1.0) is False,
              "wait_until_ready 在超时后返回 False")
        check(RuntimeBridgeClient(port=port, timeout=3).wait_until_ready(timeout=3.0) is True,
              "wait_until_ready 在服务器就绪后返回 True")

        # ---- CLI ----
        code, out, err = run_cli(["ping"], port)
        check(code == 0 and "pong" in out, "CLI: ping", "code=%s out=%s err=%s" % (code, out, err))

        code, out, err = run_cli(["list"], port)
        check(code == 0 and "runtime.ping" in out and "[只读]" in out, "CLI: list 表格输出",
              "code=%s out=%s" % (code, out))

        code, out, err = run_cli(["list", "--filter", "echo", "--json"], port)
        parsed = json.loads(out) if code == 0 else {}
        check(code == 0 and parsed.get("count") == 1, "CLI: list --filter --json", "code=%s out=%s" % (code, out))

        code, out, err = run_cli([
            "call", "runtime.echo",
            "--arg", "count=5",
            "--arg", "ratio=1.5",
            "--arg", "flag=true",
            "--arg", "nothing=null",
            "--arg", "text=hello",
            "--arg", "player.name=Bob",
            "--arg", "tags=[1,2]",
            "--arg", "quoted=\"007\"",
        ], port)
        payload = json.loads(out)["args"] if code == 0 else {}
        check(code == 0, "CLI: call 退出码为 0", "code=%s err=%s" % (code, err))
        check(payload.get("count") == 5 and isinstance(payload.get("count"), int), "CLI: --arg 识别整数", repr(payload))
        check(payload.get("ratio") == 1.5, "CLI: --arg 识别小数", repr(payload))
        check(payload.get("flag") is True, "CLI: --arg 识别布尔", repr(payload))
        check(payload.get("nothing") is None, "CLI: --arg 识别 null", repr(payload))
        check(payload.get("text") == "hello", "CLI: --arg 保留字符串", repr(payload))
        check(payload.get("player", {}).get("name") == "Bob", "CLI: --arg 点号造嵌套", repr(payload))
        check(payload.get("tags") == [1, 2], "CLI: --arg 识别 JSON 数组", repr(payload))
        check(payload.get("quoted") == "007", "CLI: --arg 引号强制字符串", repr(payload))

        code, out, err = run_cli(["call", "runtime.echo", "--args", '{"a":{"b":[1,2]}}'], port)
        check(code == 0 and json.loads(out)["args"]["a"]["b"] == [1, 2], "CLI: --args 原始 JSON", "out=%s" % out)

        code, out, err = run_cli(["call", "mock.fail", "--arg", "message=故意失败"], port)
        check(code == 1 and "故意失败" in err, "CLI: 命令失败时退出码 1 且消息走 stderr",
              "code=%s err=%s" % (code, err))

        code, out, err = run_cli(["call", "not.a.command"], port)
        check(code == 1, "CLI: 未知命令退出码 1", "code=%s err=%s" % (code, err))

        code, out, err = run_cli(["call", "runtime.echo", "--args", "{bad json"], port)
        check(code == 2 and "合法 JSON" in err, "CLI: --args 非法 JSON 退出码 2", "code=%s err=%s" % (code, err))

        code, out, err = run_cli([], port)
        check(code == 2 and "usage" in out, "CLI: 不带子命令时打印帮助", "code=%s" % code)

        code, out, err = run_cli(["--compact", "stats"], port)
        check(code == 0 and "\n" not in out.strip(), "CLI: --compact 单行输出", repr(out))

        # ---- 部署布局：把整份 StreamingAssets 内容拷到别处，验证 ini 能被自动发现 ----
        check_deployed_layout()

    finally:
        server.shutdown()

    print("\n通过 %d 项，失败 %d 项" % (len(PASSED), len(FAILED)))
    if FAILED:
        for item in FAILED:
            print("  - %s" % item)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
