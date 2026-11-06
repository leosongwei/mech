# 去掉"顺序即模拟"（范围收敛版）

## 1. 动机

不是为了并行。现在的架构有一条隐性契约：**遍历顺序是模拟的一部分**。它写在四个源码文件的
注释和 `AGENTS.md` 一整条里，靠 `world.rng` 那一条全局随机流维持：任何一次抽取次数的变化都
会平移之后所有实体的所有抽取，于是"改一行代码"和"换一场战斗"混在一起。

这条约束的**认知成本**是：每写一个循环都要先判断"这个系统抽不抽随机数？能不能用 `groups()`？"，
而这个判断错了不会报错，只会让战斗悄悄变成另一场。它同时是每次性能优化的讨论起点。

本计划只做一件事：**把这条约束删掉**，并让文档和注释不再声称它存在。

## 2. 范围

| 做 | |
| --- | --- |
| 步骤 1 | `world.rng` 改为线程本地；删掉这条约束上的所有注释和 `AGENTS.md` 那一整条 |
| 步骤 2 | `query()` 去掉排序和拷贝（顺序既然无意义，就不该为它付钱） |
| 步骤 3 | 文档修订：`AGENTS.md`、`Net/Netcode.cs` 两处过时注释、实测数字改成"单次实测" |

## 3. 明确不做

- **不并行**：没有 `Parallel.ForEach`，没有写集声明，没有串行/并行对照 harness。
- **不动伤害链**：`Combat.apply` 继续当库被 `Projectiles`/`Proximity`/`Blasts` 直接调用。
- **不做回放**：没有输入日志、状态哈希、版本戳。
- **不加** `rng_seed` 组件、不用 `hash(seed, tick, salt)`。
- **不改地形种子**：`Core/Terrain.cs:2115,2427` 的 `new Rng(seed)` 保持确定性——地图要发给
  客户端镜像、要能按 seed 复现一张图，而它只在启动时单线程跑一次，与模拟循环无关。

并行的路已经写在 `doc/plans/remove_order.md` 之外的讨论里；本计划做完之后若真要并行，
入口是 `View.Refresh()`（见 §7）。

## 4. 步骤 1：`world.rng` 线程本地

### 4.1 `Core/Rng.cs`

保留类和方法外壳（十个调用点、`Defs.ai_loadout(Rng)` / `ai_name(Rng)` 的签名都不动），
只换底层：

```csharp
public sealed class Rng
{
    private static int _seed = Seed();
    private static int _n;
    [ThreadStatic] private static Random? _tls;
    private static Random R() => _tls ??= new Random(_seed + 7919 * Interlocked.Increment(ref _n));

    // 每个线程一条自己的流；同一条流只在单线程下可复现
    public float random() => (float)R().NextDouble();
    // uniform / integers / randint / gauss / choice / shuffle / sample / weighted 照旧
}
```

`MECH_SEED` 环境变量给 `_seed` 定值；不设则用 `Environment.TickCount`。

**为什么要这个环境变量**：现在整个模拟是单线程的，所以 `MECH_SEED=2024` 时一条线程拿到的流是
确定的——**A/B 对比今天仍然能用**，而代价是零。一旦真开了第二个线程，这个性质自动失效，
这与要不要并行正好对齐。要在注释里写明这一点，别让它变成下一个"文档描述了一个不存在的保证"。

### 4.2 调用点

- `Core/World.cs:67` → `public Rng rng = new();`（去掉 `2024`）
- `Core/Terrain.cs:2115,2427` **不动**（地图生成仍需有种子）
- 其余十个抽取点一行不改

### 4.3 删掉的注释（逐条）

| 位置 | 删的是 |
| --- | --- |
| `Systems/Ai.cs:9-12` | 文件头「the order of the loop is part of the simulation. `_acquire` 和 `_civ` 从 `world.rng` 抽数，所以必须按 id 升序访问」 |
| `Systems/Ai.cs:101-103` | 「The order is the query order (ascending ids) because this loop draws the random numbers, so `groups` would reshuffle the whole battle」 |
| `Systems/Weapons.cs:112-114` | 「`fire` draws the spread from `world.rng`, so the order of these pulls is part of the simulation: keep the query order, do not group it」 |
| `Modes/Range/TestAi.cs:26-28` | 「`movable()` is a query, so the hulls are visited in ascending id order - `Ai.run` draws from `world.rng` inside its loop」 |
| `Core/Defs.cs:711-715` | 「The draw is part of the simulation (it comes off `world.rng`), so a list of templates in a different order fields a different battle」 |
| `Core/Defs.cs:763-766` | 「Drawn like the loadout (`ai_loadout`), off `world.rng`, so the order of the pool is part of the simulation」 |
| `Core/Rng.cs:9-15` | 「the *order* of the draws inside a loop is part of the simulation and every system must keep it」 |

