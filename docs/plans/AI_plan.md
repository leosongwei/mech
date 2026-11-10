# AI 重构计划：`Systems/Ai.cs` → `Systems/AI/`

> 目标：把 914 行、一个 `run` 里塞满分支的 AI，拆成「ECS 化 + 多系统 + 策略层」的架子。
> 参照物是 `Systems/Flights.cs`：**每个系统在自己的文件里声明自己需要的列**（`Ecs/Columns.cs`
> 的 `Columns<TRow>` / `IRow<TCols>`），身份由标签组件决定，共享代码里没有 `etype` 分支。
>
> 本工作交付的是**可扩展的骨架**：今天的行为逐条搬过去、逐条验证不变；`attack_flank` /
> `capture` / `withdraw` / `resupply` 这些新战术**只留接口和词表**，不实现。

---

## 1. 现状

`Systems/Ai.cs`（914 行）只有一个 `run(world, dt, hulls)`，把「感知、威胁判定、决策、
瞄准、开火、寻路、平民游荡」全按行挤在一个循环里，靠 `b.has(...)` 和 `etype` 分叉：

| 今天 `run` 里的东西 | 位置 | 问题 |
| --- | --- | --- |
| `owner >= 0` 跳过（玩家） | 每行一次 `i("owner")` 读 | 身份靠运行时判定，不是组件 |
| `!has("combat")` → `_civ` | 每行一次 | 平民和战斗单位混在一个循环 |
| 视线眼位：机甲用 `sight_eye`，其他用 `pos + h*0.75` | `etype` 分支 | 平台差异靠类型切换 |
| `ai_timer` → `_acquire`（网格 + 通视） | 内联 | 感知与决策耦合 |
| `_missiles` + `_missile_step`（雷达、引信臂、威胁选择、拦截瞄准） | 内联，且在交战之前 | 导弹防御和地面战共享一个 `trigger` 写者 |
| 交战判定（`dist < ai_range` + `_sees`） | 内联 | 决策散落 |
| `spot_*` 记忆 + `_suppress` | 内联 | 同上 |
| `_engage`（最慢弹速解提前量） | 内联 | 决策与火控耦合 |
| `_aim_and_fire`：机甲 / APC / 步兵三分支 | `etype` 分支 | 平台差异靠类型切换 |
| `moving` → `_advance` / `_advance_route` / 交给突击队的 `vel_cmd` | 内联 | 机动与决策耦合 |
| `_advance`（兵线折线 + 撞墙扇形） | 静态方法 | 步行 |
| `_advance_route` / `_corner_speed` / `_route_speed` / `_arrive_radius` / `_passed` | 静态方法 | 轮式 |
| `_civ`（平民游荡） | 内联 | 与战斗 AI 无关 |

**为什么必须拆**：`docs/plans/roadmap.md` 里排着「AI 行为树」「野战部队（UGV / 步兵 /
无人机发射车 / 侦查车）」。每加一种单位、一种战术，今天都要改这一个共享循环——
一个 5000 单位的战场里，那意味着每个新兵种都在给所有单位付一次 `has()` 和 `etype` 判断，
而且新战术的代码没有地方放。

---

## 2. 目标结构

```
Systems/AI/
  Tags.cs            标签与状态词表的唯一来源（AiTags / AiState 常量）
  Crew.cs            共享基础 schema `CrewColumns`/`CrewRow` + 小工具（眼位、臂查表、触发位）
  Pipeline.cs        模式注册用的系统列表：battle() / range() / defence()
  Sense.cs           Sense.Acquire       —— ai_timer 到点、网格查询、通视，写 target
  Threats.cs         ThreatTable + Threats.build —— 本 tick 在飞的制导弹，world.threats
  Intercept.cs       Intercept.select / Intercept.lay / Intercept.run —— 导弹防御三段
  Policy.cs          Situation（一次战况快照）+ 策略层共用件
  Engage.cs          火控共用件：提前量解、臂的射程、触发位、半自动上升沿
  EngageMech.cs      EngageMech.run    —— mech_ai
  EngageVehicle.cs   EngageVehicle.run —— 车（vehicle）
  EngageFoot.cs      EngageFoot.run    —— 步兵（foot_ai）
  March.cs           March.run         —— 兵线机动（步行折线 / 轮式路线，原型内分派）
  Wander.cs          Wander.run        —— 平民游荡
  Strategies/
    Front.cs         Front.run         —— 兵线机甲的战术状态机（本工作的架子重点）
    Raid.cs          Raid.run          —— 突击队（由 `Modes/Battle/Raids.cs` 迁入）
    RaidPath.cs      128 m / 16 m 两级寻路（随 Raid.cs 一起迁入）
```

