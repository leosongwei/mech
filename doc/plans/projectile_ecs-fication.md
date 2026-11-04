# 投射物系统的 ECS 化（`first_hit_record`）

## 1. 动机

一发弹从枪口到炸开，今天由一个 649 行的 `Systems/Projectiles.cs::run` 全部负责：构建球表、
四种飞行/制导、地形碰撞、单位碰撞、寿命、命中结算；再加上 `Systems/Proximity.cs` 里同一段
"线段 vs 运动球"的第二份实现，以及它自己的一次 `_impact` + `destroy`。四个后果：

1. **身份靠 4 路分支。** pass 1 读出 `proj_kind`（`:100`）后按它分四支（`:132-245`），pass 2
   再读一次（`:273`）再分一次；而仓库其它地方早就把这件事交给了标记组件（`unit` /
   `combat` / `moves` / `person` / `lockable`，见 `Core/Defs.cs:878` 的 `tags`）。加一种弹 =
   改 `fire`、改 `run` 的两个分支、改 `_impact` 的调用者，四处共享代码。
2. **碰撞由管线顺序决定，而不是由几何决定。** `Projectiles` 在 `Proximity` 之前，而
   `Projectiles` 会 `destroy` 掉已经命中的弹。于是一发在腿上 t=0.4 处掠过来袭导弹 2 m、t=0.7 处
   撞地的 30 mm，**引信从不响**——它要打的那条导弹是被该响的那一发打下来的。
3. **寿命有三处特例，散在两个 pass 里。** `:105-119`（`life<=0`：`k==3` 原地炸、其它静默消失）、
   `:365`（`maxdist`：导线末端炸）。同一件事的三种写法。
4. **"谁先打到"是一个函数局部变量。** 地面命中、单位命中、树干命中在 `run` 里比出
   `best_t` / `best_unit` / `best_mod` 三个局部变量，只有这段代码能看见它们——任何拆分都得先
   把这份判定搬到一个多个系统都够得着的地方。

本计划把这份判定变成一个**实体组件记录**（`first_hit_record`），把每种弹变成
**一个 tag + 一个 flight 系统**，把每种碰撞变成**一个只写记录的 gatherer**，再留**一个**
读记录的裁决者。改完之后：加一种弹 = 加一条 `KIND_TAGS` + 一个系统，共享代码零修改（和
"加一个模式就是加一个目录"同构）。

## 2. 范围

| 做 | |
| --- | --- |
| 步骤 1 | `first_hit_record` 组件组、四个 flight 系统、`Flights.Expire`、`Sweep.Spheres`、`Impact`、`Sweep` 里的共享求解器与 scratch 池 |
| 步骤 2 | `Proximity` → `FirstHit.Fuse`（这一步**修正语义**，见 §4.4） |
| 步骤 3 | `run` 的 pass 2 按碰撞体的形状拆成三步：`FirstHit.Ground`（连续高度场）/ `FirstHit.Structures`（建筑柱体 + 树干）/ `FirstHit.Units`（球体）；删掉 `walk` 回退 |

**明确不做：**

- **不改任何数值**：伤害、`hit_r`、`prox`、`life`、`maxdist`、`thrust`/`maxvel`、`spread`、
  推力曲线、地形采样步长（9 m / 8 m）全部原样。步骤 1 与 3 必须是"数字逐位不变"的重构。
- **不改客户端**：投射物不在快照里被模拟，`map.js` 的 `predictImpact` 镜像的是**飞行员自己
  的**弹道解算（`wd.thrust` / `maxvel` / 重力），与本计划无关。`PROJ_STRIDE` 那 32 B 一个字节
  都不动。
- 不并行、不做回放、不做弹丸对象池、不在 `World` 上维护按 id 索引的平行数组。
- 不动 `Blasts` / `Mortality` / `Combat` / `Ballistics` 的结构（只改注释里指错的路径）。
- 不给弹加 `vel_cmd`：弹是投掷物，`vel` 就是它自己的命令。
- 不给弹加 `moves`：`movement` 管的是坡度和轮子。
- **不做可摧毁建筑/树木**。本计划只把碰撞拆到"按形状各查各的"，并为将来的可摧毁留出四个零成本的门
  （§7.5）：`first_hit_kind` 从第一天就分 `BUILDING` / `TRUNK`、结构查询走
  `terrain.structure_hit` 这道门、`first_hit_unit` 这个 I32 留给结构 id、摧毁 patch 复用
  `plant` 的下发路径。网格数据、chunk 增量重建、瓦砾 tile 类型、客户端 patch 里的墙高段都
  不属于本计划。

## 3. 现状（带行号的事实）

| 职责 | 现在在哪 |
| --- | --- |
| 每 tick 的球表 | `Projectiles.sphere_table`（`:384`），`World.sphere_table`（`World.cs:342`）缓存，`_sph_premove` 标记"建在 movement 之前" |
| 宽相 | `_block_index`（`:510`）、`_block_span`（`:555`）、`_leg_spheres`（`:571`） |
| 飞行/制导 | `run` pass 1（`:97-245`），4 路分支 |
| 地形 | `terrain.ground_hits_many`（`Terrain.cs:737`）一次批扫，每个采样点取 `_sample = height_at + _walls_arr`（`:708`，即地面和房屋已经是一个数），`walk[i]` 标记树瓦片 → 回到标量 `ground_hit`（`:1100`，`_march(At.obstacle, trunks: true)`） |
| 结构 | 建筑是每 tile 一个米制高度字节 `walls[k]`（`wall_at` `:506`）；树干是 `_trunks(i,j)` 的圆柱（`_trunk_hit` `:659`）；两者现在都被揉进上面那条 8 m 采样 |
| 单位 | `run` pass 2（`:283-345`）：相对运动最近点，`pr = k == 0 ? 0.35f : 0.6f`（`:297`） |
| 近炸 | `Proximity.run`（`:31`），自己解最近间隙、自己 `_impact`（`:152`）+ `destroy` |
| 命中结算 | `Projectiles._impact`（`:624`），三个调用点（pass 2 两处 + `Proximity.cs:152`） |
| 实体集 | `Components.PROJ_COMPS`（`:225`）、`MISSILE_COMPS`（`:235`，= PROJ + `interceptable hp hpmax armor last_hit tgt_mem tgt_vel`） |
| "哪些是弹" | `World.projectiles()` = `ecs.query("proj_kind")`（`World.cs:328`）；线上 `proj_kind` 一字节 + `tier_of_round(kind)`（`Netcode.cs:544,714`）；`Ai._missiles`（`Ai.cs:243`）；`Tiers.cs:159` |
| 生成 | `Weapons.fire`（`:205`），`is_interceptable` 决定 archetype（`:292` / `:312`） |

