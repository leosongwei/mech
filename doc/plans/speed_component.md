# 一个速度，一个位置：速度组件重构计划

状态：**已实现（2026-11-01）**，P0-P5 全部落地。范围：`backend/server`（ECS +
systems + netcode）、`frontend/src`（只涉及读速度的地方）、`tools/`。

落地时的三处偏差，记在这里以免以后有人按计划原文找：

1. **巡逻靶写的是 `vel_cmd`，"不写 `pos`"**（计划原文如此），所以它现在真的由
   车体自己开（加速、掉头）。原来的"直接挪 `pos`"会让它在 sweep 里 `disp = 0`。
2. **`wheel_speed` / `backout_t` / `blocked_t` 在 P0 里就位**，不是留到 P3：
   越早让名字对上，后面越少误会。
3. **验收多了一个工具**：`tools/top_attack_dive.py`（复飞录到的俯冲），它是这次
   重构的动机来源，也是今后调 `pn_n` / `pn_r` / `pn_v` 的唯一手段。数值见 §3.3，
   实测：同一批 5 发，喂指令速度脱靶 8~36 m，喂真实速度 0.7~2.8 m。

## 1. 为什么

`pos` 和 `vel` 是游戏里每个动态物体共享的两条属性，而本项目的 ECS 哲学正是
这一点：**一个实体就是它拥有的属性集合，一个系统就是对这批属性的查询。**
如果"有位置"和"有速度"在所有会动的实体上是同两个组件，那么任何会动的东西都
可以直接丢给"负责移动"的系统——重力、风、提前量、新的运动学模型、新种类的弹
——都不需要按类型分支。今天它们不是同两个，而且代价已经在付了：

* 攻顶弹**被假动作骗**，不是因为比例导引写错了（`flight.py:109` 是教科书实
  现），而是因为导引头拿到的是车体的**指令速度**（`seeker.py:99`、
  `weapons.py:282`）——飞行员抖一下杆，它立刻就是满速 50 m/s，而车体实际只
  在以 0.3 m/s 爬（真实速度留在 `drive` 里，`movement.py:157-169`）。在靶场
  录到的 5 次俯冲上实测：同一批飞行，喂指令速度脱靶 8~36 m，喂真实速度
  0.7~2.8 m（§3.3，复飞工具 `tools/top_attack_dive.py` 待写）。
* 同样的混淆还藏在命中球表（`projectiles.py:296`）、雷达接近率判定
  （`ai.py:235`）、AI 提前量（`ai.py:122,184,319`）和线上（`netcode.py:179`）
  ——五处独立的地方都在默默假设"`vel` 就是速度"，而一个正在加速、刹车或转弯
  的车体上，这个假设能错到 20 倍。
* 客户端其实早就知道了：它不信任线上的速度，自己从相邻快照差分重建一个真实
  速度（`net.js:108-121`），然后**用那个**去画提前量框和速度表
  （`game.js:947,1232`）。

每加一个功能，这笔税就要再交一次。修法是一条不变式，不是五个补丁。

## 2. 不变式

两个组件、两个含义，到处都一样，永久有效：

| 组件 | 形状 | 单位 | 含义 | 谁写 |
| --- | --- | --- | --- | --- |
| `pos` | `float32[3]` | m | 它在哪里（`x`，向上 `y`，`z`） | 移动它的那个系统，每 tick 一次 |
| `vel` | `float32[3]` | m/s | **它此刻实际在以多快移动**：`pos += vel·dt` | 积分它运动的那个系统 |
| `vel_cmd` | `float32[3]` | m/s | **它被要求达到的速度**——意图，不是物理 | 控制器：飞行员输入、AI、模式的巡逻 |

由此推出的、代码此后可以依赖的规则：