`Systems/Ai.cs` 在本工作结束时**删除**。

命名空间：`Mech.Server.Systems.AI`。每个 `run` 的签名仍是 `void run(World world, float dt)`，
`Pipeline` 把它们按顺序拼成 `List<Action<World, float>>`，也就是 `World._build_pipeline`
从模式手里拿的那个东西——**模式注册什么，AI 就跑什么**，和今天 `pre_systems()` 的接口一致。

---

## 3. 身份：标签与组件

`Ecs/Components.cs` 的注释已经写死了这条原则：**组件集就是实体的类型，在唯一的那次
`ECS.create(comps)` 里决定**。AI 今天用运行时判定（`owner`、`has`、`etype`）绕开了它，
现在改成标签。全部改在 `World.spawn_unit`——它已经在那里拼 `unit` / `combat` / `vehicle`
这些标签，以及 `raider` / `range_drone` 这类「任务」标签。

### 3.1 新增标签

| 标签 | 含义 | 谁带 | 谁查询 |
| --- | --- | --- | --- |
| `ai_crew` | 这台车由 AI 机组驾驶 | 所有 AI 单位（`spawn_unit(crew: true)`，默认） | `Sense` / `Intercept` 全部 |
| `mech_ai` | **机甲机组**：视线在车顶、两条臂、车体随动 | 所有机甲机组（兵线 / 突击队 / 靶场无人机 / 防御哨） | `EngageMech` / `Intercept.lay_mech` |
| `foot_ai` | 步兵机组：整个人转身瞄 | 步枪兵 / 反坦克兵机组 | `EngageFoot` |
| `crew_engage` | 这组人会打地面目标 | 兵线机组、突击队、靶场无人机 | `Sense.Acquire` / `Engage.*` |
| `front_mission` | 任务：沿兵线推进 | 战斗模式的兵线部队、靶场的 `debug_spawn` 无人机 | `Front.run` / `March.run` |

复用的既有标签：`vehicle`（APC，轮式）、`person`（人）、`raider`（突击队任务）、
`range_drone`（靶场控制台刷的车）、`home_x`（平民）。

`mech_ai` 就是设计里说的 **MECH_AI**：兵线上的机甲和抄后路的机甲都带它，所以
`EngageMech` / `Intercept.lay_mech` / `March` 的机甲路径对两者是同一份代码——
这正是「共用绝大部分 systems」落地的方式。

### 3.2 `spawn_unit` 的新参数

```csharp
public int spawn_unit(int etype, int faction, float x, float z, int owner = -1,
                      int[]? weapons = null, float hp_scale = 1f,
                      int lane = -1, string[]? extra = null, bool crew = true)
```

`crew: true`（默认）时按类型补标签，全部在这里、一次性、在 `e.create(comps)` 之前：

```csharp
if (crew)
{
    comps.Add(AiTags.CREW);                       // ai_crew
    if (arms) comps.Add(AiTags.MECH);             // mech_ai
    else if (d.tags.Contains("person")) comps.Add(AiTags.FOOT);   // foot_ai
}
// 任务标签由模式给（它才知道这一局这个兵是干什么的）：
//   extra: { "front_mission", "crew_engage" }   兵线部队 / 靶场无人机
//   extra: { "raider", "crew_engage" }           突击队（Raids 已经在传 raider）
```

传 `crew: false` 的地方（今天的**玩偶**，必须保持被动）：

* `Modes/Range/Targets.cs` —— 12 个 APC 靶
* `Harness/InterceptCheck.cs` 的射击机甲（注释里就叫它 puppet）
* `World.spawn_mech`（玩家机甲本来就走 `owner >= 0`，多传一个 `crew: false` 把意图写明）