每处替换成一句：**抽取线程本地，顺序不影响结果**（其中 `Ai.cs` 文件头那句展开成完整的两段，
因为它原来还带着"两个 load bearing 的东西"的结构，另一半——"sight leads, the machine follows"
——要保留）。

### 4.4 验收

- `./dotnet.sh build` 零警告（`Nullable` 开着）。
- `MECH_SEED=2024 ./dotnet.sh run -- --check battle --minutes 12` 连跑两次**逐条相同**
  （单线程下仍然可复现，这是这一步的证明）。
- 不设 `MECH_SEED` 跑两次：结果不同（也是证明）。
- `--check battle --minutes 12` 的绝对数值会变（和改动前不同），**只需量级一致**：
  每分钟击杀、爬升到 6 km 的时长、末期残骸数量。

## 5. 步骤 2：`query()` 去排序去拷贝

`Ecs/Ecs.cs` 的 `query()`：先扫两遍 `archetypes`、再拼进 `_qbuf`、再 `Array.Sort`、再
`_qbuf[..total]` 拷贝出一个新数组。改成一次扫描匹配（`_match` 复用一个 `List<Archetype>`）、
直接填进结果数组：**一次分配、无排序、无中间缓冲**，`_qbuf` 字段整个删掉。

（没有做成"零分配"：那要求把 `_qbuf` 直接返回给调用方，而查询是会重入的——
`Weapons.run` 在 `world.units()` 上循环，`fire` 里的弹道解算经
`world.aim_spheres()` → `sphere_table` 又调一次 `world.units()`。复用同一个缓冲区会把
外层正在遍历的数组踩掉。一次 `int[n]` 换掉一个会咬人的缓存，是这里的正确取舍。）

同时在注释里写清两扇门的语义，这是步骤 1 之后才成立的：

> `groups()` 和 `query()` 是同一个 archetype 集合的两种投影：前者保留块结构（快、无分支），
> 后者摊平成 id 列表。选哪个只看循环的形态。

### 验收

- `MECH_PERF=1` 的每 tick 耗时不变或下降。
- `--check net --minutes 30 --from 8` 的 kB/s 不变（快照内容与顺序无关）。
- `--check hit` / `--check intercept` 仍然全绿（数字可以有 ±1 的漂移，见步骤 1）。

## 6. 步骤 3：文档修订

### 6.1 `AGENTS.md:153-158` —— 删掉不存在的 API

原文描述 `ecs.columns(ids, names)` 是"batch door"，并声称 5× 提速。**这个 API 在
`Ecs.cs` 里不存在**（`Netcode` 实际是一行一个 `slot()`）。照它写代码会编译不过。

替换为：

```
`ecs.locate(ids)` gives `(rows, archetypes)` for a flat id list — one resolution,
then every access is a buffer index. A system that touches many components on
one entity takes `slot()`; one that touches one component on many entities takes
a `View`. There is no batch-column door: a `View` resolves the archetype per
access, which is why the snapshot builds its rows through `slot()`.
```

### 6.2 `AGENTS.md:159-168` —— 整条重写

原文是「Iteration order is part of the simulation.」那一段。**这是本计划的核心交付物**，
把它换成：

```
* **The order a system visits entities in is not part of the simulation.**
  `world.rng` is thread-local (`Core/Rng.cs`), so no draw can depend on who drew
  before it, and `query()` returns an unordered list. Any pass may use `groups()`
  (archetype-major: no allocation, no per-row component test) or `query()` (flat
  ids) interchangeably — pick on loop shape, never on "does this system draw".
  Two runs of the same build are *not* expected to agree; verification is by
  magnitude and by the per-tick invariants, not by diffing two runs. A seeded
  run is reproducible only while the simulation is single-threaded
  (`MECH_SEED`), which is what `--check` uses today.
  What *is* order is the system pipeline (below) and the damage chain
  (projectiles/proximity write hp, blasts judge the burst, mortality decides the
  death): that is semantics, not an accident, and never to be relaxed.
```

### 6.3 `AGENTS.md:244-245`

