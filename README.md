# Unity Runtime Bridge

**让 Python 在运行时和打包后的游戏里调用你自己写的 C# 函数。**

编辑器里能玩的东西很多，但打包出去以后就没法调了。这个库把「命令通道」做成纯运行时组件：
游戏跑起来（Play 模式或 exe）就自动开一个本机 TCP 端口，Python 侧连上去，调用你用
`[RuntimeCommand]` 标记过的任何静态方法。

```
┌──────────────┐   TCP + 单行 JSON   ┌────────────────────────────────────┐
│ Python 侧     │ ─────────────────▶ │ 游戏进程（Play 模式 / 打包后的 exe） │
│ unity_rt_bridge│                    │  RuntimeBridgeServer（后台线程收发） │
│ CLI / 当库用  │ ◀───────────────── │        ↓ 投递到主线程队列            │
└──────────────┘   {"ok":true,...}   │  RuntimeBridgeDispatcher（反射分发） │
                                     │        ↓ 在主线程执行                │
                                     │  你写的 [RuntimeCommand] 方法        │
                                     └────────────────────────────────────┘
```

零第三方依赖：C# 侧只用 Unity 引擎自带 API（连 JsonUtility 都没用，JSON 是自己实现的，
见下方"为什么自己写 JSON"）；Python 侧只用标准库。

---

## 与 unity-python-bridge 的关系

两者是**独立的两套东西**，可以同时装在同一个工程里、同时开着、互不干扰：

| | unity-python-bridge | 本库（unity-python-bridge-runtime） |
|---|---|---|
| 目标环境 | 仅 Unity 编辑器（Edit / Play 模式） | 运行时 + 打包后的游戏（也能在 Play 模式用） |
| 默认端口 | 21927 | **21928** |
| 配置文件 | `bridge.ini` | `runtime_bridge.ini` |
| C# 命名空间 | `UnityPythonBridge` | `UnityRuntimeBridge` |
| 命令特性 | `[BridgeCommand]` | `[RuntimeCommand]` |
| Python 包 | `unity_bridge` | `unity_rt_bridge` |
| 内置命令 | 63 条场景/Prefab/地形编辑命令 | 6 条 `runtime.*` 自检命令，其余全是你的 |
| 依赖 UnityEditor | 是（整个 Runtime 目录都在 `#if UNITY_EDITOR` 里） | **否**（已用 Unity 2022.3 的引擎 DLL 验证过） |

本库**不引用也不需要** unity-python-bridge 的任何东西；两者的命令注册表互相看不见
（特性类型不同）。想让编辑器做场景搭建、让运行时游戏跑自动化逻辑，可以两个一起用。

---

## 目录结构

```
unity-python-bridge-runtime/
├── Assets/
│   ├── unity-python-bridge-runtime/          ← C# 侧：拷到工程 Assets/ 下
│   │   ├── Runtime/                          # 全部运行时安全，可在 Player 中运行
│   │   │   ├── RtJson.cs                     # 自实现的 JSON 解析/序列化（纯 C#，无 Unity 依赖）
│   │   │   ├── RuntimeBridgeJson.cs          # Unity 值 → JSON 的转换（Vector3/Color/对象引用…）
│   │   │   ├── RuntimeCommandAttribute.cs    # [RuntimeCommand] 特性
│   │   │   ├── RuntimeBridgeContext.cs       # 上下文 / 参数封装 / 处理器委托
│   │   │   ├── RuntimeBridgeDispatcher.cs    # 反射扫描 + 命令分发
│   │   │   ├── RuntimeBridgeRunner.cs        # 主线程队列（带帧预算）
│   │   │   ├── RuntimeBridgePump.cs          # 主线程心跳（常驻隐藏物体）
│   │   │   ├── RuntimeBridgeServer.cs        # TCP 服务器 + 自动开服
│   │   │   ├── RuntimeBridgeConfig.cs        # 读 StreamingAssets 下的 ini
│   │   │   ├── RuntimeBridgeInfo.cs          # 常量（版本/端口/目录名）
│   │   │   └── RuntimeBridgeSystemCommands.cs# 内置 runtime.* 命令
│   │   ├── Editor/RuntimeBridgeMenu.cs       # 仅编辑器菜单（不进 Player，可选）
│   │   └── Samples/RuntimeBridgeSampleCommands.cs  # 示例命令，跑通后可删
│   └── StreamingAssets/
│       └── unity-python-bridge-runtime/      ← 随包发布：Python + 配置
│           ├── runtime_bridge.ini            # 两侧共用的配置
│           └── python/
│               ├── unity_rt_bridge/          # 客户端包（config/client/cli）
│               └── scripts/
│                   ├── mock_runtime_server.py # 没 Unity 时的模拟服务器
│                   └── selftest.py            # 端到端自测
├── link.xml.example                          # IL2CPP 代码剥离的示例配置
└── README.md
```