1. `has(vel) ⇒ has(pos)`。有速度就必然有位置。
2. `has(moves) ⇒ has(pos) ∧ has(vel) ∧ has(vel_cmd)`。会动的车体三样齐全：
   `vel_cmd` 是控制器下的令，`vel` 是物理给出的结果，两者的差**就是**这台机器
   （斜坡、转向、一堵墙）。轮式车体额外带自己的运动学状态 `wheel_speed` /
   `backout_t` / `blocked_t`（§4.1）——那是它私有的，不是速度、也不是意图。
3. `has(proj_kind) ⇒ has(pos) ∧ has(vel)`；`has(canister) ⇒ has(pos) ∧ has(vel)`。
   弹和发烟罐没有意图——`vel` 同时就是两者。
4. `vel` 对所有人都是三维的。地面车体的竖直分量来自它正在开过的地形
   （`vel[1] = Δy/dt`），弹的来自重力和升力。谁都不需要特判"车体没有 vy"。
5. 静态景物只带 `pos`、不带 `vel`：爆炸、烟团、残骸、尸体、基地、掩体。
6. 每实体每 tick 有且只有一个 `vel` 写者：车体是 `movement`，弹是
   `projectiles`，发烟罐是 `smoke`。"瞬移"写 `pos` 并把 `vel` 清零；脚本化的
   运动写 `vel_cmd`、让运动学自己去实现，这样碰撞、sweep 表和线上看到的是同一
   个数。

`vel_cmd` 放进 `UNIT_COMPS`，所以静态车体（基地、掩体）也带着一个 0 值。这比
拆开单位属性集便宜（12 字节、少一个 archetype）；"这一个是被驱动的"仍然由
`moves` 标签表示。

## 3. 今天的真实状况（调查）

### 3.1 transform 区（`components.py:22-41`）

```python
ecs.register("pos",  np.float32, (3,), 0.0)   # :23
ecs.register("vel",  np.float32, (3,), 0.0)   # :24  "commanded velocity"
ecs.register("drive", np.float32, (2,), 0.0)  # :25  "the speed it is really rolling"
ecs.register("veh",  np.float32, (3,), 0.0)   # :29  wheeled state: speed/back-out/blocked
ecs.register("yaw"/"pitch"/"leg_yaw"/"turret_yaw"/"sight_yaw"/"sight_pitch", np.float32)
```

`vel` 在不同 archetype 上是**两个不同的含义**：

| archetype | `vel` 是 | `drive` 是 |
| --- | --- | --- |
| 机甲（`UNIT_COMPS`） | 指令（`player_input.py:53`、`ai.py:542`、`raids.py:424`） | 真实的平面速度（`movement.py:157-169`） |
| APC / 轮式 | 指令 | `drive[0]` = 沿车体的有符号车速（`movement.py:73,104`） |
| 步兵 / 平民 | 两者：它就是按指令被推进的（`movement.py:136-137`，积分在 `:172-173`） | 未用（0） |
| 基地 / 掩体 | 从不写（0） | 未用 |
| 子弹 / 火箭 / SACLOS / 攻顶 | 真实速度（`projectiles.py:169`） | 无 |
| 发烟罐 | 真实速度（`smoke.py:113`） | 无 |
| 残骸 / 尸体（`DEBRIS_COMPS`） | 有，但**从不写** | 无 |
| 烟团 / 爆炸 | 无 | 无 |

`veh[0]` 的注释写的是"沿自身轴滚动的速度"，但代码把这个数存进了 `drive[0]`；
`veh[1]`（倒车计时）和 `veh[2]`（顶墙计时）是活的。

### 3.2 所有读车体 `vel` 的地方，以及它们错在哪