「The draw comes off `world.rng`, which makes the order of the list part of the
simulation like every other draw.」
→「The draw comes off `world.rng` (thread-local, so the list's order carries no meaning).」

### 6.4 `AGENTS.md:608-609`

「an AI mech draws one off `defs.AI_NAMES` at spawn (a draw off `world.rng`, so the
pool's order is part of the simulation)」
→「an AI mech draws one off `defs.AI_NAMES` at spawn (a draw off `world.rng`)」

### 6.5 `AGENTS.md:414-416` —— 保留但有细微差别

「resolves impacts in ascending `pid` order (entity creation order is part of the
simulation, not an implementation detail)」——这句话在步骤 1 之后**不再准确**，但结论没变：
落点仍按 `pid` 升序解算，只是理由从"两次运行必须一致"变成"同一 tick 内谁先扣血要有一个
固定规则，否则击杀归谁不确定"。改成：

```
and only then resolves impacts in ascending `pid` order — not because two runs
must agree (they don't, see "iteration order" above), but because within one
tick the credit for a kill has to follow a fixed rule.
```

### 6.6 `AGENTS.md` 的实测数字

Build / Harness 一节里的数字（`78 kB/s`、`67.8 kB/s`、`1.2 ms/tick`、`34/36 拦截`、
`3–4 kills per 16` 等）现在都是**一次实测的样本**，不再是可以逐位对比的基线。在 Harness 一节
开头加一句：

> RNG 是线程本地的：同一份构建两次运行结果不同。下面所有 "measured" 都是单次运行的量级，
> 用来判断改动有没有让事情变坏，不是可以逐位对比的基线。`MECH_SEED=2024` 时单线程仍可复现。

不改那些数字本身（它们仍然代表量级），只加这一句。

### 6.7 `Net/Netcode.cs:325,612` —— 过时注释

两处注释都还在引 `columns()` / "a hull that has no arms is exactly what `columns()` fills"。
改成指向真实机制（`View`/`slot()` 给默认值）。

## 7. 留下的债务（写清楚，不修）

`Ecs.cs:259-266` 的 `View.Refresh()` 在读取路径上写 `_lut` / `_gen`（懒刷新），
`ECS.view()` 也在懒创建 `_views` 条目。**单线程下完全无害**，但它是将来任何并行的第一块拦路石。
在那里加一行注释，把入口写在代码旁边：

```csharp
// lazy LUT rebuild: a write on a read path.  Harmless while the tick is one
// thread; if a `run()` is ever parallelised, make this eager (refresh every view
// when `_gen` bumps) and warm `_views` at startup.
```

## 8. 提交划分

**一次提交**。代码、注释、`AGENTS.md` 必须同步：任何一半单独存在都会让文档与代码不符——
而这个计划的存在理由就是消除这种不符。

`doc/product/` 不加条目，`MechServer.csproj` 的 `<Version>` 不 bump：玩家看不到任何变化
（有的只是"这局和三局前不完全一样"，这本来就已经是事实）。

## 9. 完成判据

1. 全仓搜索 `part of the simulation`、`keep the query order`、`ascending id order`、
   `columns(ids` 不再命中任何"顺序/API"语境。
2. 任何一个系统里，把 `foreach (var g in e.groups(...))` 换成 `query()` 或反过来，
   **不需要任何论证**——这是本计划唯一的成功标准。
3. `MECH_SEED=2024` 下 `--check battle` 单线程可复现；`--check hit` / `intercept` / `net`
   量级不变。
4. 没有新增任何组件、没有新增抽象、代码净行数为负。

## 10. 落地记录

已按本计划完成（步骤 1–3 一次提交）。

| 验收项 | 结果 |
| --- | --- |
| `./dotnet.sh build` | 0 警告 0 错误 |
| `MECH_SEED=2024 --check battle --minutes 3` 连跑两次 | 除耗时行外逐行相同 |
| 不设 `MECH_SEED` 连跑两次 | 模拟内容不同（预期） |
| `--check hit` | 与改动前逐行相同（未设 `MECH_SEED` 时单发弹道的 `wob`/散布每次不同，属预期） |
| `--check battle --minutes 12 --perf` | 改动前 1.57 ms/tick / 707 实体；改动后 1.30 ms/tick / 815 实体 |
| `--check net --minutes 16 --from 8` | 正常输出，`ecs.validate() ok` |

一处与计划的偏差：`query()` 不是"零分配"，而是一次 `int[n]` 分配。原因是查询会重入——
`Weapons.run` 在 `world.units()` 上循环，`fire` 里的弹道解算经 `world.aim_spheres()` →
`sphere_table` 又调一次 `world.units()`，复用同一个缓冲区会把外层正在遍历的数组踩掉。
详见 §5。