`Modes/Battle/Waves.cs::_spawn`、`Modes/Battle/BattleMode.cs` 的基地机甲、`Raids.cs`、
`Net/Server.cs` 的 `debug_spawn`（靶场）改成传 `extra: { "front_mission", "crew_engage" }`。

### 3.3 新增/改名的组件

| 组件 | 类型 | 谁写 | 谁读 | 说明 |
| --- | --- | --- | --- | --- |
| `ai_move` | U8 | `Front.run` / `Raid.run` | `March.run` | 本 tick 想不想动（1 = 走）。脱离「移动」这个副作用，成为策略的输出 |
| `ai_cover` | U8 | `Intercept.select` | `Front.run` / `Engage.*` | 有弹在朝我来：本 tick 的枪归拦截系统 |
| `ai_threat` | I | `Intercept.select` | `Intercept.lay` | 我瞄的那发弹的实体 id（-1 = 没有） |
| `ai_goal` | U8 | 策略 | 策略 / `March.run` | 目标 id（城镇索引，255 = 敌方指挥部）。由 `raid_goal` 改名而来，两种策略共用一张表 |

沿用今天的 `target`、`ai_state`、`ai_timer`、`spot_x/y/z`、`spot_t`、`path_i`、`lane`。
它们在包里属于每个单位（`UNIT_COMPS`），本轮**不动**（见 §10「可选的收尾」）。

---

## 4. Columns：每个系统声明自己的列

照 `Systems/Flights.cs` 的写法，一个文件上半部分是 schema、下半部分是系统，
「这个 pass 读什么」和「读它的 pass」在同一个屏幕上。

```csharp
// Systems/AI/Crew.cs
public class CrewColumns : Columns<CrewRow>
{
    public float[] pos = null!, vel = null!, yaw = null!, pitch = null!;
    public float[] sight_yaw = null!, sight_pitch = null!, turret_yaw = null!;
    public byte[] etype = null!, faction = null!, weapon = null!, trigger = null!;
    public byte[] state = null!, move = null!, cover = null!;
    public int[] target = null!, threat = null!, mag = null!;
    public float[] hp = null!, hpmax = null!;

    internal static readonly string[] names =
    {
        "pos", "vel", "yaw", "pitch", "sight_yaw", "sight_pitch", "turret_yaw",
        "etype", "faction", "weapon", "trigger", "ai_state", "ai_move", "ai_cover",
        "target", "ai_threat", "mag", "hp", "hpmax",
    };

    // 查询列表 = 基础列 + 标签：Cat 是 Components 里已有的那个
    public static readonly string[] OnCrew = Components.Cat(names, AiTags.CREW);

    public CrewColumns(Group g) : base(g) { bind(g); }
    public override string[] Names => names;
    protected override void bind(Group g) { /* 一行一个 g.f/g.u8/g.i */ }
    public override CrewRow row(int i) => new CrewRow(this, i);
}
```

要点，全部是这套机制既有的语义：

* **`groups(names)` 是超集匹配**：列表里有的组件它都有，多出来的不管。所以 `ai_crew`
  会把突击队、兵线、靶场无人机一起捞上来——**任务怎么分，靠谁把任务标签写进自己的
  `Names`**（`Strategies/Front.cs` 查 `front_mission`，`Strategies/Raid.cs` 查 `raider`）。
* **扩列用 `Components.Cat`**，与 `Flights.RoundColumns.OnBullet` / `TopColumns` 同一手法：
  ```csharp
  public sealed class MechColumns : CrewColumns         // EngageMech
  {
      public float[] arm_hp = null!;                    // 只有机甲机组有这条缓冲
      private static readonly string[] mech_names = Components.Cat(names, "arm_hp", "ammo");
      public static readonly string[] OnMech = Components.Cat(mech_names, AiTags.MECH);
      ...
  }
  ```
  于是 `_aim_and_fire` 里那句 `b.has("arm_hp") ? b.f("arm_hp") : null`（每行一个 HashSet 查）
  **消失**：`EngageMech` 的 schema 里就是有 `arm_hp`，`EngageVehicle` / `EngageFoot` 的
  里就是没有。