| 位置 | 它做什么 | 当 `vel` 是指令时 |
| --- | --- | --- |
| `projectiles.py:296` | `move = vel·dt` → 每个车体的 sweep 位移，`prev = pos - move` | 一个 2 m/s 爬行的车体被记成 50 m/s；`disp` 每 tick 多算最多 1.7 m |
| `ai.py:235` | 雷达接近率判定用整车体自己的速度 | 原地不动但按着杆的车体，响得像在跑 180 km/h |
| `ai.py:122, 184, 239, 319` | 喂给 `_engage` / 反导的提前量 | 朝一个根本没那么快的车体做提前量 |
| `seeker.py:99, 117`、`weapons.py:282` | `tgt_vel`——PN 的提前项和记忆点漂移 | 假动作 bug：50 m/s 的幽灵目标 |
| `raids.py:439-441` | 卡住检测：指令 vs 实际 | **碰巧对的**——这是唯一一处本来就想要指令的地方 |
| `netcode.py:179` | 线上的 `vx`/`vz` | 客户端绕着它自己补救（`net.js:108-121`） |
| `modes/range/record.py:152` | 录像里的"真值" | 把指令当真相录了下来 |
| `modes/range/targets.py:100-118` | 把 `vel` 压成 0、自己写 `pos` | 唯一一类在动的车体，sweep 里 `disp = 0`（`prev == pos`）、线上速度显示"站着不动" |

调查中顺带发现的其他缺陷，都是同一条不变式缺失的症状：

* 车体的 `vel[1]` 从不写（`movement.py:172-206` 把 `pos[1]` 贴到地形上），所以地
  面上没有任何东西有竖直速度——爬坡、跳跃、下坠的残骸，每一个都得新造一套机制。
* 步兵被挡住时 `pos` 被夹住、`vel` 却仍是满速（`movement.py:172-181`）：报告的
  速度不是速度。
* `DEBRIS_COMPS` 带着 `vel`，但从没人写（无害，但让"有 vel"这个测试失去意义）。
* 客户端从快照差分出来的 `mvx/mvz`（`net.js:108-121`）是整个系统里唯一诚实的
  速度，而它算在渲染器里。

### 3.3 促成这次重构的量化证据

靶场录像，5 次攻顶俯冲打一个基本站着不动的车体
（`/tmp/mech-range-trace.jsonl`，由 `modes/range/record.py` 录制）。下表是把每
次录到的飞行用 `flight.step` 复飞、其余一切不变后的脱靶量：

| 飞行 | 车体真实速度 | 导引头拿到的 `tgt_vel` | 喂指令时的脱靶 | 喂真值时的脱靶 |
| --- | --- | --- | --- | --- |
| 0 | 0.0 m/s | `0 → ±50` | 15.6 m | **1.6 m** |
| 1 | 0.0 | `0 → ±49.9` | 11.8 | **0.7** |
| 2 | 0.3 → 2.7 | `±49` | 35.6 | **2.2** |
| 3 | 0.0 → 1.9 | `±49` | 8.1 | **2.8** |
| 4 | 0.0 → 2.4 | `±48.8 → -48.8` | 21.2 | **2.5** |

在 `pn_n`（1-8）× `pn_r`（150-800）× `pn_v`（60-200）上做网格搜索，只要 PN 看
到的是指令速度，最好也只能做到最差 17.9 m；换成真值，`pn_n = 2, pn_r = 800`
的最差值是 1.95 m。**任何超参数都投不过一个 50 m/s 的幽灵**——这就是"该修组件
而不是该调参"的论据。

## 4. 目标设计

### 4.1 组件集

```
pos      float32[3]   世界坐标，米            所有有位置的实体
vel      float32[3]   m/s，真实速度           所有会动的实体
vel_cmd  float32[3]   m/s，被要求的速度       只有车体（`UNIT_COMPS`）
```

`veh` 拆掉，**不给它起名字，改成三个自解释的标量**。今天它把"车速 + 两个计时
器"塞进一个 vec3，字段含义只能翻注释（`veh[row, 1]` 是什么？），而项目里标量组件
本来就是常态（`yaw`、`pitch`、`wreck_t`、`smoke_t`、`blast_t`、
`work_remaining_time`），`_t` 后缀就是现成的计时器约定：

