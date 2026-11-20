# ECS：存储、三扇门与不变式

> **读者**：维护者（写后端系统的人）
> **一句话**：archetype ECS 怎么存、怎么读写才不慢，以及四条不许破的不变式（组件集定死、
> 身份是组件、一个 `pos` 一个 `vel`、访问顺序不属于仿真）。
> **代码**：`Ecs/Ecs.cs`、`Ecs/Components.cs`、`Core/World.cs`（工厂）、`Core/Grid.cs`、
> `Core/Rng.cs`、`Modes/Mode.cs`、`Modes/Modes.cs`
> **核对**：1.39.0 / 2026-11-20

玩家能看见的规则与数值不在这里：单位、武器、伤害、移动、视线各有一页（见 §12）。

## 1. 三个地址

一个实体有三个"地址"，别混：

| 地址 | 是什么 | 谁用 |
| --- | --- | --- |
| `eid`（实体 id） | 系统的货币：查询返回它、组件里存它、线上发它 | 所有系统、`View`、`slot()` |
| `archetype` | 组件集相同的一组实体的存储体 | `groups()`、`locate()` |
| `row` | 实体在它自己 archetype 里的行号 | `a.f("pos")[row * 3 + 0..2]` |

`eid` 不再是存储地址：`ECS._arch[eid]` / `ECS._row[eid]` 把它翻成 (archetype, row)，
`swap_remove` 会改 `row`，而 `eid` 在实体活着期间不变。**拿到的 `row` 只在这一次访问里有效**——
同一 tick 里销毁了一个实体，别人的 `row` 可能就变了。

## 2. 存储形状

`Archetype` 一个组件集一个，每个组件一条稠密平铺缓冲：

```csharp
public sealed class Archetype {
    public string[] key;                       // 组件名，排序去重
    public Dictionary<string, Array> bufs;     // 组件名 -> float[] / int[] / byte[] / ushort[]
    public int[] ids;                          // row -> eid
    public int n, cap;                         // 活行数 / 容量
}
```

* 行宽由注册时的 `shape` 决定（`register("pos", Kind.F, 3)` → stride 3），所以 `pos` 的第 3 个
  分量是 `f("pos")[row * 3 + 2]`。四种元素类型：`Kind.F` / `I` / `U8` / `U16`。
* 缓冲从 16 行起（`Archetype.INITIAL`），满了翻倍。翻倍会**换掉数组实例**，所以
  `ECS._gen` 自增——所有缓存了缓冲列表的东西（`ECS.buffers`、每个 `View` 的 LUT）靠它失效。
* `add` 给新行按注册默认值重置；`swap_remove` 把最后一行搬进洞并返回被搬动的 `eid`
  （-1 表示洞本来就在末尾），这是唯一会改 `row` 的操作。
* 每行都是零初始化 + 默认值，所以"组件值为 0"是完全合法的值，**没有 null 语义**。

## 3. 组件集就是类型

`ECS.create(comps)` 是**唯一**决定组件集的地方：名字去重排序成 `key`，同一个 `key` 命中同一个
archetype（`_by_key`）。没有 `add_component` / `remove_component`，也故意不做。

* 状态转换因此是**另一个实体**：烟幕罐爆开是 `Smoke.cs` 销毁 `CANISTER_COMPS` 的实体、建一个
  `PUFF_COMPS` 的实体；子弹是 `PROJ_COMPS`，能被打的导弹是 `MISSILE_COMPS`（多一个血量池），
  有导引头的再加 `SEEKER_COMPS`。
* 实体的组件集由**工厂**拼出来，一个 `create` 定死：
  * `World.spawn_unit`：`UNIT_COMPS`（平民 `CIV_COMPS`）＋类型自己的 `tags`（`unit`/`combat`/`moves`/
    `person`…）＋`arms` 的 `ARM_COMPS`＋`designator` 的 `BEAM_COMPS`；`crew: true` 再加 `ai_crew`、
    平台 tag（`mech_ai`/`foot_ai`）与 `CREW_FCS_COMPS`，任务自己的 tag 走 `extra`。
  * `World.spawn_round`：池子按 `Defs.is_interceptable(wid)` 选，种类 tag 来自 `Defs.KIND_TAGS`，
    导引头记忆只给有导引头的种类。
* 允许 archetype 数量增长（一个组件集一个新 archetype），代价是 `_gen` 自增让所有 `View` 缓存重建。
  **别在热循环里拼新组件集**：那是每 tick 一次全量缓存失效。

## 4. 身份是组件，不是类型

`etype` 是**数据**（车体尺寸、血量槽、死亡配方、哪只手拿什么）；实体"是什么"由它的 tag 组件说：

```csharp
Defs.UNIT_DEFS[Defs.ETYPE_MECH].tags   // unit, combat, lockable, moves, designator
```

系统只查询它能处理的那个 tag，不 switch `etype`：