* `Columns` 基类在构造时对 `Names` 做一次注册校验（名字写错会抛），每条 schema 一次。
* **原型内层级分派**：`March.run` 需要「步行折线」和「轮式路线」两套互斥的算法，它们
  由阵营内的**组件**决定（`vehicle`）。取 `groups(MarchColumns.names)` 之后，用
  `g.has("vehicle")` **每原型判一次**、而不是每行判一次：

  ```csharp
  foreach (var g in e.groups(MarchColumns.names))
  {
      var c = new MarchColumns(g);
      if (g.has("vehicle")) _wheels(world, c); else _legs(world, c);
  }
  ```
  这也是 `AGENTS.md` 说的「*谁*是一个组件」：一个 pass 只支付它能处理的那种行。

---

## 5. 流水线

`Systems/AI/Pipeline.cs`：

```csharp
public static class Pipeline
{
    public static List<Action<World, float>> battle() => new()
    {
        Threats.build,            // 本 tick 在飞的制导弹（一次，压缩成数组）
        Intercept.select,         // 谁在朝我来：ai_cover / ai_threat
        Sense.Acquire,            // ai_timer 到点：换目标
        Policy.Front.run,         // 战术状态机：ai_state / ai_move / spot_*
        Policy.Raid.run,          // 突击队任务：ai_state / ai_goal / vel_cmd
        Intercept.lay,            // 拦截：把视线压到提前量上，写 trigger
        EngageMech.run,           // 地面战：三种平台，各自一个 schema
        EngageVehicle.run,
        EngageFoot.run,
        March.run,                // 机动：步行折线 / 轮式路线，读 ai_move
        Wander.run,               // 平民
    };

    public static List<Action<World, float>> range() => /* battle() 去掉 Policy.Raid.run */;
    public static List<Action<World, float>> defence() => new()
    {
        Threats.build, Intercept.select, Intercept.lay,      // 只有导弹防御（靶场哨位）
    };
}
```

顺序里每一处都是语义：

| 顺序 | 为什么必须在这个位置 |
| --- | --- |
| `Threats.build` 最先 | 后面两个拦截系统共用一张表；一张表一个 tick，挂在 `world.threats` 上，用 `_threat_tick` 守卫（和 `world._sph` / `_sph_tick` 同一个模式） |
| `Intercept.select` 在感知之前 | `ai_cover` 是本 tick 的**最高优先级**：`Policy.Front` 要能读到它（今天就有的行为：被弹追着打的时候不理会地面目标、继续开） |
| `Sense.Acquire` 在策略之前 | 策略读的是「已经定好的目标」 |
| `Policy` 在两个拦截/交战之前 | 策略只写 `ai_state` / `ai_move` / `ai_goal`，不碰枪也不碰车；它要判断的一切（目标是否还在、通视、距离）都在这一段里做完 |
| `Intercept.lay` 在 `Engage` **之前** | 触发器归属靠顺序而不是靠分支：拦截先无条件写 `trigger`（没威胁写 0），地面战随后写，而 `ai_cover != 0` 的行被 `Engage` 跳过——于是「有弹追我」的机组保留拦截的扳机，「没弹」的机组拿到地面战的扳机，「两组都不参与」（靶场哨位）的机组留在 0 |
| `Engage` 在机动之前 | 和今天一样：先决定打不打，再由 `moving` 决定动不动（现在是 `ai_move`） |
| `March` 最后 | 写 `vel_cmd`，`Movement` 下一个阶段读它 |

`Intercept.run(world, dt)` 保留为「一个模式只想要导弹防御」的入口（靶场的
`Interceptor.cs` 今天调 `Ai.intercept`），实现就是 `Pipeline.defence()` 里那三段。

---

## 6. 策略层（本工作的架子重点）

### 6.1 状态词表：`Systems/AI/Tags.cs` 里的 `AiState`

`ai_state` 这个组件今天已经存在（突击队用它做 MARCH / ASSAULT / HOLD），本工作把它
提升为**所有 AI 机组共用的一个词表**，并补齐将来要用的名字：

