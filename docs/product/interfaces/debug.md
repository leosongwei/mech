# 调试消息与控制台

> **读者**：维护者 / 调参
> **一句话**：服务器开了 `MECH_DEBUG=1` 之后，浏览器控制台能让世界做哪些事——
> 八条消息各自的 payload 与效果、它们和日志等级的区别、以及为什么 `respawn` 是
> 例外。
> **数值出处**：`Net/Server.cs` 的 `handle` / `DEBUG` / `PAUSE_WHEN_EMPTY`、
> `Core/Defs.cs` 的 `ETYPE_*` / `WID_*` / `MOD_*`、`Core/World.cs` 的
> `reset_match`、`Modes/Mode.cs` 的 `console_spawn_extra`
> **核对**：1.39.0 / 2026-11-20

## 1. 两个开关，两件事

它们是两条独立的开关，一个管**控制台能做什么**，一个管**日志写什么**：

| 开关 | 管什么 | 默认 | 谁设 |
| --- | --- | --- | --- |
| `MECH_DEBUG=1` | 七条 `debug_*` 消息是否被接受；设置菜单里的 `DEBUG` 区块是否出现 | 关（`./dev.sh` 起的服务器**是开的**） | 启动服务器的环境变量 |
| `MECH_LOG_LEVEL` | 写进日志文件的等级：`INFO`（默认）只要罕见事件，`DEBUG` 连每发弹的起爆、每次脱锁、每次击落都写 | `INFO` | 启动服务器的环境变量 |

所以：

* `MECH_DEBUG` 打开而 `MECH_LOG_LEVEL=INFO`（`./dev.sh` 的默认组合）：控制台能用，
  日志只有偶发几行。
* 想看逐发的行要另起一次：`MECH_LOG_LEVEL=DEBUG ./dev.sh range`，然后
  `tail -f /tmp/mech-range.log`。
* `MECH_DEBUG` 关掉时，七条 `debug_*` 消息被当成不认识的消息**忽略**，`DEBUG`
  区块也不在菜单里（菜单只剩音量与画质）。`respawn` 不受影响。
* 空服务器上世界是**暂停**的（没有玩家连接就不推进）；`MECH_DEBUG=1` 顺手把这个
  暂停也关掉，所以脚本、复盘与 `--check` 能看到一个没有浏览器也在跑的世界。

## 2. 消息表

全部从游戏页面的控制台发出，走大厅建立的那条 websocket：

```js
const g = window.__game;
g.net.send({ t: 'debug_spawn', etype: 3, dist: 400 });
```

| `t` | payload（括号里是默认值） | 效果 |
| --- | --- | --- |
| `respawn` | — | 跳过死亡倒计时，下一 tick 立刻重生（**不需要 `MECH_DEBUG`**） |
| `debug_restart` | — | 清场重开一局，地图不变；消息区写 `MATCH RESTARTED` |
| `debug_god` | `on`（`true`） | 免伤开关；消息区写 `GOD MODE ON` / `GOD MODE OFF`，`meta.god` 镜像它 |
| `debug_spawn` | `etype`（`3` APC）、`dist`（`800` m）、`angle`（`0`，相对机身朝向）、`faction`（敌方）、`weapons`（可选武器 id 列表） | 在身前 `dist` 米处放一台单位；消息区写 `DEBUG SPAWN <etype>` |
| `debug_trees` | `dist`（`200` m）、`angle`（`0`）、`radius`（`2` 瓦片）、`forest`（`true` 密林 / `false` 稀疏树） | 在身前长出一片林子，服务端改瓦片并**把同一块矩形发给客户端**重画；消息区写 `DEBUG TREES` |
| `debug_missile` | `wid`（`4` SACLOS）、`dist`（`2000` m，最小 30）、`angle`（`0`）、`alt`（`2` m，相对于自己的地面）、`speed`（武器自己的巡航速度）、`miss`（`0` m 横向偏移） | 往空中放一发朝你飞来的弹；消息区写 `DEBUG MISSILE <key> <dist> m out` |
| `debug_tp` | `dist`（`300` m）、`angle`（`0`） | 把自己沿机身朝向瞬移 `dist` 米，落地高度取地面，**速度清零** |
| `debug_hurt` | `module`（`1` 躯体）、`amount`（`100`）、`base`（`5000`，打**敌方**指挥部的伤害） | 扣自己的血（按部位），`base` 为真时同时打敌方指挥部 |

`angle` 一律是**相对机身朝向**（服务器手里那份，见 4.1）。

### 2.1 `etype` 取值

| 值 | 是什么 | 值 | 是什么 |
| --- | --- | --- | --- |
| `1` | 步枪兵 | `5` | 指挥部 |
| `2` | 反坦克兵 | `6` | 平民 |
| `3` | APC | `9` | 碉堡 |
| `4` | 机甲 | | |

### 2.2 `weapons` 取值

左右两臂的武器 id，省略则按机组的装备模板发：

| id | 武器 | id | 武器 |
| --- | --- | --- | --- |
| `0` | 空挂点 | `6` | 5.56 mm 步枪 |
| `1` | 30 mm 机炮 | `7` | RPG |
| `2` | 12.7 mm 机枪 | `8` | 105 mm 滑膛炮 |
| `3` | 无制导火箭巢 | `9` | APC 反坦克导弹 |
| `4` | SACLOS 反坦克导弹 | `10` | 高爆火箭巢 |
| `5` | 顶攻红外反坦克导弹 | | |

