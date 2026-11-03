# ECS 体检：哪里不像 ECS，以及哪几处真的在付代价

状态：**分析，未实施**（2026-11-02）。范围：`backend/server` 全部（`ecs.py` /
`components.py` / `world.py` / `systems/` / `modes/`）。本文只记录问题与建议，
不含改动；每一条都带 `file:line`，可以直接去读原文。

---

## 0. 一句话结论

`ecs.py` + `components.py` 这一层是**干净**的（archetype、组件集即类型、不可变、
`groups` / `slot` / `columns` / `locate` 几道数据门都给到位了），问题全部在它**之上**：

* **`World` 是杂物间**：本属于模式的状态、本属于某个系统的缓存、两个只服务一种
  模式的字段、一个名字注册表、一个事件总线、一组几何生成辅助函数，全都挂在这
  一个对象上，于是"系统只通过组件交流"这条线在这里断了。
* **`Player` 是第二套存储**：一堆**属于车体**的仿真状态（锁定、烟弹余量、瞄距、
  无敌）住在飞行员对象上，于是系统要写一个组件得先 `world.players.get(owner)`
  绕一圈回来。
* **一批实体状态住在 dict 里**：城镇（8 个字段，无实体）、突袭计划（键是
  `(faction, party, lane)`——正好是该实体自己的三个组件）、靶场靶位、导弹防御哨。
* **几个 system 太胖**：`ai.py`（706 行，九个职责）、`weapons.py` + `projectiles.py`
  （开火控制 + 弹道 + 碰撞 + 伤害 + 生成实体）、`mortality.py`（死亡 + 经济 +
  比分 + 胜负 + 尸体工厂）、`waves.py`（争夺 + 补给 + 前线 + 出生点）、
  `raids.py`（880 行，规划 + 寻路 + 驾驶 + 任务状态机）。
* **`etype` 分支冒充标签**：`movement` 用 `etype == ETYPE_MECH` 选转速，`gunnery`
  用它决定炮塔是否解耦，`ai` 用它四次选瞄准架，`weapons` 用它选枪口高度——
  而项目自己的规矩是"身份是组件，不是类型分支"，`combat.py` 已经给出了正解。
* **顺序契约只活在一行 list 里**：`world.py:107-119`。六条跨系统的隐含通道
  （`proj_prev`、`_sph_premove`、`ecs.destroyed`、`world.tick & 1`、爆炸晚一拍、
  `blasts` 的 stale 集）没有任何类型或断言保护。

按"现在要不要修"分四档（P0-P3），见 §10。最该动的三件事是：**把 `Player` 里属于车体的
那几项挪成组件**、**把城镇和突袭计划变成实体**、**把 `World` 上只服务一种模式的
字段收进 `Mode`**。这三件不是为了好看——它们各自都已经造成过一次"另一处代码不
知道这件事"的 bug 或者一次绕路。

---

## 1. 本文的判据

不是"教科书 ECS 怎么说"，而是这个仓库自己写下的规矩（`AGENTS.md` 与
`components.py` / `ecs.py` 的注释）。一条实现违背了其中任意一条，就算一条：

| # | 规矩 | 出处 |
| --- | --- | --- |
| 1 | 实体状态只有一个存储：组件。不是 dict、不是旁挂对象 | `components.py:1-8` |
| 2 | 一个系统一件事；输入是查询，输出是组件写 | `world.py:103-119` 的意图 |
| 3 | 身份靠标记组件，不靠 `etype` 分支 | `components.py:60-77` |
| 4 | 系统之间只通过组件交流，不通过共享可变全局 | `ecs.py:1-20` |
| 5 | 实体只在工厂里创建（组件集决定类型，且只决定一次） | `ecs.py:create` |
| 6 | 类型数据在 spawn 时拷进组件，热循环不读 `defs` dict | `components.py:99-101` 的 `armor` 先例 |
| 7 | 流水线顺序是契约，契约必须看得见 | `world.py:112-118` |

第 6 条和第 7 条是"该做但还没做完"，不是"违反设计"——文档里会把两者分开标注。

**有意偏离、不算问题的地方**（写在这里免得以后有人"顺手修掉"）：

* 没有 `add_component` / `remove_component`，状态转移 = 换实体（`ecs.py:11-16`）。
* `combat.py` / `shop.py` / `ballistics.py` 是库不是系统，伤害没有队列
  （`AGENTS.md` 的 "Damage is not a system"）。这是对的：伤害不拥有状态。
* 命中球表一 tick 一份、跨系统共享（`world.sphere_table`）。共享缓存本身没错，
  错的是它挂在 `World` 上且用距离启发式验新鲜度（§7.2）。

---

## 2. `World` 是杂物间

`world.py:62-101` 的 `__init__` 一屏里混了七种东西。逐个点名，按"这属于谁"归类：