| 状态 | 含义 | 谁写 | 今天是否实现 | 实现它需要的数据（都已在世界里） |
| --- | --- | --- | --- | --- |
| `ADVANCE` | 沿任务路线前进 | Front / Raid | ✅ 兵线 | `path_i`、`lane`、`ai_goal` |
| `ENGAGE` | 目标在射程内且通视：边打边走（远）/ 停下打（近） | Front | ✅ | `target`、`dist`、`ai_move` |
| `SUPPRESS` | 丢目标：打最后看到的那一点 | Front | ✅ | `spot_x/y/z`、`spot_t` |
| `MARCH` | 突击队：走自己的路线 | Raid | ✅ | `raid_paths` |
| `ASSAULT` | 突击队：站进占领环 | Raid | ✅ | `Waves.CAPTURE_RADIUS` |
| `HOLD` | 突击队：这一城是我们的，守住 | Raid | ✅ | `raid_t`、`towns` |
| `ATTACK_FRONT` | 正面压上去（= 今天的 ADVANCE，语义收紧） | Front | 结构就位，本工作按 ADVANCE 走 | 兵线折线 |
| `ATTACK_FLANK` | 绕侧：给同一段路一个横向偏移 | Front（将来） | ❌ 只留 case | `world.lanes` + 偏移量；`March.run` 已经吃「一条点列」，侧翼就是另一条点列 |
| `CAPTURE` | 站进最近的敌方城镇环 | Front（将来） | ❌ 只留 case | `world.towns`；站环的逻辑从 `Raid` 抽成共用件 |
| `WITHDRAW` | 血太少：往回撤到友方城镇 | Front（将来） | ❌ 只留 case | `hp / hpmax`、`path_i` 往回递减 |
| `RESUPPLY` | 弹打空：回基地补 | Front（将来） | ❌ 只留 case | `mag`、`ammo`、最近的己方基地；`Shop` / `Rearm` 目前只服务玩家 |

**规则**：新战术 = 一个新的 `AiState` 常量 + 策略里一个新的 `case` + **执行它的那个系统
里的一段**（机动在 `March`，占领在将来抽出的 `Hold.run`）。共享系统（感知、火控、
威胁、机动）一行都不用改。

### 6.2 战况快照：`Policy.Situation`

策略只读一个结构，不自己到处抓 buffer：

```csharp
public struct Situation
{
    public int eid, row, faction, etype;
    public Vec3 eye, pos;
    public float hp_frac;         // hp / hpmax
    public int mag_left;          // 两臂剩余弹
    public int target;            // 当前目标 id（-1 = 没有）
    public float target_dist;
    public bool target_seen;      // 通视（Sight.clear，含烟）
    public bool covered;          // ai_cover：有弹在朝我来
    public bool raider;           // 任务
}

public static Situation sense(World world, in CrewColumns c, int i);   // Policy.cs
```

### 6.3 兵线策略的骨架：`Systems/AI/Strategies/Front.cs`

```csharp
public static class Front
{
    private static readonly string[] names = Components.Cat(CrewColumns.names, "front_mission");

    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(names))
        {
            var c = new FrontColumns(g);
            foreach (var r in c)
            {
                var s = Policy.sense(world, c, r.index);
                // 有弹朝我来：拦截系统拥有视线和扳机，机组继续开（今天的 `covering`）
                if (s.covered) { r.state = AiState.ADVANCE; r.move = 1; continue; }
                decide(world, c, r, s, dt);
            }
        }
    }

    /// 战术状态机：一 tick 一次，输出「干什么」(ai_state) 和「动不动」(ai_move)。
    private static void decide(World world, FrontColumns c, FrontRow r, in Situation s, float dt)
    {
        // ---- 目标在射程内且通视 -------------------------------------------
        // 后面三个 case 是空位：数据都在 Situation 里，实现它们的代码只写在这里
        // 和负责执行的那个系统里（机动 → March，占领 → 将来的 Hold.run）。
        //   WITHDRAW  : s.hp_frac < defs.AI_WITHDRAW_HP
        //   RESUPPLY  : s.mag_left == 0
        //   FLANK     : 本路正面城镇 contested，改走 `March` 的侧翼点列
        //   CAPTURE   : 最近的敌方城镇在 RAID_* 距离内
        switch (r.state)
        {
            case AiState.SUPPRESS: /* 丢目标：打最后看到的那一点，不走 */ break;
            case AiState.ENGAGE:   /* 远：边打边走；近：停下 */ break;
            default:               /* ADVANCE */ break;
        }
    }
}
```