一次 `run` 里的临时数组：`run` 内 14 个，`sphere_table` 内约 13 个，每 tick 约 27 次分配。

## 4. `first_hit_record`

### 4.1 是什么

一条记录，六个组件字段，长在**每一发弹**身上：

| 组件 | 类型 | 默认 | 含义 |
| --- | --- | --- | --- |
| `first_hit_t` | F32 | `FIRST_HIT_ARMED` (2) | 这一 tick 腿上遇到第一个东西的位置，`[0,1]`；2 = 本 tick 什么都没遇到 |
| `first_hit_pos` | F32×3 | 0 | 停在哪（gatherer 直接写点，不靠 t 反推） |
| `first_hit_kind` | U8 | 0 | `NONE / UNIT / GROUND / BUILDING / TRUNK / FUSE / EXPIRE`（前后两者今天都只是放一发烟，分开是为了将来能扣结构血、出不同 fx） |
| `first_hit_unit` | I32 | -1 | 直接命中的那个实体；结构命中今天写 -1，将来装 `building_id` |
| `first_hit_mod` | U8 | 0 | 命中模块（`MOD_CHASSIS` / 手臂） |
| `first_hit_gap` | F32 | 0 | 引信触发时的真实间隙（`splash` 要在它上面算） |

写法只有一种，每个 gatherer 都是这四行：

```csharp
if (t < first_hit_t[row])
{
    first_hit_t[row] = t;
    a.f("first_hit_pos")[row * 3 + 0] = p.x;   // … +1 / +2
    a.u8("first_hit_kind")[row] = K;
}
```

**为什么只有一个格子。** 物理上只有第一个算数：一 tick 里可能同时算出引信 t=0.4、树干
t=0.55、地面 t=0.7、某个车体 t=0.9，其中三个在物理上不存在——弹在 0.4 就没了。所以这是一次
min 归约，不是收集。

**为什么是组件。** archetype 的行是 dense 的、`destroy` 时 `swap_remove` 会把最后一行搬进
洞里；任何按 id 索引的平行数组都要跟着重排，而组件自己会跟着实体搬家。仓库里已有同一形状的
先例：`proj_prev` 就是"flight 系统写、几个系统读"的每 tick 状态。

**为什么六个字段而不是一个 vec。** 理由和 `wheel_speed` / `backout_t` / `blocked_t` 从
`veh` 里拆出来一样：`record[1]` 不会告诉任何人它是引信间隙。

### 4.2 生命周期 = 一个 tick

| 时刻 | 谁 | 做什么 |
| --- | --- | --- |
| flight 系统开头 | 该弹自己的 flight 系统 | `first_hit_t = FIRST_HIT_ARMED`，同时写 `proj_prev`（腿的起点） |
| flight 之后 | `FirstHit.Ground` / `FirstHit.Structures` / `FirstHit.Units` / `FirstHit.Fuse` | `if (t < first_hit_t)` 覆盖 |
| flight 之前 | `Flights.Expire` | `first_hit_t = 0`（t=0 处的一次"首次命中"：电池/导线先耗尽了） |
| 全部之后 | `Impact` | `first_hit_t <= 1` → 按 `first_hit_kind` 结算 → `destroy` |

`(proj_prev, pos)` 是"这发弹这一 tick 飞过哪条线段"，`first_hit_record` 是"线段上哪一点是
它的终点"。两者由同一个系统、同一刻上膛，这是它们成对的原因。

### 4.3 不变量（正面陈述）

1. gatherer 只写记录，**没有任何 gatherer 销毁实体**，所以它们之间顺序自由。
2. `Impact` 是唯一读记录、唯一调用 `detonate`、唯一销毁投射物的系统。
3. flight 系统的 `if (first_hit_t[row] <= 1f) continue;` 是**语义**（`Flights.Expire` 判了死刑的弹
   这一 tick 停在原地，与今天"寿命在移动之前检查"逐字节一致）；gatherer 里同样一行的
   `continue` 是**纯成本优化**，两者长得一样、性质不同，注释里要分开写。
4. `first_hit_t == FIRST_HIT_ARMED` 是"本 tick 无接触"的唯一表示，用具名常量，`2f` 不散在
   七个文件里。

### 4.4 它修的那个 bug（步骤 2 的唯一行为变化）

顺序：`Projectiles`（含地面/单位命中）→ `Proximity`（引信）。今天：

- 30 mm 弹这一 tick 腿上 t=0.4 掠过一条来袭导弹 2 m（引信该响），t=0.7 撞地；
- `Projectiles` 在 t=0.7 放烟并销毁这发弹；
- `Proximity` 遍历 `world.projectiles()` 时它已经不在 → 引信从没响过。

拆开之后两个候选写进同一个格子，`t=0.4` 留下，`Impact` 在 0.4 处爆 → 击落。
`--check intercept` 的 34/36 应该因此变好一点，这是**预期**，要单独记录。

## 5. 组件与 tag

### 5.1 组件集

