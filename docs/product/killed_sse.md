# 击杀流（`GET /events/kills`）

屏幕外的一条线：**每一条玩家参与的击杀，以 Server-Sent Events 的形式发出去**。
给脚本、直播叠加层、第二块屏、计分板这类**不跑游戏本体的读者**用。

* 消息区那一行字长什么样、什么颜色、存活多久：见 [screens.md](screens.md) 第 2.13 节。
  本文的这条流和那一行是**同一份数据**（`Combat.HitAttributions`），只是读者不同。
* 代码：`Systems/Support/KillFeed.cs::publish`（判据与记录）、`Net/KillStream.cs`
  （订阅者队列与 JSON 帧）、`Net/Server.cs::kill_stream_endpoint`（路由、心跳、断连）。

## 1. 端点

```
GET /events/kills
```

* 和游戏同一个端口（战斗服 `8010`，靶场服 `8011`），不需要额外的鉴权或开关。
* 响应头：`Content-Type: text/event-stream`、`Cache-Control: no-cache`、
  `X-Accel-Buffering: no`（代理不许攒包），chunked，连接一直开着。
* 连上先收到一条注释 `: mech kills`，之后按 §2 的三种帧往下走。
* **不补发历史**：连上之前的击杀全部没有，连上就等下一杀。
* 断线由客户端自己重连（浏览器的 `EventSource` 默认就会，2–3 s 起）。

## 2. 帧

SSE 的三条规矩在这个流里的样子：

| 时机 | 线上一行 | 说明 |
| --- | --- | --- |
| 连上 | `: mech kills` | 注释，证明流开了 |
| 一条击杀 | `data: {"killed":{...},"by":{...},"weapon":"...","distance":1654.1}` | 一帧一条，帧间一空行 |
| 静默 15 s | `: tick 4210` | 心跳，带服务器当前的 tick |

心跳是 **`HEARTBEAT_S` = 15 s** 一条：一条击杀可能隔几分钟，没有心跳的话中间层会把这
条连接当死连接掐掉，客户端也分不清"没人死"和"没在跑"。它是 SSE 注释，`EventSource`
直接忽略，自己读原始行的脚本可以拿 tick 判断世界在不在推进。

## 3. 记录

```json
{
  "type": "killed",
  "killed": { "name": "jack", "is_player": true, "unit_type": "mech" },
  "by":     { "name": "Char", "is_player": false, "unit_type": "mech" },
  "weapon": "105mm smoothbore",
  "distance": 1654.1
}
```

| 字段 | 类型 | 含义 |
| --- | --- | --- |
| `type` | string | **这条消息是什么**。现在只有 `killed` 一种；以后流里放别的消息（比如占领、重开）时用它区分，读者应当先读这个字段，而不是从形状去猜 |
| `killed.name` | string | 被击毁者的名字。玩家是**大厅呼号**；AI 机甲是名字池里的驾驶员名；没有名字的单位用类型名（`APC`、`RIFLEMAN`、`COMMAND BASE`） |
| `killed.is_player` | bool | 这一端有没有玩家在开（`owner >= 0`） |
| `killed.unit_type` | string | 被击毁者的类型，枚举见 §3.1 |
| `by.name` | string | 打出**最后一击**的那一发的发出者 |
| `by.is_player` | bool | 那一发是不是玩家打的（AI 车组/步兵为 `false`） |
| `by.unit_type` | string | 击杀者的类型，枚举见 §3.1 |
| `weapon` | string | 造成最后一击的武器名，小写，和消息区行里的写法一致，枚举见 §3.2 |
| `distance` | number | 这一发从**出膛位置**到**落点**的直线距离（斜距），米，一位小数；`-1` 表示没有发射点可量（见 §4.3） |

名字里**没有 `[bot]` 后缀**（消息区有），因为 `is_player` 已经把这件事说清楚了：
外面读这条流的人要的是数据，不是给眼睛看的排版。

### 3.1 `unit_type` 枚举

就是 `Core/Defs.cs` 里 `Defs.UNIT_DEFS[etype].key`，跟着单位表走（加单位就加一行）。
一律小写下划线，不随消息区的叫法变。