今天的判定原样搬进去（值都不改）：`dist < d.ai_range && Sight.clear(...)` → `ENGAGE`，
`ai_move = dist > d.ai_range * 0.55f`；`spot_t > 0` → `SUPPRESS`（`ai_move = 0`，
`spot_t -= dt`）；其余 `ADVANCE`（`ai_move = 1`）。`spot_*` 的写入也在这一层
（`ENGAGE` 时刷新，`covered` 时不刷新——和今天一致）。

### 6.4 突击队策略 = 同一个架子的第二个实例

`Modes/Battle/Raids.cs` 整段迁到 `Systems/AI/Strategies/Raid.cs` + `RaidPath.cs`，
改动只有三处：

1. `STATE_MARCH/ASSAULT/HOLD` → `AiState.MARCH/ASSAULT/HOLD`（数值不变，0/1/2）。
2. `raid_goal` → `ai_goal`（改名，255 = 敌方基地）。
3. 它自己的 `_drive` 循环改成查 `raider` + `ai_crew` 的 schema，`_one` 里对
   `pos`/`vel_cmd`/`ai_state` 的访问改成走行结构。

**枪一律不碰**：今天注释里写的「guns stay `ai.run`'s」在新结构里就是「`Raid.run` 只写
`ai_state` / `ai_goal` / `vel_cmd`，`EngageMech` 照样按 `crew_engage` 把它的枪打了」。
寻路、逼停、`_rescue` 全部留在 `RaidPath.cs`，与 `March.run` 的轮式/步行代码并列而不是重复。

### 6.5 野战部队（roadmap 里的 UGV / 步兵 / 无人机发射车 / 侦查车）

新的「任务」= 一条新标签 + 一个新策略文件，其余全部复用：

```csharp
// Systems/AI/Strategies/Field.cs —— 侦查车：去几个点、看见就打、不占城
public static class Field
{
    private static readonly string[] names = Components.Cat(CrewColumns.names, "field_mission");
    public static void run(World world, float dt) { /* 和 Front 同构 */ }
}
```

* 一辆侦查车 = `Defs.UNIT_DEFS` 里一条新类型（`vehicle` 标签自动带上轮式机动）
  + 出生时 `crew: true` / `extra: { "crew_engage", "field_mission" }`。
  它立刻得到：感知、导弹防御、炮塔火控、轮式路线跟随。
* 一台 UGV 没有炮塔就**没有** `vehicle`：给它 `foot_ai` 或将来的一条新平台标签，
  只在 `Engage*` 里加一个文件。
* 一个新的任务（野战、护送、巡逻）= `Strategies/` 下一个文件 + `Pipeline` 里一行。

---

## 7. 一个系统长什么样（示例）

```csharp
// Systems/AI/EngageMech.cs —— mech_ai：视线在车顶、两臂、车体随动
namespace Mech.Server.Systems.AI;

public static class EngageMech
{
    public static void run(World world, float dt)
    {
        foreach (var g in world.ecs.groups(MechColumns.OnMech))
        {
            var c = new MechColumns(g);
            foreach (var r in c)
            {
                if (r.cover != 0) continue;                 // 拦截系统拥有这一 tick
                if (r.move == 0 && r.target < 0) { r.trigger = 0; continue; }
                Engage.lay_mech(world, c, r, dt);            // 提前量 + 摆视线 + 等炮膛
            }
        }
    }
}
```