```csharp
// Components.cs —— 名字用全拼：一条 grep first_hit 就能列出全部写者和唯一的读者
public static readonly string[] PROJ_COMPS = {
    "pos", "vel", "faction", "proj_kind", "proj_wid", "proj_life", "proj_src",
    "proj_pid", "proj_name", "proj_etype",
    "proj_age", "proj_seed", "proj_prev", "proj_trav", "target",
    // first_hit_record：这一 tick 的腿停在哪（§4）
    "first_hit_t", "first_hit_pos", "first_hit_kind",
    "first_hit_unit", "first_hit_mod", "first_hit_gap",
};

// 能被打下来的那一半：一个池子。制导记忆从它里面搬出去，只归真正用它的弹
public static readonly string[] MISSILE_COMPS = Cat(PROJ_COMPS,
    "interceptable", "hp", "hpmax", "armor", "last_hit");
public static readonly string[] SEEKER_COMPS = Cat(PROJ_COMPS, "tgt_mem", "tgt_vel");
```

| archetype | 组件（`create` 会去重） |
| --- | --- |
| 子弹 | `PROJ_COMPS` + `bullet` |
| 无制导火箭 | `MISSILE_COMPS` + `unguided_missile` |
| SACLOS | `MISSILE_COMPS` + `saclos` |
| 顶部攻击 | `MISSILE_COMPS` + `SEEKER_COMPS` + `top_attack` |

收益：一发火箭不再背 24 B 的 `tgt_mem`/`tgt_vel`；每个 flight 系统 `groups()` 提 buffer、
行里没有 `proj_kind` 读、没有分支。

### 5.2 tag 从 `kind` 派生

`kind` 已经是 4 值闭集，所以一张表就够，不用在每个 `WeaponDef` 上重复写。表里只有**身份**
标签，"它有没有一个可被打掉的池子"仍然由 `Defs.is_interceptable(wid)` 回答（它读的就是
`kind`）——同一个问题留一个答案：

```csharp
// Defs.cs
public static readonly Dictionary<string, string[]> KIND_TAGS = new()
{
    ["bullet"] = new[] { "bullet" },
    ["rocket"] = new[] { "unguided_missile" },
    ["saclos"] = new[] { "saclos" },
    ["top"]    = new[] { "top_attack" },
};
```

`Weapons.fire` 的组件集于是是三段拼起来的：

```csharp
var comps = new List<string>(Defs.is_interceptable(wid)
    ? Components.MISSILE_COMPS : Components.PROJ_COMPS);
if (kind == 3) comps.AddRange(Components.SEEKER_COMPS);   // 只有它带制导记忆
comps.AddRange(Defs.KIND_TAGS[wd.kind]);
```

**`proj_kind` 保留。** 它的角色和单位的 `etype` 一样：`Defs.WEAPONS` 的索引 key + 上线的那
一字节。于是 `world.projectiles()`（`query("proj_kind")`，天然跨 4 个 archetype 取并集）、
`Netcode.tier_of_round`、`Ai._missiles`、`Harness/Tiers` 全部不用改。**不加**一个
`projectile` 标签——那是同一个问题的第二个答案，"哪些是弹"已经由 `proj_kind` 回答了。

### 5.3 生成点

`Weapons.fire` 保留解算（枪口、机架、零位、散布），把最后那段"建实体 + 写 12 个字段"抽成
`World.spawn_round`，和 `spawn_unit` 的断言同构：

```csharp
if (!comps.Contains("first_hit_t"))
    throw new InvalidOperationException("a round must own first_hit_record");
if (comps.Contains("interceptable") && !comps.Contains("hp"))
    throw new InvalidOperationException("an interceptable round must own a pool");
```

`first_hit_t` 的初值来自 `ecs.register("first_hit_t", Kind.F, 1, FIRST_HIT_ARMED)`：
`Archetype.add` → `ResetRow` 用 spec 的 default 填行，`View` 也用它，所以上膛这件事在工厂
就完成了。**依赖 `2f` 的默认值**，别靠 `fire` 里手写（漏一个就是"一出生就炸"）。

## 6. 系统与管线

### 6.1 串行调用顺序（`Core/World.cs::_build_pipeline`，替换今天的 `:125-147`）

```csharp
systems = new List<Action<World, float>>
{
    Systems.Rearm.run, Systems.PlayerInput.run,
};
systems.AddRange(mode_mod.pre_systems());                 // battle: Systems.Ai.run

// 视线 → 枪：gunnery 让 yaw / pitch 成为已经架好的炮，fires 与 draw 都读它
systems.AddRange(new Action<World, float>[]
{
    Systems.Gunnery.run, Systems.Locking.run, Systems.Smoke.run, Systems.Weapons.run,
    Systems.Movement.run,
    // 球表：movement 之后（单位的 pos 已是 tick 末）、flights 之前（导弹的 pos 还是 tick 初）
    Systems.Sweep.Spheres.run,
    // 寿命 / 导线：在移动之前结算，与今天"寿命在 pass 1 顶部检查"逐字节一致
    Systems.Flights.Expire.run,
    // 一弹一系统，靠 tag 分派，行里没有 proj_kind 分支
    Systems.Flights.Ballistic.run,                        // tag: bullet         今天 k == 0
    Systems.Flights.Rocket.run,                           // tag: unguided_missile   k == 1
    Systems.Flights.Saclos.run,                           // tag: saclos          k == 2
    Systems.Flights.Top.run,                              // tag: top_attack      k == 3
    // ---- 碰撞三步，按形状各查各的；四个 gatherer 只写 first_hit_record，
    //      彼此顺序自由（只差白做的 compare）----
    Systems.FirstHit.Ground.run,                          // 步骤一：连续高度场（批扫）
    Systems.FirstHit.Structures.run,                      // 步骤二：建筑柱体 + 树干圆柱（逐 tile DDA）
    Systems.FirstHit.Units.run,                           // 步骤三：单位与在空中的导弹（球表）
    Systems.FirstHit.Fuse.run,                            // 30 mm 近炸（person ∪ interceptable）
    // ---- 唯一读记录、唯一调 detonate、唯一销毁投射物 ----
    Systems.Impact.run,
});

// 伤害链：弹和爆都写 hp，爆在弹之后结算，只有 mortality 判死
systems.AddRange(new Action<World, float>[]
{
    Systems.Blasts.run, Systems.Mortality.run, Systems.Economy.run, Systems.Debris.run,
});
systems.AddRange(mode_mod.post_systems());                // battle: Systems.Waves.run
```