放在 `StreamingAssets` 下的原因：打包后它会原样出现在 `<游戏>_Data/StreamingAssets/` 里，
Python 客户端和配置跟着游戏走，现场不用再单独拷一份、也不会出现"版本对不上"。

---

## 安装（三步）

1. **拷代码**：把本仓库 `Assets/` 下的两个目录合并进你的 Unity 工程：
   - `Assets/unity-python-bridge-runtime/` → `<你的工程>/Assets/unity-python-bridge-runtime/`
   - `Assets/StreamingAssets/unity-python-bridge-runtime/` → `<你的工程>/Assets/StreamingAssets/unity-python-bridge-runtime/`

   > ⚠️ 两个都要拷。只拷前一个也能跑（用默认端口），但 Python 侧就得自己另找一份了。
   > 目录名不要改——C# 按这个名字找 ini 和 Python 目录。

2. **让 Unity 生成 `.meta`**：导入后 Unity 会自动为脚本生成 `.meta`（GUID）。建议把它们一并提交，
   这样换机器/换工程时 GUID 稳定。

3. **验证**：进 Play 模式，Console 应出现：
   ```
   [UnityRuntimeBridge] 已注册 N 条命令（扫描 M 个程序集）
   [UnityRuntimeBridge] v0.1.0 服务器已启动，监听 127.0.0.1:21928；端口=21928 …
   ```

用 Python 连上去（Python 3.6+，无需装包）：

```bash
cd <你的工程>/Assets/StreamingAssets/unity-python-bridge-runtime/python
python -m unity_rt_bridge ping
python -m unity_rt_bridge list
python -m unity_rt_bridge call sample.scene_info
```

打包后的用法完全一样——把命令行的 `cd` 指向 `<游戏>_Data/StreamingAssets/unity-python-bridge-runtime/python` 即可。

**没有 Unity 也想先试**：用模拟服务器（在 `python/` 目录下）

```bash
python scripts/mock_runtime_server.py        # 终端 A
python -m unity_rt_bridge list               # 终端 B
python scripts/selftest.py                   # 或直接跑自测（46 项，不需要 Unity）
```

---

## 写自己的命令（核心）

新增命令 = **写一个静态方法 + 打一个特性**。不需要改本库任何代码，也不需要注册。

```csharp
using System;
using UnityRuntimeBridge;
using UnityEngine;

namespace MyGame.Bridge
{
    // IL2CPP 打包时加这一行，防止命令被代码剥离删掉（详见"打包注意事项"）
    [UnityEngine.Scripting.Preserve]
    public static class PlayerCommands
    {
        [RuntimeCommand("player.add_item", "给玩家发道具")]
        public static object AddItem(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            int itemId = args.GetInt("itemId", 0);
            int count = args.GetInt("count", 1);

            Player.Inventory.Add(itemId, count);
            ctx.Log($"发了 {itemId} x{count}");       // 打日志统一带前缀

            return new { itemId, count, total = Player.Inventory.Count };
        }

        [RuntimeCommand("player.get_state", "读取玩家状态", ReadOnly = true)]
        public static object GetState(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
        {
            return new
            {
                hp = Player.Hp,
                position = Player.transform.position,   // Vector3 自动变成 {x,y,z}
                buffs = Player.Buffs,                    // List/数组自动变成 JSON 数组
            };
        }
    }
}
```