`Engage.cs` 里放三种平台共用的那部分，也就是今天 `_engage` 的上半段与
`_aim_and_fire` 的规则头：`Engage.lead_aim(...)`（取**能打到目标的臂里最慢的平均弹速**，
`MathUtil.mean_speed`）、`Engage.arm_reach(...)`（`rocket`/`saclos` 2.2 km、其余 `ai_range`，
并和 `wd.range` 取小）、`Engage.semi_edge(...)`（半自动臂隔 tick 松扳机）、
`Engage.bore_gate(...)`（机甲的 `err < 0.14` 且 `|d pitch| < 0.02`）。

---

## 8. 分期实施

每一期都能编译、能跑、能用 `--check` 验证；任何一期出问题都可以单独回退。

| 期 | 内容 | 验证 |
| --- | --- | --- |
| **P0 标签与组件** | `Tags.cs`；`Components.register` 新组件；`spawn_unit(crew:)` 及各 spawn 点；`World.spawn_unit` 的组装（含 `extra`）；靶场 `Targets` / `InterceptCheck` 的 `crew: false`。`Systems/Ai.cs` 里把所有 `owner >= 0` 换成「有 `ai_crew`」——**行为完全等价**（两组集合相同） | `--check battle --minutes 3`、`--check range`、`--check intercept` |
| **P1 感知与威胁** | `Crew.cs` / `Sense.cs` / `Threats.cs` / `Intercept.cs`；`Ai.run` 里删掉 `_missiles`/`_threat`/`_missile_step`/`_acquire`/`_missiles` 表与 `ai_timer` 段；`Ai.intercept` → `Intercept.run` | `--check intercept`（36 发、34 中以上）、`--check battle --minutes 5` |
| **P2 交战** | `Engage.cs` + `EngageMech/Vehicle/Foot.cs`；`Ai.run` 里删掉 `_engage`/`_suppress`/`_aim_and_fire` | `--check range --minutes 2`（AI 机甲的枪打得中）、`--check battle` |
| **P3 机动** | `March.cs`（`_advance` + 全部轮式静态方法原样搬入）、`Wander.cs` | `--check diag --minutes 2`（几台 AI 车在干什么）、`--check battle`（占领推进节奏） |
| **P4 策略层** | `Policy.cs`、`Strategies/Front.cs`、`Pipeline.cs`；`Systems/Ai.cs` 只剩空壳 → 删除；`BattleMode.pre_systems` / `RangeMode.pre_systems` 改为 `Pipeline.*`；删除 `Modes/Range/TestAi.cs`（靶场无人机 = `front_mission` + `crew_engage`） | 全量 `--check battle --minutes 12`、`--check range`、`--check intercept`、`--check diag`、`--check net` |
| **P5 突击队迁入** | `Modes/Battle/Raids.cs` → `Systems/AI/Strategies/Raid.cs` + `RaidPath.cs`；`STATE_*` → `AiState`；`raid_goal` → `ai_goal` | `--check battle --minutes 12`（raid 周期仍然发生） |
| **P6 收尾** | 注释/文档：仓库里对 `Systems/Ai.cs` 的引用（`Weapons.cs`、`Defs.cs`、`BattleMode.cs`、`Interceptor.cs`、`Raids.cs`、`AGENTS.md`）；`docs/plans/roadmap.md` 的「AI代码ECS化」指向本文件 | 编译零警告 |

---

## 9. 验证与不变量

**基线（重构前先各跑一次，记下量级）**

```bash
./dotnet.sh run -- --check battle --minutes 12            # 每分钟普查曲线
MECH_PERF=1 ./dotnet.sh run -- --check battle --minutes 12 # 1.3 ms/tick 基线
./dotnet.sh run -- --check intercept                      # 34 / 36 基线
./dotnet.sh run -- --check range  --minutes 2
./dotnet.sh run -- --check diag   --minutes 2
./dotnet.sh run -- --check net --minutes 12 --from 2      # 线上字节数应该完全不变
```

**硬不变量**

1. **玩家的机甲永远不在任何 AI 查询的结果里**：它没有 `ai_crew`（结构上排除，
   而不是每行判一次 `owner`）。
2. **一个 tick 里 `trigger` 只有一个写者**：`Intercept.lay` 先写（并清 0），
   `Engage.*` 后写且跳过 `ai_cover != 0` 的行，两种平台的行集互斥（同 archetype 层）。