### 6.2 哪些相邻关系是语义、哪些只是成本

| 相邻关系 | 性质 |
| --- | --- |
| `movement` → `Sweep.Spheres` | **语义**。单位已被移动，`pos` 是 tick 末，`prev = pos - vel*dt` |
| `Sweep.Spheres` → `flights` | **语义**。导弹还没被本 tick 移动，`pos` 是 tick 初，`disp = vel*dt` |
| `Flights.Expire` → `flights` | **语义**。电池/导线在移动之前结算，弹这一 tick 停在原地 |
| `flights` → 四个 gatherer | **语义**。腿 `(proj_prev, pos)` 由 flights 写 |
| 三个碰撞步骤之间（以及 `FirstHit.Fuse`） | **无**。各自只做 `if (t < first_hit_t)`，换个顺序只改变谁白做了一次 compare——"地面 → 结构 → 单位"是这个顺序，但它不承载语义 |
| 四个 gatherer → `Impact` | **语义**。`Impact` 是记录唯一的读者，也是唯一的销毁者 |
| `Impact` → `Blasts` | **语义**。爆炸是实体，在下一 stage 结算，所以链式反应天然相隔一 tick |
| `Blasts` → `Mortality` | **语义**。伤害链的最后一个判死者 |

`weapons` 仍然在 `movement` 之前，所以它拿的是 `aim_spheres` 的 premove 表（`_sph_premove`），
`Sweep.Spheres` 随后重建——机制原样保留，只是现在"谁建、什么时候建"是管线里写着的。

### 6.3 各系统职责

| 系统 | 文件 | 职责 |
| --- | --- | --- |
| `Sweep.Spheres` | `Systems/Sweep.cs` | 调 `world.sphere_table(dt)`，把"谁先要就给谁建"变成显式 |
| `Flights.Expire` | `Systems/Flights.cs` | `life`/`maxdist` → 写 `first_hit_t = 0` 并按 `wd.expire_burst` 决定炸/消失 |
| `Flights.Ballistic` | `Systems/Flights.cs` | `vy -= g*dt`（今天的 `k==0`） |
| `Flights.Rocket` | `Systems/Flights.cs` | `speed = min(maxvel, speed+thrust*dt)`，再落重力（`k==1`） |
| `Flights.Saclos` | `Systems/Flights.cs` | 视线前方点转向 + 摆动 + 燃尽后二次阻力（`k==2`） |
| `Flights.Top` | `Systems/Flights.cs` | `Seeker.track` + `Flight.step`（`k==3`） |
| `FirstHit.Ground` | `Systems/FirstHit.cs` | 步骤一：`ground_hits_many` 一次批扫，所有腿，**只算高度场** |
| `FirstHit.Structures` | `Systems/FirstHit.cs` | 步骤二：每条腿一次 `terrain.structure_hit`（逐 tile：建筑 slab / 树干圆柱），跨不到结构 tile 的腿整条跳过 |
| `FirstHit.Units` | `Systems/FirstHit.cs` | 步骤三：球表 + 块索引 + `Sweep.earliest`，`pr` 0.35 / 0.6 |
| `FirstHit.Fuse` | `Systems/FirstHit.cs` | `person ∪ interceptable`，`gap <= wd.prox`（今天的 `Proximity`） |
| `Impact` | `Systems/Impact.cs` | 读记录、`Combat.hit`/`splash`、`Blasts.spawn`、`detonate`、`destroy` |

`Systems/Projectiles.cs` 与 `Systems/Proximity.cs` 消失：数据（`HIT_R`、球表、块索引、
最近点求解）进 `Systems/Sweep.cs`，行为进 `Systems/Flights.cs`、`Systems/FirstHit.cs`、
`Systems/Impact.cs`。

## 7. 关键论证

### 7.1 三种碰撞体，三种粒度

今天地面和房屋被揉成一个数：

```csharp
private float _sample(float x, float z) => height_at(x, z) + _walls_arr(x, z);   // Terrain.cs:708
```

每个 8 m 采样点都算这一句，cull 网格存的也是 8×8 块里的 `max(ground + obstacle)`。揉在一起的
理由曾经是"反正一次采样顺手算两样"，但它有一个代价：**拿错粒度去测一种形状**。

| 碰撞体 | 几何 | 该用的查询 | 今天用的是什么 |
| --- | --- | --- | --- |
| 地面 | 连续高度场（每 tile 4 个角点双线性） | 沿腿每 8-9 m 取一个点，比较 `y` 与 `height_at` | 正确，但每个点顺带查了墙 |
| 建筑 | **整格柱体**：`[ground, ground + walls[k]]`，每 tile 一个米制字节 | 逐 tile 的 slab 测试（进入 t / 离开 t 与高度区间求交） | 8 m 点采样：一条 36 m 腿跨 4-5 个 tile，却取 5 个点 |
| 树干 | 圆柱，r 0.35-0.6 m，`h = 0.4 ×` 树高 | 圆柱与线段求交（`_spans` DDA + 二次方程） | 已经是对的（`_trunk_hit`），但被 `walk` 标记藏在标量回退里 |
| 单位/导弹 | 球 | 相对运动最近点 | 已经是对的（球表 + 块索引） |

所以拆分不是"把最热的循环走两遍"，而是**让每一种形状用它自己的解**：