| 查询 | 含义 |
| --- | --- |
| `ecs.query("unit")` → `world.units()` | 弹会停、伤害会落、能被选为目标的壳子 |
| `ecs.query("combat")` → `world.combat_units()` | AI 可以打、据点可以占的 |
| `ecs.query("lockable")` | 红外导引头能锁的 |
| `ecs.query("moves")` → `world.movable()` | 有底盘、有驱动的 |
| `ecs.query("person")` | 人的近炸引信只看这个 tag |
| `ecs.query("proj_kind")` → `world.projectiles()` | 四种弹 archetype 的并集 |
| `ecs.query("smoke")` / `("interceptable")` / `("blast")` | 烟团 / 能被打的弹 / 待判的爆 |

残骸和尸体**不带 `unit`**，所以在 `world.units()` 里不存在——不需要任何排除名单；烟团连
`unit` / `faction` / `etype` 都没有，武器、AI、占点、碰撞全都看不见它。`ecs.has(eid, "arms")`、
`"designator"` 这类组件测试是唯一允许的"类型判断"。

## 5. 三扇门：对 buffer 写

系统独立地积分一批实体时，**走 archetype 的缓冲**，不是走 `View`：

### `ecs.groups(...)` → 一个 archetype 一块，行号 `0..n-1`

```csharp
foreach (var g in e.groups(RoundColumns.names)) {
    var c = new RoundColumns(g);            // 把缓冲全部 hoist 成字段
    foreach (var r in c) { r.age += dt; }   // 没有逐行组件测试
}
```

活行一定拥有全集，所以**不需要测试实体**。`World.update` 里的移动、倒计时、腿的转动都是这个形状。

### `ecs.slot(eid)` → 单个实体的 `(archetype, row)`

一个实体要碰很多组件时用一次查表换掉一遍 `View` 往返：

```csharp
var s = e.slot(pid);
if (!s.ok) continue;                          // 死了
int row = s.row; var a = s.a;
if (a.has("weapon")) ...                       // 组件测试
float t = a.f("first_hit_t")[row];
```

热路径上还有 `ecs.row_f(eid, "pos", out row)` / `row_i`：直接拿缓冲 + 行号。

### `ecs.locate(ids, out rows, out arch)` → 一次解析一张 id 表

循环要保留 id 表又要缓冲访问时用：`arch[k]` 为 -1 表示该 id 已死。

## 6. `View`：散读的门，带注册默认值

`ecs.fv("pos")` / `iv` / `uv` / `uv16` 按 `eid` 读写，**只用于散读**——一个系统碰几个它没查询到的
实体。语义有两半：写一个不拥有该组件的实体会抛 `KeyNotFoundException`，读一个不拥有它的实体返回
注册时的默认值（`owner` -1、`target` -1、`last_hit` -1、`first_hit_t` = `FIRST_HIT_ARMED` 2），
这正是"残骸没有 `weapon`，读出来是 0"能安全成立的原因。

* 批量形式写进调用者给的数组：`fv.get(ids, c, dst)`、`fv.get(ids, dst)`（整行，`ids.Length * stride`）。
* 实现是"每个 archetype 一条缓冲的查找表"，按 `_gen` 惰性重建——**写发生在读路径上**，
  单线程一 tick 成立；要让某个 `run()` 并行，先把这个缓存改成构建期做好。
* `fv` / `iv` / `uv` 只缓存一个名字一个视图，所以问错元素类型当场抛
  `InvalidOperationException`，而不是把毒化条目交给后面的调用者。

## 7. 一个 `pos`，一个 `vel`

`pos` / `vel` / `vel_cmd` 拆开是整套仿真的根：

| 组件 | 含义 | 唯一写者 |
| --- | --- | --- |
| `vel` | **它实际怎么动**：`pos` 靠它推进，所有碰撞对它解算，导引头对它做前置 | `movement`（壳）/ `projectiles`（弹）/ `smoke`（罐） |
| `vel_cmd` | 它**被告知**要跑多快：摇杆、机组意图、巡逻 | 控制器（`player_input`、AI、模式） |
| `pos` | 它站在哪 | 同上，由 `vel` 积分 |

工厂在 spawn 时就断言这条不变式，新实体类型忘记组件会在第一次 spawn 死掉，而不是三周后在导引头里：

```csharp
if (!comps.Contains("pos") || !comps.Contains("vel")) throw ... "a unit must own pos and vel"
if (d.tags.Contains("moves") && !comps.Contains("vel_cmd")) throw ... "a mover must own vel_cmd"
```

**脚本位移和传送写 `pos` 的同一处必须清 `vel`**（`debug_tp`、`Unstick`、模式的重生）：不清的话
下一 tick 的驱动会把这一跳当成速度读回去。速度与指令之间的差**就是机器**（机甲的斜坡、车的转向、
一堵墙），所以别拿 `vel_cmd` 当实际速度解算：旧的 `Seeker.track` 就是这么把机组的指令当速度喂给
比例导引的，攻顶弹因此打偏一个静止的壳子。

## 8. 生命周期与 id 复用

`destroy(eid)`：`swap_remove` 掉那一行、修被搬动者的 `_row`、`alive[eid] = false`、把 `eid` 推回
`_free`、并追加进 `ECS.destroyed`。