| `unit_type` | 是什么 | 能被打死 | 能开火（能当 `by`） |
| --- | --- | --- | --- |
| `mech` | 机甲（玩家与 AI 的主力） | ✔ | ✔ |
| `apc` | 轮式装甲车 | ✔ | ✔ |
| `rifleman` | 步枪兵 | ✔ | ✔ |
| `at_rifleman` | 反坦克兵（RPG） | ✔ | ✔ |
| `command_base` | 指挥部 | ✔（整场胜负） | ✘ |
| `civilian` | 平民 | ✔ | ✘ |
| `bunker` | 混凝土碉堡 | ✘（`invulnerable`，打不坏） | ✘ |
| `wreck` / `corpse` | 残骸 / 尸体 | —（不是 `unit`，不进击杀判定） | ✘ |

所以实际会出现的情况：`killed.unit_type` 是前六种之一；`by.unit_type` 通常是 `mech`、
`apc`、`rifleman`、`at_rifleman` 之一。

### 3.2 `weapon` 枚举

是 `Defs.WEAPONS[*].name` 转小写（`Defs` 里存的是大写，消息区也写小写）。括号里是它
在代码和商店里的 `key`，加上挂它的平台：

| `weapon` | `key` | 谁用 |
| --- | --- | --- |
| `30mm autocannon` | `ac30` | 机甲左臂（**所有**机甲配置的左臂都是它） |
| `105mm smoothbore` | `g105` | 机甲右臂 / 靶场防御机甲 |
| `unguided rocket pod` | `rocket` | 机甲右臂（玩家默认配置的右臂） |
| `saclos atgm` | `saclos` | 机甲右臂 |
| `top-attack ir atgm` | `topattack` | 机甲右臂 |
| `rpg launcher` | `rpg` | 机甲右臂 / 反坦克兵 |
| `12.7mm machine gun` | `mg127` | APC 左臂 / 机甲右臂（AI） |
| `apc atgm` | `apc_atgm` | APC 右臂 |
| `5.56mm rifle` | `rifle556` | 步枪兵 |
| `empty` | `none` | 空挂点：正常击杀不会出现，见到就当未知 |

武器名随 `Core/Defs.cs` 的 `WEAPONS` 表走（[weapons.md](weapons.md) 有全参数表）；新增
武器时这张表要跟着加一行。

## 4. 口径

### 4.1 谁能上：至少一端是玩家

**和消息区同一条规矩**：一条击杀只要有玩家在任意一端（自己击杀的 / 自己被打死的），
就发。AI 打 AI 不发——16 km 战场上那些每秒都在发生，外面看也看不完，而且两端都会是
`"is_player": false`，没有信息量。

"在击杀里"看的是**打出最后一击的那一发**：自己先打伤、最后由 AI 收掉的敌人算 AI 的
击杀，因此**不出现在流里**；赏金照旧归玩家（那是显示器左下角的事，见
[screens.md](screens.md) 第 2.3 节）。

### 4.2 三种不上流的死亡

* **两端都不是玩家**（AI 打 AI、车组打车组）。
* **无名爆炸**：一发没人认领的爆炸（比如连环引爆里没有击杀者的那种）没有 `by`，
  也就没有可以写的名字。
* **同阵营事故**：击杀者阵营与被击毁者相同。

### 4.3 `distance` 是"这一炮"的距离

* 起点是**弹药出膛的位置**，终点是**它落点**（直击是命中点，空爆是引爆点），取三维
  直线距离。它在出膛那一刻和击杀归属一起冻住，所以：开完炮被打死、炮弹随后落地命毙，
  距离仍然是**那一炮**的；同一发炮弹炸死两个目标、或者残骸连锁引爆带走第二个，两条
  记录报的都是**同一个**距离。
* `-1` 的两种情况：调试旋钮直接扣血这类没有发射点的伤害、以及任何没有弹道起点的爆
  炸。读到 `-1` 就当"这一条没有距离"，不要当 0 米。
* 消息区把同一个数显示成千米（`0.81 km` / `2.6 km`），流里给的是**米的原值**，怎么
  写由读者决定。

### 4.4 步兵也在流里

消息区不刷步兵（一场仗打死几十个，会把整列刷满）。这条流只在**玩家参与的**那些死亡上
二者一致；**玩家打死一个步兵会出现在流里**（`killed.name` 是 `RIFLEMAN` 之类），因为
对屏幕外的读者来说那仍然是一条击杀记录。