- **地面**那一遍仍然是一次批扫（`ground_hits_many`），每个采样点只算 `height_at`，省掉墙查表；
  cull 继续用今天那张 `_cull_solid`（含墙的最大值）——它对地面是保守的，跳过率与今天完全相同，
  所以这一遍严格比今天便宜。
- **结构**那一遍是逐 tile 的 DDA：每个跨过的 tile 一次类型判断，跨不到结构 tile 的腿整条跳过
  （开阔地上绝大多数；今天 `walk` 标记那段循环已经在做这个判断，成本早就付过了）。`solid` 网格
  可以再加一个"这个 8×8 块里有墙吗"的位，整块 128 m 直接跳过。
- 两遍都只写 `first_hit_record`，谁先谁后只影响白做的 compare（§4.3、§6.2）。

于是三个碰撞系统正好对上"地面 → 建筑与树 → 单位"三步，而"建筑与树同一个系统"是**对的**：
它们同属"地图上直立的结构"，共用 chunk 索引、共用生命周期、共用一份下发 patch；区别只在
系统内部那一次形状分派（建筑 → slab，树 → 圆柱）。**但几何上它们不统一**：一棵树永远不是
一个 16 m 实心柱——只有树干挡子弹，叶子只挡视线（`AGENTS.md` 的 "trunks for physics,
canopies for sight"）。"树作为一种建筑"说的是生命周期，不是碰撞体。

**`walk` 标记随之消失。** 批扫对树瓦片给出的地面答案本身是合法的（树瓦片的地还是地），
`FirstHit.Structures` 给出更小的 t，min 归约自动选对。删掉的东西连成一串：

| 删 | 理由 |
| --- | --- |
| `Terrain.cs:737` 里标 `walk` 的那段循环 | 不再需要把腿退回标量重解 |
| `Terrain.ground_hit`（`:1100`） | 唯一调用者就是上面那段 |
| `_march` 的 `trunks` 参数与 `:967` 那次 per-tile `_trunk_hit` | 只有 `ground_hit` 用 `trunks: true`；`los` 用的是 canopies |
| `_march` 的 `At` 枚举与 `obstacle_at`（`:548`） | 只剩 `los` 一个使用者，即 `At.sight` |
| `sample_points` / `_sample` / `_walls_arr`（`:690-713`） | `sample_points` 全仓零调用；另两个随 `ground_hit` 一起失去调用者 |

`_trunk_hit`（`:659`）与公开的 `trunk_hit`（`:1072`）保留——它们变成 `structure_hit` 门里树
那一半的实现。`wall_at`（`:506`）同样保留：它是建筑那一半的现成实现。这是**全仓最热循环里
少一个 per-tile 分支**，Step 3 要单独用 `MECH_PERF=1` 量一次。

### 7.2 `Flights.Expire` 在 flights 之前

这样三处特例都落在与今天完全相同的位置上：

| 情形 | 今天 | 拆开后 |
| --- | --- | --- |
| 顶部攻击电池耗尽 | pass 1 顶部，`pos` 是腿起点 | `Flights.Expire` 在 flights 之前，`pos` 同样还是腿起点 |
| 其它弹 `life<=0` | 静默销毁 | `Flights.Expire` + `wd.expire_burst=false` → 静默销毁 |
| `maxdist` 耗尽 | pass 2 末尾，`(p1x,p1y,p1z)` | 下一 tick 的 `pos` 正好等于上一腿末端 → 同一点 |

唯一差异：同一 tick 内"导线最后一米"与"撞上单位"谁赢（今天碰撞赢，之后导线赢）。要保留旧
行为，`Flights.Expire` 写成 `if (first_hit_t[row] > 1f)` 即可。计划采用**保留旧行为**的写法。

`WeaponDef` 新增一个字段（唯一的 defs 改动）：

```csharp
public bool expire_burst;   // 电池/导线耗尽时战斗部是否起爆
                            // bullet/rocket = false（今天的行为）, saclos/top = true
```

SACLOS 的 `life`(36 s) 永远先被 4 km 的导线截断，所以 `true` 也不改变任何可观测行为。

### 7.3 一份求解器，两处调用

"线段 vs 运动球最近点"今天写了两遍（`Projectiles` pass 2 内联、`Proximity` 内联，后者还多个
`- radii` 得到间隙）。抽到 `Sweep`：

```csharp
// 返回最近接近的 t（腿上比例）、候选行、以及减去半径后的间隙
public static bool earliest(SphereTable sph, Vec3 p0, Vec3 p1, float pr,
                            int[] rows, int nrow, out float t, out int row, out float gap);
```

`FirstHit.Units` 用 `gap <= pr`，`FirstHit.Fuse` 用 `gap <= wd.prox`，两处共用同一段相对运动
解算（`rel = prev - p0`、`w = seg - disp`、`t* = clamp(rel·w/|w|²)`）。这条式子必须只有一份：
它是"导弹 20 m/tick 对 2.5 m 引信"能判对的原因，两份拷贝早晚会漂。

### 7.4 scratch 池

每 tick 约 27 次数组分配（`run` 14 + `sphere_table` 13）。拆成 11 个系统若不池化会变成更多。
在 `World` 上挂一个 `Sweep` 对象持有 `ids / p0 / p1 / terr_hit / terr_t / rows / stamp`，
只在实体数变大时重分配；`sphere_table` 也复用它的缓冲区。目标：拆完之后每 tick 的分配次数
**少于今天**。

### 7.5 为可摧毁的建筑与树留的门（本计划不做）

可摧毁不在本次范围（§2）。它将来要的四样东西里，前三样是纯新增的地图数据，第四样属于客户端；
本计划唯一要做的是**现在就把接口的形状定对**，免得将来再拆一次：