Python 侧立刻可用（按名字调）：

```bash
python -m unity_rt_bridge call player.add_item --arg itemId=1001 --arg count=3
python -m unity_rt_bridge call player.get_state
```

```python
from unity_rt_bridge import RuntimeBridgeClient

with RuntimeBridgeClient() as client:
    client.call("player.add_item", itemId=1001, count=3)
    print(client.call("player.get_state"))
```

### 签名约定

```csharp
public static object MethodName(RuntimeBridgeContext ctx, RuntimeBridgeArgs args)
```

| 项 | 说明 |
|---|---|
| 必须是 | `public static`，四个参数类型严格匹配，不能是泛型方法 |
| 返回值 | 任意对象 → 自动序列化为 JSON；返回 `void` 也行（`data` 为 `null`） |
| 抛异常 | 会被转成 `{"ok":false,"error":"异常消息"}`，完整堆栈进 Unity Console |
| `ctx` | `Command` / `RequestId` / `Peer` / `Token`，以及 `Log/LogWarning/LogError` |
| `args` | 按键取值，缺省返回你给的默认值；键名不做任何限制 |
| 命名建议 | `域.动作`，全小写下划线，如 `player.add_item`、`battle.start` |
| `ReadOnly` | 标记后 AI/网关知道这条不改状态，可以免二次确认 |

### 参数怎么读

参数是任意结构的 JSON，你的命令自己决定读哪些键——这是本库能"方便接入具体玩法"的关键，
不用去改任何公共的参数类：

```csharp
args.GetInt("count", 1)              // 整数
args.GetFloat("speed", 1f)           // 浮点
args.GetString("name", "Bob")        // 字符串
args.GetBool("god", false)           // 布尔
args.GetEnum("mode", MyMode.Normal)  // 枚举（按名字，忽略大小写）
args.GetStringArray("tags")          // string[]，没有返回 null
args.GetFloatArray("weights")        // float[]
args.GetVector3("position", Vector3.zero)   // 支持 [1,2,3] 或 {"x":1,"y":2,"z":3}
args.GetColor("color", Color.white)         // 支持 "#RRGGBB"、[r,g,b,a]、{"r":..}
args.Has("x") / args.GetNode("x")    // 原始节点，自己处理复杂结构
args.Require("name")                 // 必填，缺失直接抛清晰异常
```

### 返回值怎么给

```csharp
return new { ok = true, score = 42 };        // 匿名对象 → 自动展开字段
return new List<object> { ... };             // 集合 → JSON 数组
return someString;                           // 字符串 → JSON 字符串（不会当 JSON 解析）
return RuntimeBridgeJson.Raw("{\"a\":1}");   // 已经是 JSON 文本时才用 Raw
```

Unity 类型有专门处理：`Vector2/3/4`、`Quaternion`、`Color`、`Rect`、`Bounds`、`LayerMask`
按字段展开；`GameObject`/`Component`/材质/贴图这类**对象只输出引用信息**，不递归展开
（否则 Transform 会把整棵场景拖出来）：

```json
{"$ref":"GameObject","name":"Player","instanceId":-1234,"activeSelf":true,"path":"Root/Player"}
```

自己写的普通类会被反射展开字段与只读属性，可以用 `[RtJsonIgnore]` 排除单个成员。

---

## Python 侧

### 当库用

```python
from unity_rt_bridge import RuntimeBridgeClient, RuntimeBridgeError

with RuntimeBridgeClient() as client:          # 参数默认读 ini，也可显式指定
    client.wait_until_ready(timeout=30)        # 等游戏启动完成再调
    names = client.command_names()             # 看有哪些命令
    print(client.call("player.get_state"))
    print(client.call("player.add_item", {"itemId": 1001}, count=3))
```

`call()` 在业务失败时抛 `RuntimeBridgeError`；想要完整响应（含 `ok/error`）用 `call_raw()`。
一个实例内部是一条长连接，**不要跨线程共享同一实例**。

### 命令行