| 今天的槽 | 改成 | 形状 | 含义 |
| --- | --- | --- | --- |
| `veh[0]`（注释说车速，实际写在 `drive[0]`） | `wheel_speed` | `float32` | 沿车体轴的有符号车速（倒车为负），后轴自行车模型自己积分的那个量 |
| `veh[1]` | `backout_t` | `float32` | 还剩几秒倒车（`APC_BACKOUT`） |
| `veh[2]` | `blocked_t` | `float32` | 顶着墙已经顶了几秒（到 `APC_BLOCKED` 触发倒车） |

三条理由：读代码不必再记索引；顺手修掉 `veh[0]` 是死值、车速实际住在 `drive[0]`
这个不一致；以及**共享的属性要统一、专用的状态要长得像专用状态**——将来加悬浮
车或履带车，它带自己的状态组件，而不是往一个 `veh` 里塞第 4、5 个含义。它们不是
速度、也不上线，模式或 AI 永远不碰。改造成本极低：今天全仓库只有
`movement.py:78,81,195-201` 的 6 处读写加 `components.py:26-29` 的注册，纯改名、
模拟零变化，可以不等本计划的 P1-P5，单独先落一个提交。

### 4.2 逐实体对照

| 实体 | `pos` | `vel` | `vel_cmd` | 运动学状态 |
| --- | --- | --- | --- | --- |
| 机甲 | ✓ | 斜坡后的速度，`vy` 来自坡度 | ✓（杆 / AI） | — |
| APC | ✓ | `heading · u`，`vy` 来自坡度 | ✓ | `wheel_speed`、`backout_t`、`blocked_t` |
| 步兵 / 平民 | ✓ | 它实际挪动的那一步 / dt | ✓ | — |
| 基地、掩体 | ✓ | — | ✓（0） | — |
| 子弹 / 火箭 / SACLOS / 攻顶 | ✓ | ✓ | — | — |
| 发烟罐 | ✓ | ✓ | — | — |
| 烟团、爆炸 | ✓ | — | — | — |
| 残骸、尸体 | ✓ | — | — | — |

### 4.3 合并什么、删除什么

**合并**

* 机甲：`drive`（2,）→ `vel` 的 (x,z)；轮式：`veh[0]` + `leg_yaw` → `vel`，而
  `veh` 本身拆成 `wheel_speed` / `backout_t` / `blocked_t`（见上）。一个 buffer 成
  为唯一真相；轮式模型要积分的那个标量仍然是它自己的状态，只是现在名字就写着它是
  什么。
* 一圈各自"重建真实速度"的读者（`net.js` 的 `mvx/mvz`、客户端提前量的回退
  值、sweep 的 `prev`）统一读 `vel`。
* 三个写意图的地方（`player_input`、`ai`、`raids`）加两个奇怪的
  （`combat._stop`、`range/targets` 巡逻）统一说 `vel_cmd`。

**删除**

* `drive` 组件（以及 `DEBRIS_COMPS` 里那个从没人写的 `vel`：残骸是景物，它有
  位置、没有速度）。
* `vel` 的第二种含义——`components.py:24` 那句 "commanded velocity" 随它一起
  消失。
* `modes/range/targets.py` 直接写 `pos`（`:107-118`）：巡逻车改成和所有车体一
  样写 `vel_cmd`（和 `leg_yaw`），顺手修掉它在 sweep 里 `disp = 0`、在线上"站
  着不动"的问题。
* 客户端把 `mvx/mvz` 当成真相来源的做法（它可以保留成一层平滑滤波，但不再需
  要凭空重建一个速度）。
* `tools/vehicle_trace.py` 的 `drive` 列（改成 `vel` vs `vel_cmd`）。

**明确不合并（以及为什么）**

* `proj_prev`——它是"这一腿的起点"，不是速度。将来可以升级成所有 archetype 共
  有的 `prev`，那样 `sphere_table` 可以直接读腿而不是重算；那是另一件事（§8）。
* `tgt_vel` / `tgt_mem`——导引头对**别人**状态的记忆。它会顺带变正确（§5，P2），
  但仍然是导弹的组件。