| 位置 | 是什么 | 应该属于 | 代价 |
| --- | --- | --- | --- |
| `world.py:74` `wave` | 波次计数 | **battle 的 `Mode`** | `netcode.py:261` 每帧给靶场也发一个恒为 0 的 `wave` |
| `world.py:75` `wave_timer` | 波次时钟，初值 `8.0` | **battle 的 `Mode`** | `modes/battle/__init__.py:28` 又写了一遍 `8.0`，两份默认值 |
| `world.py:76` `winner` | 胜负方 | **`Mode`（规则）** | 只被 `mortality.py:82` 写、被三个地方读 |
| `world.py:77` `end_timer` | 赛后读秒（`40.0` 硬编码在 `mortality.py:83`） | **`Mode`** | 见 §9 表 |
| `world.py:78` `events` | 每 tick 事件队列，`>400` 就 `del [:100]` | 事件总线，独立对象 | 会被静默丢弃；`modes/range/record.py:128` 靠过滤整表来读 |
| `world.py:83-85` `names` / `_name_ids` | 名字驻留表（`name_id` 组件的背面） | `name_id` 的合法归宿，但应有自己的类 | 可接受；只是它让"实体叫什么"存了两处 |
| `world.py:86-89` `_sph` / `_sph_tick` / `_aim_ok` / `_sph_premove` | 命中球表的缓存与失效标志 | **`projectiles` 系统**（它造表） | 见 §7.2，这是全文最脆的一处 |
| `world.py:94` `base_entity` | 每方指挥基地的实体 id | 一个 `base` 组件 / 查询 | 靶场要手动塞 `{}`（`modes/range/__init__.py:45`）才不让 `main.py:326` 崩 |
| `world.py:95-96` `targets` / `interceptor` | **靶场专用**，类型是 `object` | `Mode` 实例（自有字段） | 共享类里写着两个具体模式的东西；`getattr` 鸭子打字 |
| `world.py:97` `mode_mod.setup(self)` 在 `_build_pipeline()` 之前 | 顺序约束 | — | 任何新模式都不能在 `setup` 里用 `world.systems`，无文档 |
| `world.py:296-343` `sphere_table` / `aim_spheres` | 空间索引服务 + 新鲜度启发式 | `projectiles` | 见 §7.2 |
| `world.py:360-373` `emit` | 事件总线 + numpy 标量规范化 | 独立对象 | 细节逻辑（`np.generic` 转换）埋在 `World` 里 |
| `world.py:375-411` `_clear_spot` / `base_spawn` | 出生点几何（含 `55.0` 基地间距） | 生成器 / `Mode` | 四种模式共用一个 battle 形状的常数 |
| `world.py:414-428` `lane_index_near` | 车道索引换算 | `Terrain`（车道的所有者） | 靶场没有车道，还是要走这个函数 |
| `world.py:430-451` `update` | tick 驱动 **+ 比赛结束与重开** | 前半属世界，后半属 `Mode` | `winner`/`end_timer`/`reset_match` 是**规则**，不是世界机制 |
| `world.py:124-200` `spawn_unit` | 77 行的实体工厂 | ✅ 这里是对的 | 唯一问题：它同时决定 battle 语义（AI 呼号池、`lane`、`path_i`、`ai_state`） |
| `world.py:230-255` `add_player` / `remove_player` | 大厅逻辑（均衡两边） | 网络层 / 大厅 | `min(factions, key=…)` 硬编码在 `World` |

模式状态还**动态挂**在 `World` 上，`World` 从未声明过：

```
modes/battle/raids.py:157-161   world.raid_timer / raid_paths / raid_retry / raid_serial
modes/range/__init__.py:47      world.bunkers        ← 写了，没有任何东西读
modes/range/__init__.py:54      world.record         ← 靠 getattr 探测，reset_match 时从不清
```

`world.bunkers` 是纯写入的死状态；`world.record` 在 `reset_match`
（`world.py:453`）里既没关文件也没换句柄，一轮重开后录制器指向旧世界。

**建议**

1. `Mode` 接口加一个 `state` 命名空间（或让每个 `Mode` 持有自己的 dataclass），
   `wave` / `wave_timer` / `winner` / `end_timer` / `raid_*` / `targets` /
   `interceptor` / `record` 全搬进去；共享代码问 `mode.state.winner`，而不是
   `world.winner`。
2. 比赛结束与重开这两条规则从 `World.update` 搬进 `Mode`：`World` 只负责调
   `systems`，`Mode.tick_rules(world, dt)` 负责"比赛结束了吗、重开吧"。
3. 命中球表搬进 `projectiles`（见 §7.2），`World` 只留一个 `Projectiles` 组件的
   门面。
4. `events` 与 `names` 各自成类（`Events` / `NameTable`），`World` 只持有实例。

---

## 3. `Player` 是第二套存储

`Player` 是纯 Python 对象（`world.py:21-59`），通过 `owner` 组件挂在车体上。
它该有的东西只有"连接级"的：`pid` / `name` / `faction` / `last_seq` /
`in_*` / `zoom` / `aspect` / 经济（`money` / `kills` / `deaths` / `score`）。

问题在于下面这些**属于车体**的仿真状态也住在它身上：