### 2.3 `module` 取值

`0` 底盘（机甲的主血池）、`1` 躯体（几何上的上半球，伤害记账到主血池）、
`2` 左臂、`3` 右臂。打掉一条臂等于那条臂的武器与弹药一起报废（规则见
[../rules/damage.md](../rules/damage.md)）。

## 3. 判定与边界

* **七条 `debug_*` 需要 `MECH_DEBUG=1`**；没有它，消息被忽略且不给任何提示。
* **大多需要一台活着的机甲**：`debug_spawn` / `debug_trees` / `debug_missile` /
  `debug_tp` 在死亡期间**什么也不做**（静默返回）；`debug_god` 与
  `debug_restart` 不需要（它们只改玩家标记与世界）。
* **`debug_hurt` 的 `amount` 是"点"**：`100` 已近一台机甲的一条命（血量见
  [../rules/units.md](../rules/units.md)）；想看某个部位中弹就传小一点，想自毁就传
  四位数。`base` 那一支打的是**敌方**指挥部。
* **`debug_tp` 是瞬移不是飞行**：位置与高度一起写，`vel` 清零，所以落点不会带出
  一段幽灵位移。
* **`debug_missile` 的发射者是"没有人"**（`src = -1`）：它沿一条直线飞向你的观瞄
  眼，不归任何一方的射手；SACLOS 没有波束可骑，会按自己的航向滑出去——正好是
  验收告警与拦截时要的那一发。顶攻弹会**锁你**（它需要一个目标）。`miss` 把瞄准点
  横向挪开，用来造"它要从旁边过"的场面（告警圈应当保持安静）。
* **`debug_spawn` 在战斗模式放出来的是不动的车**：它带着 AI 机组，但没有本线的
  任务标记，所以不索敌、不推进，是一个靶子；靶场模式会给它补上任务标记，于是它会
  打人（见 [../modes/range.md](../modes/range.md)）。
* **`debug_restart` 与一局结束后自动重开是同一条路**：清场、重建基地与城镇、玩家
  回到开局资金，战绩保留（规则见 [../modes/battle.md](../modes/battle.md)）。

## 4. 控制台示例

### 4.1 先把机甲搬到想看的地方

`debug_tp` 用的是**服务器手里那份机身朝向**（最后一条输入消息的值），不是页面上
改过的 `g.input.viewYaw`，所以每转一次头要等几 tick 再瞬移；读回位置要看服务端的
`px` / `pz`（`x` / `z` 是插值出来的画面值）：

```js
const g = window.__game, M = g.net.meta, me = () => g.net.entities.get(M.mech);
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const TX = 2600, TZ = 2400;                 // 想去的地方
for (let k = 0; k < 8; k++) {
  const p = me();                           // p.px / p.pz = 服务端位置
  const dx = TX - p.px, dz = TZ - p.pz, d = Math.hypot(dx, dz);
  if (d < 20) break;
  g.input.yaw = Math.atan2(dz, dx);
  await sleep(300);                         // 让服务器收到新的朝向
  g.net.send({ t: 'debug_tp', dist: Math.min(d, 1200) });
  await sleep(1100);                        // 等下一个快照
}
```

### 4.2 常见几手

```js
g.net.send({ t: 'debug_spawn', etype: 3, dist: 400, angle: 0.3 });   // 前方 400 m 放一台 APC
g.net.send({ t: 'debug_spawn', etype: 4, faction: 0, weapons: [1, 5] });  // 红方机甲：30mm + 顶攻
g.net.send({ t: 'debug_hurt', module: 2, amount: 999 });            // 打掉左臂
g.net.send({ t: 'debug_hurt', base: 3000 });                        // 削敌方指挥部 3000 点
g.net.send({ t: 'debug_trees', dist: 150, radius: 2 });             // 身前种一片林子
g.net.send({ t: 'debug_missile', wid: 5, dist: 2000, alt: 40 });    // 一发顶攻来袭
g.net.send({ t: 'debug_missile', wid: 4, dist: 1800, alt: 250, miss: 400 }); // 一发从旁边过
g.net.send({ t: 'debug_god', on: true });                           // 之后读 g.net.meta.god 确认
g.net.send({ t: 'respawn' });                                       // 死着的时候立刻回来
```

免费看机器：`g.freeCam(id)` 把镜头停在任何一台车上，`g.freeCam(null)` 还回去
（相机与截图的做法见 [../../dev/browser.md](../../dev/browser.md)）。

## 5. 相关页

* 设置菜单里的三个按钮（`RESPAWN` / `RESTART` / `GOD`）：
  [../hud/panels.md](../hud/panels.md)
* 靶场怎么用这些指令量东西：[../modes/range.md](../modes/range.md)
* 战斗模式的出兵与重开：[../modes/battle.md](../modes/battle.md)
* 伤害落在哪个部位：[../rules/damage.md](../rules/damage.md)
* 日志等级与构建流程：[../../dev/checks.md](../../dev/checks.md)