3. **一 tick 一张弹表**：`world.threats` 由 `_threat_tick` 守卫，`reset_match` 里复位。
4. **`vel_cmd` 只有一个写者**：任务策略（Raid / Front 将来的 `WITHDRAW` 之类）或 `March`，
   靠 `ai_move` 与标签分开，同一行不会被两处写。
5. `ecs.validate()` 每个 `--check` 都会跑，行指针越界会当场失败。

**判定标准**：`--check battle` 的火力/占领/存活量级不变（不是逐位相同——`--check` 的
RNG 是单线程可复现，但重构不应改变抽取顺序；若曲线明显不同，说明有系统换了遍历顺序，
先查 `groups()` 的原型顺序）；`--check net` 的 kB/s 应该**一位不动**（AI 的组件不上线）。

---

## 10. 风险与对策

| 风险 | 对策 |
| --- | --- |
| 标签拆分让 archetype 数量变多（`mech_player` / `mech_ai` / `mech_ai+raider` …） | 组合数有限（平台 3 × 任务 3），且每类单位都只会出现一次；`--check battle` 的 tick 时间是验收指标 |
| 顺序拆错导致「拦截和地面战抢扳机」 | 顺序表（§5）写死在一个地方 `Pipeline.cs`；`--check intercept` 是它的体检 |
| 突击队搬迁期间行为漂移 | P5 单独一期、只做搬家（常量同值、逻辑一字不改），`--check battle` 前后对比 raid 次数与死亡分布 |
| 靶场的「被动」依赖 `[crew:false]`，漏一处就变成「靶子会开枪」 | 三个 spawn 点写在一处清单（§3.2）；`--check range` 里靶场 unit 的击杀数应保持 0 |
| 弹表从 `query()` 改成 schema 后，`tgt_mem` 只有攻顶弹有 | 用两条 schema（`ThreatColumns` / `SeekerColumns : ThreatColumns`，后者多绑 `tgt_mem`），非导引弹的 `mem` 读 0——与今天 `FView.get` 填默认值完全等价 |
| `Ai.run(world, dt, hulls)` 的过滤参数被删掉，靶场 `TestAi` 依赖它 | 用标签替代：靶场无人机带 `front_mission` + `crew_engage`，靶场注册 `Pipeline.range()`，`TestAi.cs` 删除 |

---

## 11. 不在本工作范围内

* 新战术的具体实现（`ATTACK_FLANK` / `CAPTURE` / `WITHDRAW` / `RESUPPLY`）——只留词表、case 与注释。
* 行为树：策略层的接口（`Situation` 进、`AiState`/`ai_move` 出）就是将来换行为树的接缝。
* 玩家控制、`Movement`、`Gunnery`、`Weapons` 的任何改动。
* 把 `ai_*` 组件从 `UNIT_COMPS` 挪进只属于机组的 `CREW_COMPS`（可以让玩家机甲的行更窄）。
  这是 §9 之外的**可选收尾**：做完需要 `--check net` 逐字节核对，也牵扯 `spawn_unit`
  对 `ai_state = 0` 的初始化与 `Harness/Check.cs::diag` 的读法。
* 任何玩家可见的变化 —— 因此 `docs/product/` 不动，只需要按仓库约定把版本号第三位 +1。

---

## 12. 文档与版本

* 本文件：`docs/plans/AI_plan.md`。
* `docs/plans/roadmap.md`：「AI代码ECS化 / AI行为树」一行加一句「见 AI_plan.md」，
  本工作完成后移到 DONE。
* `AGENTS.md`：`Systems/` 那段注释里关于 `Systems/Ai.cs` 的描述（「AI crew fires the arms
  it has」「AI missile defence」等）改写为 `Systems/AI/` 的目录与流水线，并把 §3 的标签、
  §5 的顺序写成设计规则。
* 版本：`MechServer.csproj` 的 `<Version>` `1.17.2` → `1.17.3`（玩家看不到差异）。
* `docs/product/`：只读核对。若 P5 之后突击队的行为出现玩家可感的差异（本该没有），
  则按 `docs/product/CHANGELOG.md` 的格式补一条。
