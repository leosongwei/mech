# 击杀流：`GET /events/kills`

> **读者**：外部集成
> **一句话**：屏幕外的一条 Server-Sent Events 流，每条"有玩家参与的击杀"一帧 JSON
> ——字段、口径、心跳与可靠性，都在这一页。
> **数值出处**：`Systems/Support/KillFeed.cs`（`publish` / `KillRecord`）、
> `Net/KillStream.cs`（`QUEUE`）、`Net/Server.cs`（`kill_stream_endpoint` /
> `HEARTBEAT_S` / `PAUSE_WHEN_EMPTY`）、`Core/Defs.cs`（`unit_key` /
> `WEAPONS[*].name` / `WIRE_JSON`）
> **核对**：1.39.0 / 2026-11-20

屏幕右上那一行字长什么样、什么颜色、活多久，归
[../hud/messages.md](../hud/messages.md)；本页只写这条流的**外部契约**。两边的数据
是同一份（同一时刻冻住的同一批归属），所以两边读出来的可以互相对照。

## 1. 端点

```
GET /events/kills
```

* 与游戏同一个端口（战斗服 `8010`，靶场服 `8011`），没有额外的鉴权、开关或参数。
* 响应头：`Content-Type: text/event-stream`、`Cache-Control: no-cache`、
  `X-Accel-Buffering: no`（代理不许攒包），响应缓冲关闭，连接一直开着。
* 连上**先收到一条注释** `: mech kills`，证明流开了；之后按第 2 节的三种帧往下走。
* 断线由客户端自己重连（浏览器的 `EventSource` 默认就会，2–3 s 起）。
* **不补发历史**：连上之前的击杀全部没有，连上就等下一杀。

## 2. 帧

标准 SSE，一帧一行数据加一个空行：

| 时机 | 线上一行 |
| --- | --- |
| 连上 | `: mech kills` |
| 一条击杀 | `data: {"type":"killed","killed":{…},"by":{…},"weapon":"…","distance":1654.1}` |
| 静默 15 s | `: tick 4210` |

一帧只有一行 `data:`，所以按行读就够，不用处理多行拼接。注释行（`:` 开头）按 SSE
的规矩忽略即可；读原始行时可以拿 `: tick N` 判断世界在不在推进。

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
| `type` | string | **这条消息是什么**。现在只有 `killed`；以后流里放别的消息时用它区分，所以先读这个字段，不要从形状去猜 |
| `killed.name` | string | 被击毁者的名字：玩家是**大厅呼号**，AI 机甲是名字池里的驾驶员名，没有名字的单位用类型名（`APC`、`COMMAND BASE`） |
| `killed.is_player` | bool | 这一端是不是玩家在开 |
| `killed.unit_type` | string | 被击毁者的类型（3.1 节） |
| `by.name` | string | 打出**最后一击**的那一发的发出者：玩家是呼号，AI 机甲是驾驶员名，车组与步兵没有名字所以用类型名 |
| `by.is_player` | bool | 那一发是不是玩家打的 |
| `by.unit_type` | string | 击杀者的类型（3.1 节） |
| `weapon` | string | 造成最后一击的武器名，**小写**（3.2 节） |
| `distance` | number | 这一发从出膛到落点的三维直线距离，米，一位小数；`-1` 表示没有发射点可量（4.3 节） |

**名字里没有 `[bot]` 后缀**：`is_player` 已经把这件事说清楚了，外面读这条流的人
要的是数据，不是给眼睛看的排版。

### 3.1 `unit_type`

就是单位表里的 `key`，小写下划线，不随界面上的大写叫法变：