| 字段 | 位置 | 真相 | 绕路证据 |
| --- | --- | --- | --- |
| `lock_target` / `lock_progress` | `world.py:36-37` | 是**这辆车的导引头**的状态，车死了就该消失 | `weapons.py:169-174`：`eid` → `owner` → `world.players` → 读字段，**实体→玩家→实体**绕一圈回来 |
| `smoke_charges` / `smoke_cd` | `world.py:45-46` | 车体消耗品与车体冷却 | `world.py:203-205` 在 `spawn_mech` 里手工作组件初始化；`netcode.py:257-260` 又单独镜像到线上 |
| `zero_range` | `world.py:42` | **炮的**表尺设定，不是人的 | `weapons.py:253-255`：`pl = world.players.get(int(e["owner"][eid]))` 只为拿一个数 |
| `god` | `world.py:47` | 调试无敌，等于 `invulnerable` 的玩家版 | `combat.py:57-58` 在伤害路径里 `world.players.get(owner)`；两行之上的 `combat.py:50` 已经在用组件写法 `e.has(eid, "invulnerable")` |
| `loadout` | `world.py:38` | **与 `weapon` / `mag` / `ammo` 组件重复的事实** | `shop.py:199-205` 必须同时写两处；`world.py:211-213` 重生时重置 Player 那份 |
| `respawn` / `death_pos` | `world.py:29-30` | 死亡→出生计划，被 `mortality.py:206-209` 写、`economy.py:20` 减、`reset_match` 覆盖 | 同一个字段被**两个系统做相反算术** |
| `work` / `work_total` | `world.py:52-53` | 改装的**订单内容**（`work_remaining_time` 组件的另一半） | `rearm.py:32-35` 必须自愈："有计时器没订单"→ 手写归零 |
| `mech` | `world.py:27` | 玩家→实体的反向指针 | 合理，但它必须手动在 5 个地方维护 |

同一组字段被**五个互不相干的地方**重置，没有单一所有者：构造器（`world.py:22`）、
`spawn_mech`（`world.py:202-207`）、`reset_match`（`world.py:465-471`）、
`shop.py:215-216,237,239-240`、`smoke.py:41-53`。

只有 `systems/locking.py` 是"整个系统的输出都写在 Player 上"（`:104-133` 写
`lock_target` / `lock_progress`），它一份组件都不写——这正是一个系统长错了存储
的样子。

**建议**

1. 加 `lock` 组件（`lock_tgt` / `lock_p`），`locking` 写它、`weapons` 读它；
   Player 上删掉两项。
2. `smoke_charges` / `smoke_cd` → `smoke_charges` / `smoke_cd` 组件（车体消耗品，
   跟着车死）。
3. `zero_range` → 组件（`sight` 的一部分，跟着炮走）。
4. `god` → 直接给玩家车体一个 `invulnerable` 标签，`combat.apply` 里删掉
   `world.players` 那一次查找。
5. `loadout` 删掉，只留组件；`shop` 从 `weapon` 组件读当前配置。
6. `work` 的**内容**变成组件（或一个 `order` 组件），`work_remaining_time`
   只是它的倒计时——`rearm.py:32-35` 那段自愈就会自然消失。

---

## 4. 实体状态住在 dict 里

### 4.1 城镇（`terrain.towns`，8 个字段，无实体）

schema 由两个模块分别造出来：`terrain.py:1544-1549` 造 `owner` / `progress` /
`name`，`modes/battle/waves.py:37-40` 再加 `contested` / `lane` / `lane_i` /
`inf_t` / `apc_t`。`_capture`（`waves.py:64-99`）每 tick 就地改这些字段。

这是**最典型的一条**：城镇有位置、有归属、有进度、有两条补给时钟、能被争夺
——它是实体，却住在 Python list 里。直接后果：
`component.raid_goal`（`components.py:154`）不得不存一个"到 `world.towns` 的
下标"，`255` 表示"敌方基地"（`raids.py:133`）；城镇顺序一变，所有突袭者的目标
就指错。而"这座城是谁的"这件事，`netcode.py:269` 还得整张表序列化给客户端。

**建议**：`town` 实体（`pos` / `town_owner` / `town_progress` / `lane` /
`lane_i` / `town_inf_t` / `town_apc_t`），`contested` 不存——它是"`CAPTURE_RADIUS`
内有没有敌对的 `combat`"，每 tick 一次空间查询的结果，本来就不该是状态
（`waves.py:68` 的注释已经承认"recomputed, never stale"）。

### 4.2 突袭计划（`world.raid_paths`）

```python
modes/battle/raids.py:159   world.raid_paths = {}     # (faction, party, lane) -> [(x, z), ...]
modes/battle/raids.py:160   world.raid_retry = {}
```

键 `(faction, party, lane)` **正好是该突袭者自己的三个组件**
（`components.py:153-156`）——这是一个**以组件元组为键的、写错形状的 eid 索引**。
后果：同 faction、同 party、同 lane 的两台车会**静默共用一条路线**；
`raids.py:238-241` 还得每 tick 手工回收死掉的键（`_prune`）。