| 门 | 今天的样子 | 将来 |
| --- | --- | --- |
| `terrain.structure_hit(p0, p1, out t, out point, out structure)` | 逐 tile DDA：`tiles[k]` 是 `TILE_BUILDING` 就 slab 测 `walls[k]`，是树就试 `_trunks` | 同一道门，内部换成 chunk 索引；**`FirstHit.Structures` 一行不改** |
| `first_hit_kind` | 从第一天就分 `BUILDING` / `TRUNK` | `Impact` 加一个 "扣结构血" 的分支（加 case，不加系统） |
| `first_hit_unit`（I32） | 结构命中写 `-1` | 装 `building_id`（每 tile 一个 U16，是唯一必须新增的地图数据） |
| 变化的下发路径 | `Terrain.plant` 已经会重画一片 tile 并把 patch 交给客户端 | 同一个 patch，payload 加一段墙高（今天的 patch 只有 tile 类型） |

因此本计划额外要求一条：**`FirstHit.Structures` 里不许出现直接读 `walls` / `tiles` 位图的
代码**，它只能调 `terrain.structure_hit`。写进 §12 的判据。

顺带记下两个将来会咬人的事实（现在不修）：`plant` 改一片 tile 后调的是**整张图**的
`_build_cull()`（1e6 tile）并清空所有 `trunk_cache`/`canopy_cache`；车体能不能站在一个 tile 上
读的是 **tile 类型**（`blocked` 的 `_BLOCKING_LUT`），而子弹停不停读的是 `walls[k]`——"摧毁"要
同时改这两个东西，这是那个功能自己要先想清楚的事。

## 8. 分三步落地

### Step 1 —— `first_hit_record` + 四个 flight + `Flights.Expire` + `Impact`（行为不变）

改：`Components`（`PROJ_COMPS` 增六个字段、`MISSILE_COMPS` 收窄、新增 `SEEKER_COMPS` 与六个
`register`）、`Defs.KIND_TAGS` 与 `expire_burst`、`World.spawn_round` 与 `Sweep.Spheres` 系统、
`Systems/Sweep.cs`、`Systems/Flights.cs`、`Systems/Impact.cs`；`Projectiles.run` 的 pass 2 收成
一个临时系统 `first_hit_all`（整体搬过去，仍用一个 `best_t`），先把管线跑通。

验：
```bash
./dotnet.sh build                       # 0 警告
./dotnet.sh run -- --check hit          # 每种武器 vs 一辆停着的 APC：命中/伤害逐行不变
./dotnet.sh run -- --check range --minutes 2
MECH_PERF=1 ./dotnet.sh run -- --check battle --minutes 12   # 1.3 ms/tick 的量级不劣化
./dotnet.sh run -- --check intercept                        # 34/36 不劣化
```

不设 `MECH_SEED` 时同一份构建两次运行本来就不同（随机流），所以验的是**量级**与
`ecs.validate()`，不是逐位对齐。设 `MECH_SEED=2024` 单线程可复现，可以拿它做改动前后的
逐行对照——但 flight 系统的分流会改变抽取顺序吗？见 §9.4（结论：不会，flight 与 Seeker 都
不抽随机数）。

### Step 2 —— `Proximity` → `FirstHit.Fuse`（这一步修正语义）

改：删 `Systems/Proximity.cs`，逻辑进 `FirstHit.Fuse`，出口从"自己 `_impact` + destroy"换成
"写记录（`first_hit_kind = FUSE`，带 `first_hit_gap`）"；`Impact` 里 `FUSE` 分支做
`splash`（仅当目标不是 `person`）+ `detonate(skip: 目标)`。**保留 `mech.prox` 这个 logger 名与
日志原文**：`--check intercept` 是把这行从服务器日志里读出来的。

验：`--check intercept`（34/36 是预期会变好的数字，单独记录），`--check range --minutes 2` 的
烟幕 A/B。

### Step 3 —— 碰撞拆成三步（行为不变）

改：`first_hit_all` → `FirstHit.Ground` + `FirstHit.Structures` + `FirstHit.Units` 三个系统，
外加 `Terrain` 侧的一道新门：

```csharp
// 逐个 tile 走这条腿；建筑测 slab、树测圆柱；返回最早结构的 t、点、tile 类型与身份
// （身份今天恒为 -1，预留给 `building_id`；`tile` 让调用者把墙面和树干记成两种 kind）
public bool structure_hit(Vec3 p0, Vec3 p1, out float t, out Vec3 point,
                          out byte tile, out int structure);
```

同时改：`ground_hits_many` 的采样从 `_sample`（高度 + 墙）换成 `height_at`（只算高度场），
`start_hit` 随之只表示"枪口在地面以下"（结构在 t=0 的命中由 `structure_hit` 覆盖，更准）；
`pr` 的两个常数进各自的系统；`aim_spheres` 的注释更新。

删（§7.1 那张表）：`Terrain.cs:737` 的 `walk` 循环、`Terrain.ground_hit`、`_march` 的
`trunks` 参数与 `:967` 那次 trunk 测试、`_march` 的 `At` 枚举与 `obstacle_at`、
`sample_points`。

验：同 Step 1 的全部，外加：
```bash
MECH_PERF=1 ./dotnet.sh run -- --check battle --minutes 12   # 最热循环少一个分支，应不劣化
./dotnet.sh run -- --check range --minutes 2                 # 用 debug_trees 造一片林子打一轮
./dotnet.sh run -- --check diag --minutes 2                  # 弹在树线/城镇里怎么落
```
另外单独记一次"一发打向楼面"的落点：这是两遍查询里最容易漂的一处。今天的 8 m 点采样只能
落在采样点上（缓变的地面无所谓，一堵墙不是），逐 tile slab 解的是墙面本身，所以爆点会在
一米之内移动、而且更准。这属于"同一发弹打在同一栋楼上"的可接受差异，量一次记进 §13。

## 9. 坑与前置确认

1. **扣下球表的时刻。** `Sweep.Spheres` 必须在 `movement` 之后（单位 `pos` 是 tick 末、
   `prev = pos - vel*dt`）、在 flights 之前（导弹 `pos` 是 tick 初、`disp = vel*dt`）。这正是
   今天 `sphere_table` 建在 `run` 顶部的语义，搬过去之后由谁建、什么时候建才是确定的。