| `unit_type` | 是什么 | 能出现在 `killed` | 能出现在 `by` |
| --- | --- | --- | --- |
| `mech` | 机甲（玩家与 AI 的主力） | ✔ | ✔ |
| `apc` | 轮式装甲车 | ✔ | ✔ |
| `command_base` | 指挥部（整场胜负的那一杀） | ✔ | ✘（没有武器） |
| `rifleman` | 步枪兵 | ✘（步兵不上流，4.2） | ✔ |
| `at_rifleman` | 反坦克兵 | ✘（同上） | ✔ |
| `civilian` | 平民 | ✘ | ✘ |
| `bunker` | 碉堡（打不坏） | ✘ | ✘ |
| `wreck` / `corpse` | 残骸 / 尸体 | ✘（不是战斗单位） | ✘ |

所以 `killed.unit_type` 只会是 `mech` / `apc` / `command_base`，`by.unit_type` 只会是
`mech` / `apc` / `rifleman` / `at_rifleman`——一个步兵用 RPG 掀掉机甲算一条，被打死
的步兵不算。

### 3.2 `weapon`

是武器表里的名字转小写，取值只可能是这十一个之一（括号里是商店与代码里的 `key`）：

`30mm autocannon`（`ac30`）、`105mm smoothbore`（`g105`）、`unguided rocket pod`
（`rocket`）、`he rocket pod`（`rocket_he`）、`saclos atgm`（`saclos`）、
`top-attack ir atgm`（`topattack`）、`rpg launcher`（`rpg`）、`12.7mm machine gun`
（`mg127`）、`apc atgm`（`apc_atgm`）、`5.56mm rifle`（`rifle556`）、`empty`
（`none`，正常击杀不会出现，读到就当未知）。武器参数见
[../rules/weapons.md](../rules/weapons.md)。

## 4. 口径

### 4.1 谁能上：至少一端是玩家

一条击杀只要有玩家在**任意一端**就发：自己的击杀、自己被打死。**AI 打 AI 不发**
——16 km 战场上那些每秒都在发生，发出来两端都是 `"is_player": false`，没有信息量。

"在击杀里"看的是**打出最后一击的那一发**：自己先打伤、最后由 AI 收掉的敌人算那条
AI 的击杀，所以**不出现在流里**（赏金照旧归玩家，那是 HUD 的事，见
[../rules/economy.md](../rules/economy.md)）。

### 4.2 四种不上流的死亡

* **步兵**（步枪兵 / 反坦克兵 / 平民——所有"人"）。一场仗打死几十个，发出来就是
  刷屏；**步兵当击杀者的那一条照发**。
* **两端都不是玩家**（车组打车组）。
* **没有击杀者的爆炸**：一发没人认领的爆炸（比如连环引爆里那种）没有 `by`，也就
  没有可以写的名字。
* **同阵营事故**：击杀者与被击毁者同阵营。

### 4.3 `distance` 是"这一炮"的距离

* 起点是**弹药出膛的位置**，终点是**它落点**（直击是命中点，空爆是引爆点），取三维
  直线距离。它在出膛那一刻和击杀归属一起冻住，所以：开完炮被打死、炮弹随后落地打死
  人，距离仍然是**那一炮**的；同一发炸死两个目标、或者残骸连锁引爆带走第二个，两条
  记录报的是**同一个**距离。
* `-1`：调试指令直接扣血这类没有发射点的伤害，以及任何没有弹道起点的爆炸。读到
  `-1` 就当"这条没有距离"，不要当 0 米。
* 界面把同一个数写成千米（`0.81 km`），流里给的是**米**，怎么显示由读者决定。

### 4.4 同一份数据的两种读者

两端是同一批击杀、同一份归属、同一个距离；差别只在渲染——界面那一行带阵营颜色、
底板、`[bot]` 后缀和千米，这条流给纯数据、裸名字和米。

## 5. 心跳与空服暂停

* 静默 **15 s** 发一条 `: tick N`。`N` 是服务器当前的 tick（30 Hz），可以判断世界
  在不在动。SSE 注释会被 `EventSource` 直接忽略，又能让中间层不把连接当死连接掐掉。