* **id 立刻可复用**（LIFO），所以"我刚销毁的那个尸体 id"下一秒可能就是别的实体。任何跨 tick
  持有一个 `eid` 的地方都得接受这件事，冻结归属信息（`proj_pid`、`last_pid`、`blast_pid`）就是为了它。
* `destroyed` 是给系统看的一 tick 内的名单；`World.update` 开头 `destroyed.Clear()`，所以系统重建
  某个死槽的替代品时不会读到上一 tick 的死亡。
* `ecs.stamps(size)` + `ecs.stamp_gen()` 是给需要"本 tick 访问过"集合的系统用的暂存区。

## 9. Grid：128 m 的粗相

`Core/Grid.cs` 是 128 m 方形格子的 CSR 索引，`World.update` 的**第一件事**就重建它
（`grid.build(ecs, "moves", "combat", "unit")`）。

* **为什么重建而不是增量维护**：单位每 tick 都在动，重建的成本才是要控的量；一场战斗的布局近似
  均匀，树/哈希都比一次计数排序贵。重建走 archetype 整块缓冲，两次扫描完成计数排序，**不留任何
  `eid` 间接**。
* **为什么三个 tag**：`moves` 只覆盖会动的壳子，`combat` 把不动的指挥基地捞进来（它是目标，车道
  终点就是它），`unit` 再把不动也不打但需要被看见的捞进来（靶场的地堡是 `unit` + `invulnerable`）。
  少一个 tag，AI 的候选盒里就少一类东西。
* 格子**自己存一份位置**（`items` / `pos` 同序），所以查询完全不碰 ECS。接口只有
  `cell_of(x, z)` 和 `box(ci, cj, r)`，返回 `(ids, pos)`。
* 唯一的读者是 `Systems/AI/Sense.cs::_acquire`：它把"谁在附近"从一个全场扫描变成一盒候选，
  再按距离排序、取前 `SIGHT_CASTS` 条视线。格子是一 tick 的快照，**动过之后再查**就是过时的。

## 10. `ecs.validate()`

末尾跑一次，查的是"值比较查不出来"的记账错误，四条：活实体有 archetype；`row` 在 `arch.n` 内；
`arch.ids[row] == eid`；每条活行反查回来 `_arch` / `_row` 都对得上。所有 `Harness/` 的
`--check` 都拿它收尾，所以行指针越界这类问题不会漂到画面上。

## 11. 访问顺序不属于仿真

`world.rng` 是**每线程一条**流（`Core/Rng.cs` 的 `[ThreadStatic]`）：tick 循环和 websocket 处理器
是两条线程、都会抽数（`debug_spawn` 抽 yaw、装备、名字），共用一个 `Random` 不安全。

* `MECH_SEED` 给线程流一个固定种子，让"一次只有一个线程抽数"的运行可复现——那正是 `--check` 的
  运行形态。接了 socket 之后两次运行必然不同。
* 所以 `query()` 返回的是什么次序都不重要（它按 archetype 块给出），`groups()` 的顺序也不重要，
  谁先被访问更不重要。**不要**为了"随机数可复现"去挑遍历形状，也**不要**靠 diff 两次运行验证；
  验证靠量级和每 tick 的不变量（见 [checks.md](checks.md)）。
* 真正属于"语义"的顺序只有两个：一 tick 的流水线（`pipeline.md`）和伤害链。

## 12. 模式拥有什么

`Modes/Mode.cs` 的 `Mode` 抽象类就是模式能改的全部：

| 成员 | 谁读 |
| --- | --- |
| `terrain(seed)` | `World` 构建地图 |
| `setup(world)` | 新一轮的初始（也是 `reset_match` 时重新跑） |
| `pre_systems()` / `post_systems()` | `World._build_pipeline` 把系统块插进共享段前后 |
| `mech_spawn` / `respawn_delay` | `World.spawn_mech` / `Mortality` 的复活计划 |
| 规则字段 `fixed_faction`、`start_money`、`rearm_anywhere`、`restart_respawn`、`smoke_cooldown`、`console_spawn_extra` | 共享代码问的那几个问题 |

`Modes/Modes.cs` 是注册表（`battle` / `range`，未知名字回落到 `battle`）。加一个模式＝加一个
文件夹＋两条注册，共享系统里没有一个 `if mode == ...`；反过来，任何"只有一个模式讲得通"的机制
就住进那个模式的文件夹，不往 `Systems/` 里加分支。模式的系统块见 [pipeline.md](pipeline.md) §3、§6。

* 单位、tag、每种底盘的数据：[../product/rules/units.md](../product/rules/units.md)
* 谁写 `vel`、五条定律与自行车模型：[../product/systems/movement.md](../product/systems/movement.md)
* 那条唯一的视线（地形/树冠/烟幕）：[../product/systems/sight.md](../product/systems/sight.md)
* 一 tick 的系统顺序：[pipeline.md](pipeline.md)；怎么量、基线读数：[checks.md](checks.md)