```bash
python -m unity_rt_bridge list                        # 表格列出所有命令
python -m unity_rt_bridge list --filter player --json  # 过滤 + 原始 JSON
python -m unity_rt_bridge ping | version | stats | config
python -m unity_rt_bridge call player.add_item --arg itemId=1001 --arg count=3
python -m unity_rt_bridge call player.set_pos --arg position=[1,0,2] --arg color='"#ff0000"'
python -m unity_rt_bridge call p --args '{"复杂":{"结构":[1,2]}}'      # 复杂参数直接给 JSON
echo '{"a":1}' | python -m unity_rt_bridge call p --stdin             # 从管道读参数
python -m unity_rt_bridge call p --compact > out.json                 # 单行输出便于下游处理
```

`--arg KEY=VALUE` 的值会自动识别类型：`true/false` → 布尔，`null` → 空，
整数/小数 → 数字，`[..]`/`{..}` → JSON，其余按字符串。
键支持 `a.b` 造嵌套（`--arg player.name=Bob`）。
想强制当字符串就用引号包住：`--arg code='"007"'`。

全局参数（`--host/--port/--token/--timeout/--ini`）默认读 ini，可临时覆盖。
退出码：`0` 成功、`1` 调用或连接失败、`2` 参数用法错误。

---

## 配置：`runtime_bridge.ini`

改完**下次运行生效**，不需要重新打包（它就在 StreamingAssets 里；打包后的游戏改这个文件也有效）。

| 节 | 键 | 默认 | 说明 |
|---|---|---|---|
| `[server]` | `port` | `21928` | 监听端口（故意与 unity-python-bridge 的 21927 错开） |
| | `bind` | `127.0.0.1` | 监听地址。填 `any` 会监听所有网卡，**风险自负** |
| | `token` | 空 | 访问令牌。非空时所有请求都要带上它 |
| | `autostart` | `true` | 游戏启动时自动开服；`false` 则需代码里调 `RuntimeBridgeServer.Start()` |
| | `flush_budget_ms` | `8` | 每帧执行命令的时间预算，超出的留到下一帧，防卡顿 |
| | `max_queued` | `64` | 队列积压上限，超过直接回错误，防止雪崩 |
| | `log_commands` | `false` | 是否把每条命令的名称与耗时写进日志 |
| `[python]` | `host/port/token/timeout` | 空 | 只给 Python 侧用；留空则回退到 `[server]` 或内置默认值 |

文件缺失或某项写错时，**该项回退默认值，不会影响游戏启动**。

---

## 打包注意事项

### 1. IL2CPP 代码剥离（最容易踩的坑）

命令是靠反射注册的，静态方法只被反射引用——在编辑器里从不剥离所以一切正常，
**打包后剥离器会把它们当成没人用的代码删掉**，表现为"命令莫名不存在"。

三种做法任选：

- **推荐**：给命令类加 `[UnityEngine.Scripting.Preserve]`（Unity 2019+ 自带）。
- 或者用 `link.xml` 逐个列出命令类，模板见 `link.xml.example`。
- 或者 Player Settings 里把 `Managed Stripping Level` 设为 `Minimal`（影响整包体积，不推荐）。

### 2. 确认 Python 真的进包了

打包后检查 `<游戏>_Data/StreamingAssets/unity-python-bridge-runtime/` 是否存在，
`python/unity_rt_bridge/` 与 `runtime_bridge.ini` 都在。缺了就是第 1 步的两个目录没拷全。

### 3. 平台限制

`StreamingAssets` 在 Android/iOS/WebGL 上不是普通目录，C# 侧读 ini 会失败并回退默认配置；
Python 侧也没法在手机上跑。**本库面向桌面端（Windows/macOS/Linux）的 Play 模式与打包游戏。**

### 4. 安全

- 默认只绑 `127.0.0.1`，本机进程才能连；`bind` 改成 `any` 等于把游戏交给整个局域网。
- 默认**不校验 token**，方便开发期。发布版建议在 ini 里填一个随机 token。
- 这个通道本质上是"游戏内后门"：**建议只在开发版/内测版开启**（例如 ini 里 `autostart=false`，
  仅在有开发标记时由代码调 `Start()`）。

### 5. 同步调用的边界

当前模型是"一次调用、一次响应"，命令在主线程同步执行：