* **读流的人不是玩家，不会把空服务器拽起来**。没有玩家连接时服务器是暂停的：世界
  不推进、tick 不动，流上只剩心跳，而且是**同一个 tick 号反复出现**。这是有意的——
  一个挂着的订阅者不应该让一场仗在没人看的时候自己打完。
* 开发环境例外：`./dev.sh` 起服务器会带 `MECH_DEBUG=1`，那个开关关掉了这个暂停，
  所以本地能看到 tick 一直涨、AI 互相打——但那种击杀两端都不是玩家，不会出现在
  流里。
* 有人部署进来之后 tick 开始增长，击杀开始出现。

## 6. 可靠性：漏了就漏了

* 每个订阅者一条**上限 64 条**的队列，满了**丢最旧的**（`DropOldest`）。读得慢的
  客户端丢的是击杀，服务器不会等它，也不会把 tick 拖慢。
* **没有历史、没有游标、没有 `Last-Event-ID` 续传**：重连等于重新开始。
* 服务器重启 = 流断 = 客户端重连，仅此而已。
* 一帧只有一条记录，所以不会出现"半条 JSON"。

## 7. 怎么读

命令行（`-N` 关掉 curl 自己的缓冲，否则你会以为是流不动）：

```bash
curl -sN http://127.0.0.1:8010/events/kills
```

浏览器 / 任何有 `EventSource` 的地方：

```js
const es = new EventSource('http://127.0.0.1:8010/events/kills');
es.onmessage = (e) => {
  const k = JSON.parse(e.data);
  if (k.type !== 'killed') return;            // 以后可能还有别的消息类型
  console.log(`${k.by.name} (${k.by.unit_type}) killed ${k.killed.name} `
            + `(${k.killed.unit_type}) with ${k.weapon} at ${k.distance} m`);
};
```

Python 标准库，按行读就够：

```python
import json, urllib.request

with urllib.request.urlopen('http://127.0.0.1:8010/events/kills') as r:
    for raw in r:
        line = raw.decode().rstrip('\n')
        if line.startswith('data: '):
            kill = json.loads(line[6:])
            if kill['type'] == 'killed':
                print(kill)
```

## 8. 这一版不提供的

* **历史击杀**、断线补偿、`Last-Event-ID` 续传。
* **阵营、时间戳、坐标、命中部位、是不是残骸连锁引爆**：现在只有类型、名字、是不是
  玩家、武器、距离。要加字段就得同时改 `KillFeed.KillRecord`、
  `KillStream._payload` 和本文档。
* **过滤参数**（比如"只看某个玩家"）：口径是固定的，见第 4 节。
* **其它消息类型**：`type` 字段留着位置，今天只有 `killed`。

## 9. 实现位置（维护者）

| 位置 | 干什么 |
| --- | --- |
| `Systems/Mortality.cs::_kill` | 死亡判定的那一刻调 `KillFeed.publish`（在实体销毁之前，那时被击毁者的列还在） |
| `Systems/Support/KillFeed.cs::publish` | 第 4 节的判据，产出 `KillRecord`；通过 `on_kill` 钩子交出去（无监听时是 `null`，一次判空） |
| `Net/KillStream.cs` | 订阅者队列（64 条、`DropOldest`）、JSON 帧（`Defs.WIRE_JSON`）、武器名转小写 |
| `Net/Server.cs::kill_stream_endpoint` | 路由、响应头、开流注释、15 s 心跳、断连清理；`HEARTBEAT_S` 在 `Server` 的常量区 |

改这条流的**口径**（谁能上、字段含义）要同步本文档与 `CHANGELOG.md`；改**传输细节**
（心跳间隔、队列长度）只改这里的数字。

## 10. 相关页

* 界面上的击杀行（文案、配色、存活时间）：[../hud/messages.md](../hud/messages.md)
* 死亡判定与伤害归属：[../rules/damage.md](../rules/damage.md)
* 赏金与计分：[../rules/economy.md](../rules/economy.md)
* 网络帧与空服暂停：[../systems/net.md](../systems/net.md)