2. **`aim_spheres` 不动。** `weapons` 在 `movement` 之前，仍然拿 premove 表（`row_ok` +
   `_sph_premove`），`Sweep.Spheres` 随后重建。机制原样，只是多了一句注释。
3. **快照行序会变。** `kp` 来自 `query("proj_kind")`，现在按 archetype 块序排列。客户端按 id
   建 map，无碍；`Harness/Tiers`、`--check net` 的计数要跑一遍核对。
4. **rng。** 只有 `Weapons.fire` 和 `Ai` 抽随机数（`Weapons.cs:280,284,328`、`Ai.cs:128,904,909`），
   `Core/Flight.cs` 与 `Core/Seeker.cs` 一个都不抽。所以按 kind 分组遍历**不改变**随机序列——
   这一条要在动手前用一次 `MECH_SEED` 对照确认，确认之后这次重构才敢说不改变战局。
5. **`tgt_mem` 从火箭/SACLOS 上消失之后**，`Ai._missiles`（`Ai.cs:241`）用 `View.get` 读会拿到
   注册默认 0；`Ai.cs:437-445` 的 `mine` 对两种弹都算、只有 `kind == 3` 时消费，所以是惰性的。
   动手时加一句注释说明这一点，别再让它承担任何语义。
6. **`Modes/Range/Record.cs:164`** 读 `tgt_mem`/`tgt_vel`：它只对 `top` 弹取，`_p` 走 `View`
   默认值，不用改。
7. **结构命中的爆点会移动一米以内。** 今天树瓦片的腿是"地面+树干"一起由标量 `ground_hit` 重解，
   建筑是 8 m 点采样里的 `height + wall`；拆开后地面由批扫解、结构由 `structure_hit` 的逐 tile
   slab/圆柱解。地面那半用的是同一个 `height_at`，结构那半是解析的——**这是变准，不是漂移**，
   但要量出来记进 §13，别让"更准"变成"说不清"。
8. **`ground_hits_many` 的 `start_hit` 语义变窄。** 今天它表示"腿的起点已经在 ground+wall 里"；
   只算高度场之后它只表示"枪口在地面以下"。结构在 t=0 的命中由 `structure_hit` 覆盖——更准，
   但那段注释要跟着改，否则下一个人会以为建筑的覆盖丢了。
9. **`walls` 是米制 byte，`blocked` 读的是 tile 类型。** 两个不同的字段：`wall_at` 决定子弹停
   在哪，`_BLOCKING_LUT[tiles[k]]` 决定车能不能站。这次重构不写任何一个，但 `structure_hit`
   里两种读法会同时出现，别把它们的语义搞混。
10. **`plant` 的整图重建不要照抄。** 它改一片 tile 后调整张图的 `_build_cull()`（1e6 tile）并清空
    `trunk_cache`/`canopy_cache`。这次重构不碰它；将来做可摧毁时那里要换成脏块增量重建，
    在代码旁留一行注释指向 §7.5。
11. **镜像要跟着改注释，不能改数。** `Core/Ballistics.cs:1-9` 与 `frontend/src/map.js:595` 都写着
    "mirrors the integrator in `Systems/Projectiles.cs`"——Step 1 之后积分器在
    `Systems/Flights.cs`，路径和注释要更新；四条推力/重力曲线的**数值**一个都不许动。
12. **`Core/Combat.cs:8`** 的注释写着 `apply <- hit <- Projectiles._impact`，改成 `Impact`。
13. **`MECH_PERF` 是环境变量，不是参数。** `--check battle --perf` 会被参数解析拒绝
    （`Program.cs` 的 `default` 分支）；perf 版是 `MECH_PERF=1 ./dotnet.sh run -- --check battle`。

## 10. 文档同步

- `AGENTS.md` 要改的条目：`Systems/Projectiles.cs` 那几条（"The geometry is batched, the bodies
  are not"、"A missile is a target"、"The hit-sphere table carries motion"、"The fuse settles the
  round it fused on"）、`Proximity.cs` 一条、`Core/Combat.cs` 一条、管线那段（`Core/World.cs`
  的 `_build_pipeline` 顺序），新增一条 `first_hit_record` 的不变量（用 §4.3 的四条正面陈述）。
- `AGENTS.md` 的 Terrain 一节：`_march` 那条"四个系统共用"要改成实际剩下的使用者（`los`），
  并记下 `ground_hits_many` 只算高度场、结构与树走 `structure_hit`。
- 架构树里的 `Systems/` 目录注释：`Projectiles.cs` / `Proximity.cs` 换成
  `Sweep.cs` / `Flights.cs` / `FirstHit.cs` / `Impact.cs`。
- `doc/product/` **不加条目**，`MechServer.csproj` 的 `<Version>` **不 bump**：Step 1 与 3 没有
  任何玩家可见变化；Step 2 只让引信更可靠（同一发弹从"被撞地吃掉"变成"击落来袭弹"），属于
  既有规则的正确实现，不构成新规则。若 Step 2 实测出现玩家可感知的变化（拦截率明显上升），
  再在 `doc/product/weapons.md` 与 `CHANGELOG.md` 记一条。

## 11. 提交划分

三次提交，一次一步，每步都能单独回滚：

1. `Step 1`：`Components` / `Defs` / `World` / `Systems/{Sweep,Flights,Impact}.cs` + AGENTS 对应条目。
2. `Step 2`：`FirstHit.Fuse`，`Systems/Proximity.cs` 删除。
3. `Step 3`：三个 gatherer + `Terrain.structure_hit` + `ground_hits_many` 改成只算高度场，
   并删掉 §7.1 那张表里的五样东西。

## 12. 完成判据

