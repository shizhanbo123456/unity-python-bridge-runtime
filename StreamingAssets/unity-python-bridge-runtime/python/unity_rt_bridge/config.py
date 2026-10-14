"""读取 runtime_bridge.ini，给客户端与命令行提供默认连接参数。

查找顺序（先找到先用）：
  1. 环境变量 UNITY_RT_BRIDGE_INI 指向的文件
  2. 本包往上两级的 runtime_bridge.ini
     （即 <base>/runtime_bridge.ini，<base> 是 python/ 的上一级目录）
  3. 当前工作目录下的 runtime_bridge.ini
  4. 当前工作目录上一级下的 runtime_bridge.ini

全部找不到时使用内置默认值，不影响使用。
"""

import os

try:
    from pathlib import Path
except ImportError:  # pragma: no cover - Python 2 不在支持范围
    raise RuntimeError("需要 Python 3.7 及以上版本")

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 21928
DEFAULT_TIMEOUT = 10.0
INI_NAME = "runtime_bridge.ini"


class Settings(object):
    """一次连接需要的全部参数。"""

    def __init__(self, host, port, token, timeout, ini_path):
        self.host = host
        self.port = port
        self.token = token
        self.timeout = timeout
        self.ini_path = ini_path

    def describe(self):
        return "host=%s port=%s token=%s timeout=%ss ini=%s" % (
            self.host,
            self.port,
            "已设置" if self.token else "无",
            self.timeout,
            self.ini_path if self.ini_path else "(未找到，用默认值)",
        )


def candidate_ini_paths():
    """返回所有候选 ini 路径（保持优先级顺序）。"""
    paths = []

    env = os.environ.get("UNITY_RT_BRIDGE_INI")
    if env:
        paths.append(Path(env))

    try:
        here = Path(__file__).resolve()
        parents = here.parents
        # parents[0] = .../python/unity_rt_bridge
        # parents[1] = .../python
        # parents[2] = <base>
        if len(parents) >= 3:
            paths.append(parents[2] / INI_NAME)
        if len(parents) >= 4:
            paths.append(parents[3] / INI_NAME)
    except (OSError, IndexError):
        pass

    try:
        cwd = Path.cwd()
        paths.append(cwd / INI_NAME)
        paths.append(cwd.parent / INI_NAME)
    except OSError:
        pass

    return paths


def find_ini():
    """返回第一个存在的 ini 路径，找不到返回 None。"""
    for path in candidate_ini_paths():
        try:
            if path.is_file():
                return path
        except OSError:
            continue
    return None


def parse_ini(text):
    """解析 ini 文本，返回 {节名: {键: 值}}。节名与键都转小写。"""
    sections = {}
    current = ""
    for raw in text.splitlines():
        line = raw.strip()
        if not line or line[0] in (";", "#"):
            continue
        if line.startswith("[") and line.endswith("]"):
            current = line[1:-1].strip().lower()
            sections.setdefault(current, {})
            continue
        if "=" not in line:
            continue
        key, value = line.split("=", 1)
        key = key.strip().lower()
        if not key:
            continue
        sections.setdefault(current, {})[key] = _strip_comment(value).strip()
    return sections


def _strip_comment(value):
    """去掉值后面的 ; 或 # 注释（谁先出现以谁为准）。"""
    positions = [pos for pos in (value.find(";"), value.find("#")) if pos >= 0]
    if not positions:
        return value
    return value[: min(positions)]


def load_settings(ini_path=None):
    """读取配置。任何一步出错都退回默认值，不抛异常给调用方。"""
    path = Path(ini_path) if ini_path else find_ini()
    sections = {}
    if path is not None:
        try:
            with open(str(path), "r", encoding="utf-8-sig") as handle:
                sections = parse_ini(handle.read())
        except OSError:
            sections = {}

    server = sections.get("server", {})
    python_section = sections.get("python", {})

    host = python_section.get("host") or DEFAULT_HOST
    port = _to_int(python_section.get("port"), None)
    if port is None:
        port = _to_int(server.get("port"), DEFAULT_PORT)

    token = python_section.get("token")
    if not token:
        token = server.get("token") or ""

    timeout = _to_float(python_section.get("timeout"), DEFAULT_TIMEOUT)
    if timeout <= 0:
        timeout = DEFAULT_TIMEOUT

    return Settings(host=host, port=port, token=token, timeout=timeout, ini_path=path)


def _to_int(text, fallback):
    if text is None:
        return fallback
    try:
        return int(str(text).strip())
    except (TypeError, ValueError):
        return fallback


def _to_float(text, fallback):
    if text is None:
        return fallback
    try:
        return float(str(text).strip())
    except (TypeError, ValueError):
        return fallback