另一半的计划（`raid_goal` / `raid_i` / `raid_stuck` / `raid_t`）已经老老实实是
组件了，见 `raids.py:137` 的 `_COMPS`——同一个功能，一半在实体上、一半在
`World` 上。

**建议**：`raid_path` 组件（指向世界持有的路径池的句柄，或直接把折线压在实体上），
`raid_retry` 组件。两条都能随实体一起死，`_prune` 整个函数消失。

### 4.3 靶场的靶位与防御哨

`modes/range/targets.py:39` 的 `Targets` 持有 dict 列表
（`role/x/z/lo/hi/dir/eid/t`）：`eid` 是实体自己的 id 存在实体**旁边**，
`t` 是重生倒计时，`dir` 是巡逻转向状态。`targets.py:60-67` 的注释在解释
id 复用怎么判——`modes/range/interceptor.py:38-40,60-61` 同样的问题再解一遍
（`self.eid` + 手写存活判定）。三个字段全是实体状态。

### 4.4 名字表

`name_id`（`components.py:77`）是 `World.names` 的下标（`world.py:83-85`）。
这是"把字符串驻留掉"的合理做法，但它意味着实体的身份数据有一部分不在实体上；
`name_of`（`world.py:354`）是唯一的读取口，尚可接受。**不建议改**，只建议把它
收成一个 `NameTable` 类而不是 `World` 上的两行。

---

## 5. 系统太胖

| 文件 | 行数 | 职责 | 拆法 |
| --- | --- | --- | --- |
| `systems/ai.py` | 706 | **9** | 见 §5.1 |
| `modes/battle/raids.py` | 880 | **9** | 规划 / 寻路 / 避障 / 任务状态机 四块 |
| `systems/projectiles.py` | 432 | **6** | 积分 / 碰撞 / 伤害结算 / 命中球表 / 生成爆炸 |
| `systems/weapons.py` | 302 | **6** | 冷却 / 弹匣 / 锁定解析 / 炮口几何 / 表尺 / 生成弹 |
| `modes/battle/waves.py` | 214 | **7** | 争夺 / 供应链 / 出生点 / 补充 |
| `systems/mortality.py` | 228 | **6** | 死亡 / 经济 / 比分 / 胜负 / 尸骸工厂 |
| `systems/movement.py` | 227 | **5** | 腿部转向 / 轮式模型 / 机甲模型 / 碰撞 / 坡度 |
| `systems/smoke.py` | 197 | **6** | 输入 / 冷却 / 生成 / 飞行 / 相变 / 遮挡查询 |
| `systems/locking.py` | 133 | **5** | 资格 / 保持 / 获取 / 视线 / 进度 |
| `systems/player_input.py` | 64 | **5** | 类型门 / 视线 / 后装门 / 指令 / 扳机组包 |
| `systems/economy.py` | 23 | **2** | 收入 + 重生（后者是**模式规则**） |

### 5.1 `ai.py` —— 一个 `run` 九件事

`ai.run`（`ai.py:45-160`）一个函数体里依次做：阵营索引构建（`:65-72`）、
空中目标表（`:74`）、眼位选择（`:94-99`）、目标获取与校验（`:100-118`）、
导弹防御派发（`:114-116`）、地面交战与压制（`:119-141`）、行驶派发
（`:143-155`）、扳机清零（`:140-145`）、轮式刹车（`:156-160`）。

其余同构问题：

* `intercept`（`:191-214`）是 `run` 的第二份拷贝（同样的乘员过滤、同样的扳机
  清零），因为"没有 `run` 的模式也得有人清扳机"。
* **眼位策略写了两遍**（`:94-99` 与 `:227-233`），**瞄准架三选一写了两遍**
  （`:248-264` 与 `:441-458`）——新增一种车要改四处。
* 死参数：`_advance(world, eid, …)` 从不用 `eid`（`:494`）、`_suppress` 收了 `d`
  不用（`:413`）、`_engage` 收了 `tid` 不用（`:386`）。

**拆法的建议**（不改行为，只切边界）：

1. `ai_acquire`：目标获取（`:348-384`）+ 视线（`:341-347`）。
2. `ai_drive`：`_advance` / `_advance_route` / `_route_speed` / `_arrive_radius` /
   `_passed` / `_corner_speed`（`:494-687`）——这一坨本身已经是一套干净的
   循迹库，只是寄居在 `ai.py`。
3. `ai_gunnery`：`_aim_and_fire`（`:422-491`）——它写的是 `yaw` / `pitch` /
   `turret_yaw`，即 `gunnery` 的组件（见 §8.1）。
4. `ai_missile_defence`：`_missiles` / `_threat` / `_missile_step` / `_fused_arm`
   （`:163-290`）——独立的空情处理，`intercept` 复用同一份。
5. `ai_civ`：`_civ`（`:690-706`）。

### 5.2 `weapons.py` + `projectiles.py`