1. `grep -rn "proj_kind" --include=*.cs .` 只剩：`Components` 的注册与成员声明、`Weapons.fire`
   的 1 处写入、`Netcode` 两处（跨域分级 + 上线那一字节）、`Ai` 1 处、`Tiers` 1 处、
   `World.projectiles()` 1 处查询——**没有任何一处用它做分支**。
2. `grep -rn first_hit` 能一次列出全部写者与唯一的读者。
3. `Systems/Projectiles.cs` 与 `Systems/Proximity.cs` 不存在；`best_t` 这个局部变量不存在。
4. 每发弹的终点只由 `if (t < first_hit_t)` 决定；`detonate` 只有 `Impact` 一个调用者。
5. `FirstHit.Structures` 里没有任何直接读 `walls` / `tiles` 位图的代码，只有一次
   `terrain.structure_hit`；`structure_hit` 的签名已经带 `out int structure`（今天恒为 -1）。
6. `Terrain` 里 `ground_hit`、`sample_points`、`obstacle_at`、`_march(trunks:)` 与 `At` 枚举
   全部不存在；`wall_at`、`_trunk_hit`、`trunk_hit` 仍在（它们是 `structure_hit` 的两半）。
7. 数值：`--check hit` 逐行不变（`MECH_SEED` 对照）；`--check intercept` 不劣于 34/36；
   `MECH_PERF=1 --check battle --minutes 12` 的 ms/tick 不劣化超过 10%；`ecs.validate()` 通过。
8. 每 tick 的数组分配次数少于今天（用 `MECH_PROBE=1` 或一次临时的 `GC.GetAllocatedBytesForCurrentThread` 对照）。

## 13. 落地记录

已按本计划完成（Step 1–3 一次执行，未提交）。

| 验收项 | 结果 |
| --- | --- |
| `./dotnet.sh build` | 0 警告 0 错误 |
| `--check hit`（`MECH_SEED=2024`，改动前后逐行对照） | **逐行相同**（8 行） |
| `MECH_PERF=1 --check battle --minutes 12` | 改动前 1.50 ms/tick / 863 实体；改动后 **1.44–1.51 ms/tick**（两次运行）/ 608–681 实体。战场本身不同，比较的是每 tick 造价 |
| `--check intercept` | 改动前 14/32 击落；改动后 15/32、14/32（两次运行）。该检查同一份构建两次运行结果就不同（AI 与逐发散布），只比量级 |
| `--check range --minutes 2` | `ecs.validate() ok`，20 发、19 存活，量级不变 |
| `--check net --minutes 3` | 正常输出，`ecs.validate() ok` |
| `--check diag --minutes 2` | 正常 |
| 打向楼面的爆点 | 未单独量：`--check hit` 的靶场在开阔地，楼面差异只在城镇里出现 |
| `ecs.validate()` | 每个 check 末尾都通过 |
| 代码量 | `git diff --stat`：12 个文件，+390 / −986（`Projectiles.cs` 649 + `Proximity.cs` 156 消失，换成 366 + 317 + 280 + 107 行的四个文件，净减约 600 行） |

### 与计划的偏差（五处，都是执行中查出来的）

1. **`structure_hit` 多了一个 `out byte tile`。** 调用者要把"打在墙面上"和"打在树干上"记成
   两个不同的 `first_hit_kind`（`BUILDING` / `TRUNK`），而 `Structure` 那个 id 今天恒为 -1。
   两个 out 都比让 `Terrain` 认识 `Defs.HIT_*` 干净。
2. **`ground_hits_many` 必须自己保住"图外即实体"这条规则。** 旧代码里它免费得到了这件事：
   每个采样点走 `_sample`，而 `_walls_arr` 在图外返回 `1e6`。改成只算 `height_at` 之后，
   `height_at` 在图外夹到边缘高度，于是一发从地图外发射的弹不再原地炸开——`--check hit` 的
   `saclos@1600` 正好把射手摆在 x=4200（4 km 靶场之外），编号对不上就是这么来的。修法是把
   "图外"显式写进 `start_hit` 与 `blocked` 两处（`xs < 0 || … || ys <= height_at`）。
3. **`Sweep.closest` 返回的是距离，不是间隙。** 先写成 `gap < pr` 时
   `saclos@1600` 的结局变了：`dd < radii + pr` 与 `dd - radii < pr` 在最后一位浮点上不等，
   而一发掠过的弹正好卡在那儿。两个调用者各减各的半径（单位用 `pr`，引信用 `prox`），
   比较式与改动前逐字相同。
4. **`View.get(eid, c)` 的第二个参数是"列"，不是默认值。** `spawn_round` 里
   `e.iv("owner").get(src, -1)` 在 tick 6012 抛 `IndexOutOfRange`（那一发的射手正好在行 0），
   而在别的行上它会静默读到**上一行**的 id。正确的是 `.get(src, 0)`。
5. **`--check hit` 不是逐次可复现的。** 战斗随机流在 `MECH_SEED` 未设时用
   `Environment.TickCount` 播种，所以两次运行的散布不同——它的**汇总行**常常一样，但那不是
   可依赖的基线。改动前后的对照一律用 `MECH_SEED=2024` 做，结论才是真的。

### 落到别处的同步

- `AGENTS.md`：架构树的 `Systems/` 一段、管线那一条、Terrain 的 `_march` 与 `ground_hits_many`
  两条、近炸那一条、导弹实体那一条，新增三条（`first_hit_record`、kind 是标签、寿命是 t=0 的
  接触）。`Core/Combat.cs`、`Core/Ballistics.cs`、`Core/Flight.cs`、`Core/Terrain.cs`、
  `Systems/Weapons.cs`、`Harness/InterceptCheck.cs` 里指向旧文件名的注释一并改掉。
- `doc/product/` **没有条目**，`<Version>` **没有 bump**：没有玩家可见的变化。Step 2 让引信在
  "同一 tick 里本来会撞地"的场合更可靠，`--check intercept` 的量级没有变（14 → 15，而该检查
  自身逐次就有 ±2 的抖动）。
