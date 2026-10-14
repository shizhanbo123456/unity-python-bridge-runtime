"""命令行入口：python -m unity_rt_bridge ...

设计目标是"不用写代码也能调游戏里的函数"：
    python -m unity_rt_bridge list                       # 看有哪些命令
    python -m unity_rt_bridge call sample.scene_info     # 调一条
    python -m unity_rt_bridge call sample.set_position --arg name=Player --arg position=[1,0,2]
"""

import argparse
import json
import sys

from . import __version__
from .client import RuntimeBridgeClient, RuntimeBridgeError

EXIT_OK = 0
EXIT_RUNTIME_ERROR = 1
EXIT_USAGE_ERROR = 2


def build_parser():
    parser = argparse.ArgumentParser(
        prog="python -m unity_rt_bridge",
        description="通过 TCP 调用运行中的 Unity（Play 模式或打包后的游戏）里注册的函数。",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=(
            "示例：\n"
            "  python -m unity_rt_bridge list\n"
            "  python -m unity_rt_bridge call runtime.ping\n"
            "  python -m unity_rt_bridge call sample.spawn_cube --arg name=Box --arg position=[0,1,0]\n"
            "  python -m unity_rt_bridge call runtime.echo --args '{\"a\":{\"b\":[1,2]}}'\n"
        ),
    )

    parser.add_argument("--host", help="游戏所在地址，默认取 ini 或 127.0.0.1")
    parser.add_argument("--port", type=int, help="端口，默认取 ini 的 [server] port")
    parser.add_argument("--token", help="访问令牌，游戏侧配了 token 时必须给")
    parser.add_argument("--timeout", type=float, help="超时秒数，默认取 ini 或 10")
    parser.add_argument("--ini", help="指定 runtime_bridge.ini 的路径")
    parser.add_argument("--compact", action="store_true", help="输出单行 JSON（便于管道处理）")
    parser.add_argument("--indent", type=int, default=2, help="JSON 缩进，默认 2")
    parser.add_argument("--cli-version", action="version", version="unity_rt_bridge " + __version__)

    sub = parser.add_subparsers(dest="command", metavar="子命令")

    sub.add_parser("ping", help="连通性检测")
    sub.add_parser("version", help="查看桥版本与运行环境")
    sub.add_parser("stats", help="运行统计")
    sub.add_parser("config", help="查看游戏侧生效的配置（端口/令牌/路径）")

    list_parser = sub.add_parser("list", help="列出游戏里已注册的命令")
    list_parser.add_argument("--filter", "-f", help="按名称或说明做子串过滤")
    list_parser.add_argument("--json", action="store_true", help="输出原始 JSON 而不是表格")

    call_parser = sub.add_parser("call", help="调用任意已注册命令")
    call_parser.add_argument("name", help="命令名，例如 sample.scene_info")
    call_parser.add_argument("--args", help="完整参数 JSON，例如 '{\"a\":1}'")
    call_parser.add_argument(
        "--arg",
        action="append",
        default=[],
        metavar="KEY=VALUE",
        help="单个参数，可重复。键支持 a.b 形式造嵌套；值自动识别 true/false/null/整数/小数/JSON，"
             "想强制当字符串就用引号包住，如 --arg name=\"'123'\"",
    )
    call_parser.add_argument("--stdin", action="store_true", help="从标准输入读取参数 JSON")

    return parser


def main(argv=None):
    _configure_output_encoding()
    parser = build_parser()
    args = parser.parse_args(argv)

    if not args.command:
        parser.print_help()
        return EXIT_USAGE_ERROR

    try:
        with RuntimeBridgeClient(
            host=args.host,
            port=args.port,
            token=args.token,
            timeout=args.timeout,
            ini_path=args.ini,
        ) as client:
            data = dispatch(client, args)
    except RuntimeBridgeError as error:
        print(str(error), file=sys.stderr)
        return EXIT_RUNTIME_ERROR
    except (TypeError, ValueError) as error:
        print("参数错误: %s" % error, file=sys.stderr)
        return EXIT_USAGE_ERROR

    if args.command == "list" and not args.json:
        print_commands(data)
    else:
        print(format_json(data, args))
    return EXIT_OK