- 单帧时间预算（`flush_budget_ms`）之外的命令会顺延到后续帧，避免卡顿；但**单个命令自身跑太久仍会卡帧**。
- 需要等待多帧的玩法（播完动画、等玩家输入）不要在一次调用里阻塞，
  改成"命令 A 触发 + 命令 B 轮询状态"两步，或让命令立刻返回一个句柄。
  异步推送响应目前未实现。

---

## 协议（想自己写客户端时看）

单行 JSON，UTF-8，换行分隔。任何语言都能接。

```jsonc
// 请求
{"id": 1, "cmd": "player.add_item", "args": {"itemId": 1001, "count": 3}, "token": ""}
// 成功
{"id": 1, "ok": true, "data": {"itemId": 1001, "count": 3, "total": 8}}
// 失败
{"id": 1, "ok": false, "error": "缺少必填参数 name"}
```

`args` 可以省略（等价于空参数），也接受 `params` 作为别名。`id` 用什么类型都行（整数最省事）。

用 netcat 手动试：

```bash
echo '{"id":1,"cmd":"runtime.ping","args":{}}' | nc 127.0.0.1 21928
```

### 为什么自己写 JSON 而不用 JsonUtility

`JsonUtility` 不支持 `Dictionary`、多态、顶层数组，要求返回值必须是 `[Serializable]` 类，
嵌套还有 10 层上限——用来承载"开发者自定义的任意参数"很快就撑不住。所以
`RtJson.cs` 自己实现了约 600 行的解析与序列化：支持任意嵌套、字典、集合、枚举、循环引用检测、
深度上限，且完全不依赖 UnityEngine（可以脱离 Unity 单独编译测试）。

---

## 排障

| 现象 | 原因 / 处理 |
|---|---|
| 连不上，提示"排查清单" | 游戏没启动 / 端口不一致 / `autostart=false` 且没手动 Start / token 不匹配 |
| Console 里没有"服务器已启动" | 看有没有"监听失败: 端口可能被占用"——换端口，或确认 21927 那边不是同一个端口 |
| Play 模式下命令不执行、队列一直涨 | 隐藏的 `[UnityRuntimeBridge]` 常驻物体被销毁了（别把它算进场景清理逻辑） |
| 打包后 `runtime.list_commands` 只有 6 条 | 你的命令类被 IL2CPP 剥离了，见"打包注意事项 1" |
| 命令名重复的 Warning | 两条命令用了同一个名字，先注册的生效，改名字 |
| 响应超时 | 命令在主线程卡住了（死循环/加载资源），或帧预算把它推到了后续帧 |
| 改了 ini 端口不生效 | 编辑器和打包版各有一份 ini，确认改的是当前在跑的那一份 |

编辑器里还有个辅助菜单：**Tools → Unity Runtime Bridge**，可以打印配置与路径、
列出已注册命令、把 Python 命令复制到剪贴板。

---

## 已验证 / 未验证

- ✅ C# 侧（Runtime + Editor + Samples）用 **Unity 2022.3.62f2 的真实引擎 DLL 编译通过，0 warning 0 error**。
- ✅ Runtime 目录**不引用 UnityEditor** 也能独立编译通过（另建了一个只引 `UnityEngine.CoreModule`
  的工程验证），这是"打包后可用"的前提。
- ✅ 自实现的 JSON 层用 66 项测试覆盖（转义、代理对、整数/浮点精度、循环引用、深度上限、往返一致…）。
- ✅ Python 侧端到端自测 **46 项通过**，**Python 3.6.3 与 3.13.14 均通过**（含 CLI 子进程、令牌校验、
  连接失败提示、参数类型自动识别、以及"拷到 StreamingAssets 布局后自动找到 ini"的部署验证）。
- ⚠️ **尚未做真机 Unity ↔ Python 联调**：需要在一个 Unity 工程里进 Play 模式跑一次。
  测试用的是协议等价的模拟服务器（`scripts/mock_runtime_server.py`）。

## 版本

`0.1.0`（`runtime.version` 命令可查）。改版本号只需改 `RuntimeBridgeInfo.cs`。