`weapons.run` 六件事：冷却与弹匣补给（`:79-105`，批量 numpy 一遍）、
开火控制与射速政策（`:124-195`）、锁定解析（`:167-183`）、炮口几何
（`:198-243`，瞄线 vs 炮膛、机架仰角、`defs.rack_tube`）、弹道表尺
（`:245-258`）、**生成弹实体**（`:269-301`，14 个字段手写）。

`projectiles.run` 六件事：制导数学库（`:21-43`）、积分与四种制导律
（`:83-176`）、批量地形查询（`:178-184`）、碰撞（`:190-254`）、
命中结算 + 伤害 + 生成爆炸（`:403-432`）、以及**整张命中球表**
（`:257-355`，含 CSR 块索引）。

两条最刺眼的：

* **没有弹的工厂**。`world.spawn_unit` 是车体的工厂，弹没有对应的
  `spawn_round`——`weapons.py:270,284` 现场 `e.create(PROJ_COMPS / MISSILE_COMPS)`
  + 十四个字段，`proximity.py:119-140`、`blasts.py:34-64`、
  `mortality.py:212-227` 各一份同样的"就地造实体"。工厂是规矩 #5，四处破例。
* **数据表在系统里又抄了一份**：`weapons.py:19` 的 `KIND_ID`（`defs.WEAPONS`
  里 `kind` 本来是字符串）、`weapons.py:23` 的 `MAG_OF`、`projectiles.py:21` 的
  `HIT_R`、`ai.py:24-26` 的 `RCS/HIT_R/PROX`——同一张 `defs.WEAPONS` 的五个
  平行索引，加一种武器要记得改五处（还是五处不同的文件）。

### 5.3 `mortality.py`

`_kill`（`:59-91`）一个函数里：发特效（`:70-73`）、写击杀播报（`:77`）、
发钱发分（`:78`）、规划重生（`:79`）、**判胜负**（`:81-84`）、
留残骸（`:86`）、销毁（`:87`）、推迟一 tick 的爆炸（`:91`）。
其中**胜负**是模式规则、**钱和分**是经济、**播报文本**是 HUD 文案，
三者都不该在"死亡法官"里。`_feed`（`:163-194`）甚至负责拼
`killer killed victim with weapon` 的段落结构——服务端的文本装配。

死亡判据本身（`hp <= 0` + `defs.UNIT_DEFS[...]["death"]` 配方）写得很好、
完全数据驱动（`:63-68`），**不要动那一半**。

### 5.4 `waves.py` / `raids.py`

`waves.py` 七个职责已列在 §5 表里，其中两条另有归属问题：
`respawn_town`（`:132`）是**出生点规则**，被 `modes/battle/__init__.py:67` 调用；
`clear_near`（`:156`）是**出生点搜索**，`raids.py:244` 的 `_spawn_near` 是它的
第二份实现。

`raids.py` 的九个职责里，四个是通用能力（A* 寻路 `_astar:781`、128 m 栅格
`Nav:524`、16 m 街格 `StreetNav:564`、避障 `_steer:473`），却私藏在 battle 的
一个文件里，别处要用只能再抄一份。

### 5.5 `movement.py` / `smoke.py` / `locking.py`

* `movement.py` 一个文件跑**三套运动学**：轮式（`_vehicle:55`）、机甲斜坡
  （`run` 内 `:160-184`）、步兵"腿就是机器"（`:185-187`），外加腿部转向
  `_turn_legs:26`、碰撞与坡度（`:188-227`）。三套模型里只有轮式那套有名字
  （`_vehicle`），另两套是 `run` 里的分支。
* `smoke.py:162` 的 `blocks()` 是**一个纯几何查询**（椭球 vs 线段）住在系统模块
  里，`systems/sight.py:38` 依赖它——调用链变成"视线系统 import 烟系统"。
* `locking.py:48` 的 `cone_of` 从 `pl.zoom` / `pl.aspect` 推锁定锥——**客户端的
  视口缩放改变了仿真的可锁定性**。

---

## 6. 类型开关冒充标签

规矩 #3。`combat.py` 给出了反例（`:50,54,149`：`has("invulnerable")`、
`has("arm_hp")`），下面这些地方没有跟上：

