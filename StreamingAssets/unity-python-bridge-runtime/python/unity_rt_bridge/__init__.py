"""Unity Runtime Bridge —— 用 Python 调用运行中的 Unity（Play 模式或打包后的游戏）里注册的函数。

C# 侧代码在 Assets/unity-python-bridge-runtime/Runtime/，
本 Python 包与配置文件在同级的 StreamingAssets/unity-python-bridge-runtime/ 下（随包发布）。

快速开始：
    from unity_rt_bridge import RuntimeBridgeClient

    with RuntimeBridgeClient() as client:
        print(client.command_names())
        print(client.call("sample.scene_info"))
"""

from .client import RuntimeBridgeClient, RuntimeBridgeError
from .config import Settings, find_ini, load_settings

__version__ = "0.1.0"

__all__ = [
    "RuntimeBridgeClient",
    "RuntimeBridgeError",
    "Settings",
    "find_ini",
    "load_settings",
    "__version__",
]