## 5. 心跳与空服暂停

* 静默 15 s 发一条 `: tick N`。`N` 是服务器当前的 tick（30 Hz），可以用来判断世界在
  不在动。
* **流读者不是玩家，不会把空服拽起来**。没有玩家连接时服务器是**暂停**的（见
  `AGENTS.md`「An empty server is a paused server」）：世界不推进，tick 不动，于是流上
  只剩心跳，而且是**同一个 tick 号**反复出现。这是有意的——一个挂着的订阅者不应该让
  一场仗在没人看的时候自己打完。
  例外只有开发环境：`./dev.sh` 起服务器时带 `MECH_DEBUG=1`，那个开关**关掉**了暂停
  （为的是让脚本、复盘和 console 会话能看一个没有浏览器也在跑的世界），所以本地测试
  时会看到 tick 一直涨、AI 互相打——但那种击杀两端都不是玩家，不会出现在流里。
* 有人部署进来之后，tick 开始增长，击杀开始出现。断线重连不会补上断线期间的记录。

## 6. 可靠性：漏了就漏了

* 每个订阅者一条**上限 64 条**的队列，满了**丢最旧的**（`DropOldest`）。读得慢的
  客户端丢的是击杀，绝不会让服务器等它，也不会把 tick 拖慢。
* 没有历史、没有游标、没有 `Last-Event-ID` 语义：重连等于重新开始。
* 服务器重启 = 流断 = 客户端重连，仅此而已。

## 7. 怎么读

命令行：

```bash
curl -sN http://127.0.0.1:8010/events/kills
```

浏览器 / 任何有 `EventSource` 的地方：

```js
const es = new EventSource('http://127.0.0.1:8010/events/kills');
es.onmessage = (e) => {
  const k = JSON.parse(e.data);
  if (k.type !== 'killed') return;      // 以后可能还有别的消息类型
  console.log(`${k.by.name} (${k.by.unit_type}) killed ${k.killed.name} `
            + `(${k.killed.unit_type}) with ${k.weapon} at ${k.distance} m`);
};
```

Python（标准库，够用）：

```python
import json, urllib.request

with urllib.request.urlopen('http://127.0.0.1:8010/events/kills') as r:
    for raw in r:                      # 一帧一行，空行是帧结束
        line = raw.decode().rstrip('\n')
        if line.startswith('data: '):
            kill = json.loads(line[6:])
            if kill['type'] == 'killed':
                print(kill)
```

`data:` 之后是一个完整的 JSON 对象，一帧只有一行数据，所以按行解析就够了（不用处理
多行 `data:` 拼接）。

## 8. 这一版不提供的

* **历史击杀**、断线补偿、`Last-Event-ID` 续传。
* **阵营、时间戳、坐标、模块**（打的是底盘还是手臂、是不是残骸连锁引爆）：现在只有
  类型、名字、是不是玩家、武器、距离。要加字段就得同时改 `KillFeed.KillRecord`、
  `KillStream._payload` 和本文档。
* **事件过滤参数**（比如 `?player_only=1`）：口径是固定的，见 §4。
* **其它消息类型**：`type` 字段留着位置，但今天只有 `killed`。

## 9. 实现位置（维护者）

| 位置 | 干什么 |
| --- | --- |
| `Systems/Mortality.cs::_kill` | 死亡判定的那一刻调 `KillFeed.publish(world, eid)`（在 `e.destroy` 之前，那时被击毁者的列还在） |
| `Systems/Support/KillFeed.cs::publish` | §4 的判据，产出 `KillRecord`；通过 `on_kill` 钩子交出去（harness 里是 `null`，一次判空） |
| `Net/KillStream.cs` | 订阅者队列（64 条、`DropOldest`）、JSON 帧（`Defs.WIRE_JSON`）、武器名转小写 |
| `Net/Server.cs::kill_stream_endpoint` | 路由、响应头、开流注释、15 s 心跳、断连清理；`HEARTBEAT_S` 在 `Server` 的常量区 |

改这条流的**口径**（谁能上、字段含义）要同步本文档和 `CHANGELOG.md`；改**传输细节**
（心跳间隔、队列长度）只改这里的数字即可。