| 位置 | 分支 | 该用什么 |
| --- | --- | --- |
| `movement.py:50` | `rate = np.where(etype == ETYPE_MECH, 2.6, 6.0)` | defs 字段（同 `speed`/`radius` 那样在 spawn 时读） |
| `movement.py:160` | `elif et == ETYPE_MECH:` 选斜坡模型 | 标签：`ramp` / 即时（`vehicle` 已是轮式的标签） |
| `gunnery.py:56` | `mech = etype == ETYPE_MECH` 决定炮塔是否解耦 | 同一个事实 `combat.py:149` 用 `has("arm_hp")` 表达过 |
| `player_input.py:36` | `if etype != ETYPE_MECH: continue` | `arms` + `owner`："这辆车是飞行员能开的" |
| `weapons.py:38,42,48` | 眼位三选一（机甲/APC/其它） | spawn 时拷一个 `eye_z` 组件 |
| `weapons.py:63` | `if etype == ETYPE_APC: return turret_yaw, pitch` | `turret` 标签 |
| `weapons.py:226` | `if etype == ETYPE_MECH:` 才有枪口/机架 | `rack` 组件（已经是数据了） |
| `ai.py:94` / `:230` | 眼位（**两份**） | 同上 `eye_z` |
| `ai.py:248-264` / `:441-458` / `:460` / `:465` | 瞄准架三选一（**四份**） | `sight_head` / `turret` 标签 |
| `ai.py:485` | `if etype == ETYPE_APC and dist < 3500: t \|= 2` | 与同函数 `:476-486` 的 reach 逻辑自相矛盾 |
| `ai.py:220-223` | 有没有雷达 → `defs["radar_strength"]` 查表 | `radar` 组件（`armor` 已经是这个做法） |
| `ai.py:335` | `mis["kind"] == 3` 判攻顶 | 具名常量或弹上的 `guided` 标签 |
| `projectiles.py:106-168` | `k == 0/1/2/3` 四种制导律 | 每种弹一个 stepper（`flight.py` 只服务 `top` 一种） |
| `projectiles.py:209` | `pr = 0.35 if k == 0 else 0.6` | `wd["hit_r"]`（就在同一文件 `:21` 抄了一份） |
| `projectiles.py:115` | `etype[shooter] not in (ETYPE_BASE,)` | 组件测试（基地没有 `sight_yaw`） |
| `weapons.py:252` | `if kind in (0, 1)` 才走表尺 | `wd.get("zero")` 数据位 |
| `weapons.py:168,187,295` | `owner[eid] < 0` 判"这是 AI"（三次） | AI/玩家的区别应当由**谁写了意图**表达，不是 owner 符号 |

一句话：`etype` 在 defs 里是**数据**（几何、配方、音效），在系统里出现分支就是
把数据当身份用了。上表里 80 % 的替换都是"spawn 时拷一个数进组件"，而这正是
`armor`（`components.py:99-101`）已经做过的事。

---

## 7. 顺序耦合与隐藏通道

流水线是 `world.py:107-119` 那一行 list。问题不是它有顺序——必然有——而是**六条
真正的数据通道不写在契约里**：

### 7.1 清单

| 通道 | 谁写 → 谁读 | 不成立时 |
| --- | --- | --- |
| `proj_prev` | `projectiles.py:101` → `proximity.py:78` | 引信"本 tick 飞过的那一段"退化成长度 0 → 永远不会起爆（`proximity.py:89-90` 静默 `continue`） |
| `world.tick & 1` | `ai.py:489` 当作半自动的上升沿 | 与 `weapons.py` 的 `trigger_prev` **共同拥有**同一个边沿，AI 射速变成另一个系统流水线位置的函数 |
| `ecs.destroyed` | `world.py:436` 清、`ecs.destroy` 写、`blasts.py:119` 读 | 爆炸结算拒绝"表建好之后才死的 id"——一个全局临时集合当跨系统通道 |
| `_sph_premove` | `world.py:315-343` | 见 §7.2 |
| 爆炸晚一拍 | `mortality.py:91` 生成 → `blasts.run` 下一 tick 结算 | 故意设计（防止链式反应炸到已成残骸的壳），但"谁在谁之前"只写在 `world.py:118` |
| `blasts.stale` / `combat.apply` 的存活再判定 | `combat.py:59-66`、`blasts.py:119` | 两个地方各自防同一件事：命中球表比死亡旧 |

还有三条**读旧值但代码不说**的：

* `ai` 在 `movement` 之前跑，所以它读的 `vel`（`ai.py:235,661`）是**上一 tick
  的**，读的 `pitch`（`ai.py:253,465`）是 `gunnery` **还没转到的**。
* `_missiles`（`ai.py:163-190`）在 `projectiles` 移动弹之前、在 `seeker` 刷新
  记忆之前建表，然后当作"当前空情"传给三个函数。
* `world.aim_spheres()` 让**调用方**（`weapons`，经 `ballistics.zero_pitch`）
  负责知道"这张表是移动前的、精度打折"——类型上没有任何区分。

### 7.2 最脆的一处：命中球表的新鲜度

```python
world.py:296   def sphere_table(self, dt=1/30)   # 一 tick 一张，缓存在 World 上
world.py:315   def aim_spheres(self)             # 移动前的表 + "这行还准吗"掩码
world.py:333   ok &= np.abs(e["pos"][ids] - self._sph[0]).max(axis=1) < 10.0
```

"这行的 id 还是那辆站得住的车吗"用**十米距离启发式**回答（`world.py:333`）。
一张表有两种语义（扫掠用 / 瞄准用），靠一个布尔标志 `_sph_premove` 和调用顺序
区分；`reset_match` 手工清四个字段（`world.py:456-458`），`update` 又故意不清
（`world.py:439-441`）。