def _configure_output_encoding():
    """输出被重定向（管道/文件）时强制 UTF-8，避免中文在不同 locale 下编码不一致。

    真正的控制台不动它——Windows 上 Python 本身就用 UTF-8 写控制台，改反而会乱码。
    """
    for stream in (sys.stdout, sys.stderr):
        try:
            if stream is None or stream.isatty():
                continue
            if hasattr(stream, "reconfigure"):
                stream.reconfigure(encoding="utf-8", errors="replace")
        except (OSError, ValueError, AttributeError):
            # 取不到 isatty 或不允许 reconfigure 时保持原样
            continue


def dispatch(client, args):
    if args.command == "ping":
        return client.ping()
    if args.command == "version":
        return client.version()
    if args.command == "stats":
        return client.stats()
    if args.command == "config":
        return client.config()
    if args.command == "list":
        return client.list_commands(filter=args.filter)
    if args.command == "call":
        return client.call(args.name, build_call_args(args))
    raise ValueError("未知子命令: %s" % args.command)


def build_call_args(args):
    payload = {}

    if args.args:
        try:
            parsed = json.loads(args.args)
        except ValueError as error:
            raise ValueError("--args 不是合法 JSON: %s" % error)
        if not isinstance(parsed, dict):
            raise ValueError("--args 必须是 JSON 对象")
        payload.update(parsed)

    if args.stdin:
        text = sys.stdin.read()
        if text.strip():
            try:
                parsed = json.loads(text)
            except ValueError as error:
                raise ValueError("标准输入不是合法 JSON: %s" % error)
            if not isinstance(parsed, dict):
                raise ValueError("标准输入必须是 JSON 对象")
            payload.update(parsed)

    for item in args.arg:
        key, value = split_arg(item)
        assign_path(payload, key, value)

    return payload


def split_arg(text):
    if "=" not in text:
        raise ValueError("--arg 需要 KEY=VALUE 形式，收到 %r" % text)
    key, value = text.split("=", 1)
    key = key.strip()
    if not key:
        raise ValueError("--arg 的键不能为空: %r" % text)
    return key, coerce_value(value)


def coerce_value(text):
    """把命令行字符串转成合适的 JSON 类型。"""
    stripped = text.strip()

    if len(stripped) >= 2 and stripped[0] == stripped[-1] and stripped[0] in ('"', "'"):
        return stripped[1:-1]

    lowered = stripped.lower()
    if lowered == "true":
        return True
    if lowered == "false":
        return False
    if lowered in ("null", "none"):
        return None

    if stripped[:1] in ("[", "{"):
        try:
            return json.loads(stripped)
        except ValueError:
            pass

    try:
        return int(stripped)
    except ValueError:
        pass
    try:
        return float(stripped)
    except ValueError:
        pass
    return text


def assign_path(target, key, value):
    """按 a.b.c 形式写入嵌套字典。"""
    parts = key.split(".")
    cursor = target
    for part in parts[:-1]:
        if not part:
            raise ValueError("参数键里有空的层级: %r" % key)
        nxt = cursor.get(part)
        if not isinstance(nxt, dict):
            nxt = {}
            cursor[part] = nxt
        cursor = nxt
    if not parts[-1]:
        raise ValueError("参数键末尾不能是点: %r" % key)
    cursor[parts[-1]] = value


def format_json(data, args):
    if args.compact:
        return json.dumps(data, ensure_ascii=False, separators=(",", ":"))
    return json.dumps(data, ensure_ascii=False, indent=args.indent)


def print_commands(data):
    if not isinstance(data, dict):
        print(format_json(data, argparse.Namespace(compact=False, indent=2)))
        return

    commands = data.get("commands") or []
    if not commands:
        print("（没有匹配的命令）")
    for item in commands:
        flag = "[只读]" if item.get("readOnly") else "[写入]"
        print("%s %-30s %s" % (flag, item.get("name", ""), item.get("description", "")))

    print("")
    print("匹配 %s 条 / 共 %s 条。用 python -m unity_rt_bridge call <命令名> 调用。" % (
        data.get("count"), data.get("total")))


if __name__ == "__main__":
    sys.exit(main())