* `yaw`、`pitch`、`leg_yaw`、`turret_yaw`、`sight_yaw`、`sight_pitch`——角度，
  是另一个统一（`orientation`），另立计划。不在本次范围。
* `wheel_speed` / `backout_t` / `blocked_t`——真正的运动学状态，不是速度。

## 5. 迁移（每一阶段结束时游戏都能跑）

每个阶段是一个提交，验收命令见 §7。Fixture 规则：**不打算改变模拟**的阶段，必
须让 `tools/replay.py` 对上已签入的 fixture；打算改变模拟的阶段要说明，并有意识
地 `--save`。

**P0——把 `veh` 拆成三个标量（可以先落，独立于本计划）。** 注册
`wheel_speed` / `backout_t` / `blocked_t`，把 `movement.py` 的 6 处读写搬过去，
顺手把车速从 `drive[0]` 挪进 `wheel_speed`（今天 `veh[0]` 是死值），删掉 `veh`。
纯改名、模拟零变化，`replay.py` 必须逐位对上。它单独先落，是因为后面 P2/P3 都
要读这个量，先让名字对，再动语义。

**P1——把"意图"命名。** 注册 `vel_cmd`（`float32[3]`）并加进 `UNIT_COMPS`。
把所有写意图的地方（`player_input.py:53-57`、`ai.py:528,542,683,685,703`、
`raids.py:375,402,414,421,424`、`combat._stop`、`range/targets.py:100`）和所有
读意图的地方（`movement.py:39,46,52,72,136,141,191`、`_turn_legs`、`_vehicle`、
机甲斜坡）搬到它上面。`vel` 完全不动，所以**模拟一点变化都没有**，fixture 必须
逐位对上。这一步是纯改名，也正该先落：它先证明指令/实际的拆分是完整的，之后才
有人依赖它。

**P2——`vel` 变成真相。** `movement` 为每个车体写出实际速度：机甲 = 夹墙之后的
斜坡结果 `(drive[0], drive[1])`，轮式 = `(cos·u, sin·u)`，步兵 = 实际被夹后挪动
的位移 `/ dt`（顺手修掉被挡时虚报速度），三者都带
`vel[1] = (pos_new[1] − pos_old[1]) / dt`（贴地形的结果）。然后在**同一个提交**
里把那批想要真值的读者搬过来：`sphere_table` 的车体 `move`
（`projectiles.py:296`）、`ai._threat` 的接近率（`ai.py:235`）、AI 提前量
（`ai.py:122,184,239,319`）、`seeker.track` 的 `memv`（`seeker.py:99,117`）、
`weapons.fire` 写 `tgt_vel` 的初值（`weapons.py:282`）、靶场录像
（`record.py:152`）、线上（`netcode.py:179-180`）。巡逻 APC（`targets.py`）开始
写 `vel_cmd` 而不是 `pos`。
**这一步预期会改变模拟**：导弹的 PN 输入、AI 的提前量、雷达的接近率判定、
sweep 的 `prev` 都会变——要 `replay.py --save`，并把录到的俯冲重新复飞（§3.3）
确认脱靶量塌下来。

**P3——删掉 `drive`。** `combat._stop` 清零 `vel`；`movement._vehicle` 的车速就是
P0 已经建好的 `wheel_speed`；机甲斜坡读写 `vel[0], vel[2]`；`ai._arrive_radius` 读
`|vel|`（`ai.py:661`）；`raids._watch_stuck` 比较 `|vel_cmd|` 与 `|vel|`
（`raids.py:439-441`）——唯一同时想要两者的地方现在明确写出来。把 `drive` 从
`UNIT_COMPS`、`components.py` 和 `tools/vehicle_trace.py` 里移除。