**建议**：表归 `projectiles` 所有，并返回两个**类型不同**的对象
（`SweepTable` / `AimTable`），瞄准表自带"本 tick 已用 `movement` 之前的位置"
这个事实；距离启发式换成 tick 号 + 位置版本号（车体每 tick 被 `movement` 写一次
`pos`，版本号本来就有：`world.tick`）。

### 7.3 建议的做法

不改流水线，只让它**自证**：

1. 每个 `run` 声明依赖：`NEEDS = ("pos", "vel")` / `AFTER = ("movement",)`，
   `_build_pipeline` 拓扑排序并在启动时断言顺序（现在是手工维护的 list）。
2. 那六条通道各换成组件或显式返回值：`proj_prev` 已经是组件（好），
   `ecs.destroyed` 改成 `blasts` 查询 `hp <= 0`（本来就是唯一判据），
   `world.tick & 1` 换成 `trigger_prev` 一个所有者。
3. `tools/replay.py` 已经在守"顺序不能变"（见它的 `_speed_invariant`），可以再
   加一条：把 `world.systems` 倒过来跑会炸——现在不会。

---

## 8. 多写者

规矩 #2 的另一面：一个组件该有一个所有者。现在至少有六个缓冲区是多方共写的：

### 8.1 姿态与运动

| 组件 | 声明的所有者 | 其它写者 |
| --- | --- | --- |
| `vel` | `movement`（`movement.py:117` 注释明说） | **`combat._stop`**（`combat.py:101-111`）——伤害库里把自己写成了运动学第二作者 |
| `vel_cmd` | 控制器（AI、玩家） | `combat._stop` 再写一遍；`ai.py:156-160` 在**乘员分支**里写轮式刹车 |
| `yaw` / `pitch` | `gunnery` | `ai._aim_and_fire`（`ai.py:454,457,458`）、`ai._missile_step`（`:255,257,262`）、`ai._civ`（`:706`，直接把朝向瞬移到游荡方向，绕过 slew） |
| `turret_yaw` | `gunnery` 镜像 | `ai.py:255,453` |
| `path_i` | 循迹 | `ai._advance`（`:520`）、`ai._advance_route`（`:667`）、`raids.py` 也写——路线进度没有主人 |
| `trigger` | `player_input` | `ai.run`（`:145`，在**行驶**分支里清零）、`ai.intercept`（`:211`）、`ai._missile_step`（`:272,274`）、`ai._aim_and_fire`（`:491`）——一个系统内三个写者 |

### 8.2 身份、信用与钱

| 事实 | 存了几份 |
| --- | --- |
| "这一发是谁打的" | 三份：`proj_pid/proj_name/proj_etype`（弹上）、`last_hit/last_name/last_fac/last_etype/last_wid`（车体上）、`blast_pid/blast_name/blast_etype/blast_wid`（爆炸上）；`blasts.py:103-110` 还要把 `mortality` 的规则再推一遍 |
| `money` | 三个写者：`mortality.py:144-146`（赏金、击杀数、分数）、`economy.py:17`（收入）、`shop.deal`（花费） |
| `hp` | 唯一的**写入者**是 `combat.apply`（好），但"什么时候、多大半径"由三个系统各判一遍：`projectiles.py:426`、`proximity.py:127`、`blasts.py:125`——`blasts.py:3-4` 声称自己是区域内伤害的唯一实现，`proximity.py:127` 又实现了一次 |
| `target` | 一个组件名两种含义：车体的"我在打谁"（`ai.py`）、弹的"我锁着谁"（`weapons.py:301`、`seeker.py`），靠 archetype 区分；`weapons.py:301` 让开火系统也写它 |

`combat._stop` 那条特别值得指出，因为它**同时违反两条规矩**：`hp` 归它管、
`vel` 不归它管；而它那么写的理由（"`movement` 和 `ai` 已经跑过了，死车还会再
开一 tick"）恰恰是 §7 顺序问题的一个症状，不是伤害库该背的锅。

---

## 9. 全局 RNG 把系统焊死

`world.rng`（`world.py:69`）是全场的单一随机流，22 处调用点。只要有一个循环
**按实体**抽数，那次抽到的序列就依赖遍历顺序：

```
ai.py:106    timer[row] = ACQUIRE * rng.uniform(0.75, 1.35)      # 每车一次
ai.py:697    timer[row] = rng.uniform(1.5, 4.5)                 # 平民每次换向
ai.py:703    a = rng.random() * math.tau
weapons.py:262-265  散布、抖动
smoke.py:74-76      罐头散布
world.py:146 spawn_unit 的初始朝向；:178 AI 呼号；:184 AI 挂载
raids.py:200-201    rng.sample 选车道
```

后果是 AGENTS.md 里写下的那一条："iteration order is part of the simulation"。
`ai.py:78-81` 有一条注释在解释这件事——**一条注释在替一条不变式站岗**。
`_acquire` 里 `np.argsort(ds)[:6]`（`ai.py:376`）让"六个等距候选里先看哪个"
也变成 id 顺序的函数。

**建议**（按代价从低到高）：