**P4——线上与客户端。** 单位行加 `vy`：`UNIT_DTYPE` 72 → 76 字节，对齐补到 80
（`netcode.py:23-51`，客户端镜像 `frontend/src/net.js:4` 必须在同一提交里改；
其他不用动——header 传的是数量不是步长）。客户端：`mvx/mvz` 改成对线上速度做一
层平滑（保留 10 m 瞬移保护），RWR 接近率（`game.js:591,606`）和提前量
（`game.js:947`）读真值，速度表也不必再靠重建。**玩家可见**：预瞄提前框和 HUD
速度在加速、转弯时变准了——这是产品可见的变化，`doc/product/CHANGELOG.md` 要
记一条。

**P5——把不变式变成断言，而不是习惯。** 在 `tools/replay.py` 开场的自检里加
`_speed_invariant`：凡是带 `moves` / `proj_kind` / `canister` 的 archetype 都必须
带 `pos` 和 `vel`；凡带 `vel` 的必须带 `pos`；凡带 `moves` 的必须带 `vel_cmd`；
没有任何 archetype 带着 `vel` 却没人写。同时在 `World.spawn_unit` 里断言，这样
一个新实体类型忘记带这对属性时，会在它第一次出生时炸掉，而不是三周后炸在导引头
里。更新 `AGENTS.md`（ECS 那节）和 `components.py:23-25` 的文档注释。

## 6. 风险

* **archetype 抖动。** 加 `vel_cmd` 会让每个单位的 archetype key 变一次（P1）。
  便宜，但 id/row 会在一个 tick 内重排——fixture 的计数序列可能差一个；浮点序列
  不许差。
* **确定性。** 没有抽签点被移动，所以 `world.rng` 的顺序不受影响。要盯的是
  `movement` 仍然按同样的顺序遍历 archetype。
* **`aim_spheres` / `_sph_premove`**（`world.py:287-338`）之所以存在，是因为在
  `movement` 之前建的表不是 sweep 该用的那张。有了真正的 `vel`（P2）之后，表的
  *内容*会变，但陈旧契约不变：一行仍然用 `pos` 在 10 m 内来验证。
* **轮式模型的标量。** 如果碰撞夹了 `pos` 却没动 `wheel_speed`，两者会漂移；P2
  的规则是 `wheel_speed` 是积分器自己的状态、`vel` 是它输出的结果，所以夹取发生
  在两者之后（`movement.py:172-206`），下一 tick 的 `u` 照旧从 `wheel_speed` 重
  新取。
* **会瞬移的模式代码**（`debug_tp`，`main.py:306-317`）在写 `pos` 时必须把 `vel`
  清零，否则车体会带着每 tick 1.7 m 的幽灵速度落地。把它做进写 `pos` 的那个
  helper 里。

## 7. 验收

每个阶段按这个顺序来：

```bash
.venv/bin/python tools/replay.py            # P1: 必须对上；P2/P3: --save 并说明
.venv/bin/python tools/gunnery_check.py     # 视线/车体/手臂的拆分未被碰到
.venv/bin/python tools/vehicle_trace.py     # 轮式模型：圈速、偏离、弯速
.venv/bin/python tools/intercept_check.py   # 引信和雷达照样能把弹打下来
.venv/bin/python tools/top_attack_dive.py   # （待写）复飞录到的俯冲
```

`tools/top_attack_dive.py` 是 §3.3 背后的那套工具，也是修好的组件的第一个使用
者：读一份 `MECH_RECORD` 的录像，用候选的 `pn_n` / `pn_r` / `pn_v` 把每次录到的
飞行重新飞一遍，打印每次的脱靶量和哪组超参数能命中。它应该在 P2 **之前**写
好，这样"改前/改后"就是一条命令。

## 8. 推迟

* 所有运动体共享的 `prev`（本 tick 起点），用来替掉 `proj_prev` 和
  `sphere_table` 里的 `pos - vel·dt` 重建。
* `orientation`——`yaw` / `pitch` / `leg_yaw` / `turret_yaw` / `sight_*` 是六个
  标量角、四种不同含义；本文的论证同样成立，那是下一个值得做的统一。
* 烟团漂移（需要烟团带 `vel` 和一个风项）——有了这条不变式是两行功能，没有它
  就是一个新系统。