1. 每实体一个 `seed` 组件（`proj_seed` 已经是这个做法，`weapons.py:299` 抽一次
   存下来），所有"这辆车的抖动"从它派生（hash(eid, tick)），而不是从共享流里抽。
2. 只有世界级的决定（出生点、车道选择、AI 挂载模板）才用 `world.rng`。
3. 那样 `groups()`（archetype 主序）和 `query()`（升序）就可以互换，
   `ai.py` 里为保序而放弃批量化的地方也就松绑了。

---

## 10. 改造路线

按"现在是不是已经在付代价"排序，不是按洁癖排序。

### P0 —— 已经在咬人，且每次加功能都要再咬一次

1. **`Player` 里属于车体的四项**（`lock_*` / `smoke_cd,charges` / `zero_range` /
   `god`）→ 组件。收益立刻可见：删掉三处 `world.players.get(owner)` 绕路
   （`weapons.py:169,253`、`combat.py:57`），`locking.py` 变成正常系统。
2. **城镇 → 实体**。收益：`raid_goal` 不再存外部下标，`waves._capture` 变成一次
   查询 + 一次写，`netcode` 少一张手写表。
3. **`World` 上的模式状态搬进 `Mode`**（`wave` / `wave_timer` / `winner` /
   `end_timer` / `raid_*` / `targets` / `interceptor` / `record` / 死字段
   `bunkers`）。顺带修两个真 bug：`reset_match` 不关录制器、`end_timer = 40.0`
   硬编码在共享系统里。
4. **命中球表归 `projectiles`**，`AimTable` / `SweepTable` 两个类型，
   删掉十米启发式（§7.2）。

### P1 —— 结构性的，越早越便宜

5. **弹的工厂 `spawn_round` / 尸骸工厂 / 爆炸工厂**，四处 `e.create` 归一。
   顺带把 `KIND_ID` / `MAG_OF` / `HIT_R` / `RCS` / `PROX` 五张平行表收成一张。
6. **`ai.py` 五刀**（§5.1），先把 `ai_drive`（循迹）与 `ai_missile_defence`
   切出去——这两块本身已经自成体系。
7. **`raids.raid_paths` → `raid_path` 组件**，`_prune` 消失。
8. **`mortality` 里的经济 / 播报 / 胜负各自搬家**（§5.3）。

### P2 —— 该做，但不做也不会出错

9. `etype` 分支换成标签 / spawn 时拷数（§6 表）。
10. `movement` 三套运动学拆成三个 `_model_*`，`smoke.blocks` 搬进
    `systems/sight.py`（或独立的几何库）。
11. `waves.py` / `targets.py` / `interceptor.py` 的职责切分。
12. 每实体 RNG 种子（§9）。

### P3 —— 只是文档债

13. `_build_pipeline` 的依赖声明与拓扑断言（§7.3）。
14. `Player` 的五个重置点归一到一个 `Player.reset_for(hull)`。

**做任何一条的验证手段**：`tools/replay.py`（顺序与聚合量的护栏，改动
`ecs.py`/`systems/`/`terrain.py` 必跑）、`tools/gunnery_check.py`（姿态与炮口）、
`tools/intercept_check.py`（引信与拦截）、`tools/profile_battle.py`
（P0/P1 的改动很可能影响每 tick 成本，A/B 见 AGENTS.md）。P0-1 到 P0-3 都是
**纯搬存储**，实现正确的标志就是 `replay.py` 的数值一条不变。

---

## 11. 已经做对的，不要顺手改

这份文档批评很多，所以把反例也写下来，免得以后有人"统一风格"把它们改坏：

* **组件集不可变、没有 add/remove**（`ecs.py:11-16`）。烟罐→烟团是换实体
  （`smoke.py:119`），弹/导弹是不同 archetype（`components.py:277`）。正确。
* **身份标签体系**（`components.py:60-77`）与 `combat.py:50,54,149` 的用法：
  没有一处 `etype` 分支。这是全仓库的正面样板。
* **伤害不是系统**（`combat.py`），`apply` 是 `hp` 的唯一写入者，
  `mortality` 是唯一法官，`blasts` 是爆炸的唯一实现，`defs.pen_damage` /
  `blast_damage` 是唯一公式。
* **死亡配方数据驱动**（`mortality.py:65-68` 的 `death` / `blast` / `wreck`），
  一个 `etype` 分支都不需要。
* **`ecs.groups` / `slot` / `columns` / `locate` 四道门**把"按缓冲写"和
  "保序"两种需求分得很清楚，AGENTS.md 里的性能结论都有实测支撑。
* **`defs.rack` 一处描述、服务器与客户端同读**（`weapons.py:198-243` ↔
  `scene.js`）——数据只有一份，这个模式值得往上推（弹的工厂、武器表索引）。
* **一实体一位置一速度**（`components.py:34-36`、`movement` / `projectiles` /
  `smoke` 各一个 `vel` 写者）——唯一破例是 `combat._stop`，而它的理由是
  §7 的顺序问题，修顺序而不是修它。
