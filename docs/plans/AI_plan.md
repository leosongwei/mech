# AI 重构计划：`Systems/Ai.cs` → `Systems/AI/` + 意图层

> **状态：已落地（2026-11-10，产品版本 1.18.0）。** 落地的文件与本文 §2 的目录一致。
> 验证（同一台机器，同一条命令，与重构前的基线并排）：
> `--check battle --minutes 12`：25/25 城镇、1.44 ms/tick（基线 1.3，+11%）；
> `--check intercept`：19/32 发被击落（基线 18/32）；
> `--check range --minutes 2`：投弹 20 发；`--check net`：45.0 kB/s（基线 53.7，
> 战场演化不同，线上格式一行未改）；`--check diag` 一切正常。
>
> **同轮（1.21.0）的更正**：本文提到的 `spot_x` / `spot_y` / `spot_z` / `spot_t` 已经不存在
> （`SPOT_MEMORY` 也随之删掉），换成班组的目标信念组件 `ai_tgt`（位置、速度、年龄、
> 类型），由 `Systems/AI/Sense.cs` 每 tick 写一次、`Engage` 只读它——见
> `AI_FCS_defect.md` §10.5。文中这些行号与字段名按"当时的实现"读。

> 目标：把 914 行、一个 `run` 里塞满分支的 AI，拆成「ECS 化 + 多系统 + 策略层」的架子，
> 并且把**「怎么开」还给机器**：AI 只表达意图（去哪、多快、打谁），路线跟随、弯道限速、
> 撞墙脱困这些操稳与驾驶的事搬进按底盘拆开的移动系统。
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
| `_advance`（兵线折线 + 撞墙扇形） | 静态方法 | 驾驶（该在移动层） |
| `_advance_route` / `_corner_speed` / `_route_speed` / `_arrive_radius` / `_passed` | 静态方法 | **操稳**：11 m 转弯半径的车在弯上开不到 100 km/h，这是物理包线，却是 AI 在操心 |
| `_civ`（平民游荡） | 内联 | 与战斗 AI 无关 |

**为什么必须拆**：`docs/plans/roadmap.md` 里排着「AI 行为树」「野战部队（UGV / 步兵 /
无人机发射车 / 侦查车）」。每加一种单位、一种战术，今天都要改这一个共享循环——
一个 5000 单位的战场里，那意味着每个新兵种都在给所有单位付一次 `has()` 和 `etype` 判断，
而且新战术的代码没有地方放。更根本的问题是**分层错了**：AI 判断的是「我要全速推进」，
不是「这个弯我只能开 11 m/s」——后者是机器的事，写在同一段循环里，两条路都长不大。

---

## 2. 目标结构

**分层规则**：`Systems/AI/` 表层**一个文件就是一个系统**，一个系统就是流水线里的一个
`static void run(World world, float dt)`；常量、schema 基类、火控助手、弹表、注册表全部
装进子文件夹。看表层就知道 AI 有哪些 pass，看子文件夹才知道它们共用什么。

```
Systems/AI/                     表层 = 系统，一个文件一个 pass
  Sense.cs            Sense.run          —— ai_timer 到点、网格查询、通视，写 target
  Threats.cs          Threats.run        —— 本 tick 在飞的制导弹，填进 world.threats
  Intercept.cs        Intercept.run      —— 谁在朝我来：ai_cover / ai_threat（雷达 + 引信臂）
  EngageMech.cs       EngageMech.run     —— 机甲机组（mech_ai）：拦截或地面，摆视线 + 扳机
  EngageVehicle.cs    EngageVehicle.run  —— 轮式机组（vehicle）：同上，摆炮塔
  EngageFoot.cs       EngageFoot.run     —— 步兵机组（foot_ai）：同上，整个人转身
  Wander.cs           Wander.run         —— 平民游荡（写它自己的意图：去哪个点）

  Strategies/                   任务策略 = 同样形状的系统，只是按任务标签分开
    Front.cs          Front.run          —— 兵线机组的战术状态机（本工作的架子重点）
    Raid.cs           Raid.run           —— 突击队（由 `Modes/Battle/Raids.cs` 迁入）
    RaidPath.cs       128 m / 16 m 两级寻路，Raid 的私有件（随 Raid.cs 一起迁入）

  Support/                      工具：没有 run，只有被别人用的东西
    Tags.cs           标签与状态词表的唯一来源（AiTags / AiState 常量）
    Crew.cs           共享基础 schema `CrewColumns` / `CrewRow` + 小工具（眼位、臂查表、触发位）
    Policy.cs         Situation（一次战况快照）+ 策略层的共用件
    Engage.cs         火控共用件：提前量解、臂的射程、半自动上升沿、炮膛门限、引信容差
    ThreatTable.cs    弹表的类型与 schema（ThreatColumns / SeekerColumns）+ world 上的缓存
    Pipeline.cs       模式注册用的系统列表：battle() / range() / defence()
```

两条硬规则：

1. **表层每个 `.cs` 恰好一个 `run`**，签名 `static void run(World world, float dt)`，也就是
   `Action<World, float>` 的一个成员；名字就是文件名（`Sense` → `Sense.run`）。没有 `run`
   的代码一律进 `Support/`。`Intercept.cs` 只有一个 `run`（选威胁）；把炮压到提前量上
   是各平台的事，落在 `Engage<Platform>.run` 里。
2. **每个 schema 跟着用它的那个系统放同一个文件**（`Flights.cs` 的写法：`EngageMech.cs` 里
   就是 `MechColumns` / `MechRow`）；被多个系统共用的（`CrewColumns` 基类、本 tick 的弹表）
   才进 `Support/`。

**AI 里没有「怎么开」的代码。** 沿着兵线折线走、绕开一堵墙、在弯道上减到能转过去的
速度、撞上了倒一把车——这些是机器的操稳与司机的活，全部在移动层：

```
Systems/Movement/               表层 = 一个底盘一个系统（原 `Systems/Movement.cs` 的三个分支）
  Foot.cs           Foot.run      —— 人：命令即速度，腿跟着走
  Mech.cs           Mech.run      —— 机甲：`MECH_ACCEL` 斜坡、腿对齐、失能降速
  Vehicle.cs        Vehicle.run   —— 轮式：自行车模型 + 路线跟随（接受半径、弯道限速、倒车脱困）
  Support/
    Drive.cs        意图 → `vel_cmd`：路线游标（`path_i`）、下一个点、撞墙扇形
    Integrate.cs    `vel_cmd` → `vel` → `pos`：积分、地形贴合、碰撞滑动（三个底盘共用）
```

三个系统用**既有**标签/组件划界，一个 mover 只会落进其中一个：`vehicle`（车）、
`arm_hp`（机甲，即 `defs.arms` 的那一类）、`person`（人，平民也算）。路线与路点数学
放进 `Core/Nav.cs`（库，和 `Grid.cs` / `Ballistics.cs` 并列）：`world.routes` 一张表，
兵线正反各注册一条，突击队与将来的侧翼路线也登记进去，`nav_route` 就是这张表的下标。

`Systems/Ai.cs` 在本工作结束时**删除**；`Systems/Movement.cs` 拆成 `Systems/Movement/`。

命名空间：`Mech.Server.Systems.AI`（`Support/` 与 `Strategies/` 在同一命名空间下）。
每个 `run` 的签名仍是 `void run(World world, float dt)`，`Support/Pipeline.cs` 把它们按
顺序拼成 `List<Action<World, float>>`，也就是 `World._build_pipeline` 从模式手里拿的那个
东西——**模式注册什么，AI 就跑什么**，和今天 `pre_systems()` 的接口一致。

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
| `mech_ai` | **机甲机组**：视线在车顶、两条臂、车体随动 | 所有机甲机组（兵线 / 突击队 / 靶场无人机 / 防御哨） | `EngageMech.run`（拦截与地面战同一处压炮） |
| `foot_ai` | 步兵机组：整个人转身瞄 | 步枪兵 / 反坦克兵机组 | `EngageFoot` |
| `crew_engage` | 这组人会打地面目标 | 兵线机组、突击队、靶场无人机 | `Sense.run` / `Engage.*` |
| `front_mission` | 任务：沿兵线推进 | 战斗模式的兵线部队、靶场的 `debug_spawn` 无人机 | `Front.run` |

复用的既有标签：`vehicle`（APC，轮式）、`person`（人）、`raider`（突击队任务）、
`range_drone`（靶场控制台刷的车）、`home_x`（平民）。

`mech_ai` 就是设计里说的 **MECH_AI**：兵线上的机甲和抄后路的机甲都带它，所以
`EngageMech.run` 对两者是同一份代码——
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
| `ai_cover` | U8 | `Intercept.run` | `Front.run` / `Engage.*` | 有弹在朝我来：本 tick 的枪瞄拦截而不是地面目标 |
| `ai_threat` | I | `Intercept.run` | `Engage.*` | 我瞄的那发弹的实体 id（-1 = 没有） |
| `ai_goal` | U8 | 策略 | 策略 | 目标 id（城镇索引，255 = 敌方指挥部）。由 `raid_goal` 改名而来，两种策略共用一张表 |

意图（§7）：AI 写给司机，写这三样，别的一概不碰。

| 组件 | 类型 | 谁写 | 谁读 | 说明 |
| --- | --- | --- | --- | --- |
| `nav_route` | I | 策略 | `Drive` | 跟着哪条路线走（`world.routes` 的下标，-1 = 没有路线） |
| `nav_x` / `nav_z` | F | 策略 | `Drive` | 没有路线时想去哪个点（占环、回基地、平民游荡） |
| `nav_speed` | F | 策略 | `Drive` | 想要的巡航速度，m/s；0 = 停住 |

沿用今天的 `target`、`ai_state`、`ai_timer`、`spot_x/y/z`、`spot_t`、`path_i`、`lane`。
它们在包里属于每个单位（`UNIT_COMPS`），本轮**不动**（见 §12「可选的收尾」）。

---

## 4. Columns：每个系统声明自己的列

照 `Systems/Flights.cs` 的写法，一个文件上半部分是 schema、下半部分是系统，
「这个 pass 读什么」和「读它的 pass」在同一个屏幕上。

```csharp
// Systems/AI/Support/Crew.cs
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
        "etype", "faction", "weapon", "trigger", "ai_state", "ai_cover",
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
* **查询只做加法，所以身份用正向标签写**：`groups(names)` 没有减法，「会走但不是轮式的」
  这种写法会退化成每行判一次。一个 mover 要么 `vehicle`、要么 `arm_hp`（机甲）、要么
  `person`，三个集合互斥且齐全，于是 `Foot` / `Mech` / `Vehicle` 各查一个集合就分完了，
  一行判定都不需要。

---

## 5. 流水线

`Systems/AI/Support/Pipeline.cs`：

```csharp
public static class Pipeline
{
    public static List<Action<World, float>> battle() => new()
    {
        Threats.run,              // 本 tick 在飞的制导弹（一次，压缩成数组）
        Intercept.run,            // 谁在朝我来：ai_cover / ai_threat
        Sense.run,                // ai_timer 到点：换目标
        Policy.Front.run,         // 战术状态机：ai_state / nav_* / spot_*
        Policy.Raid.run,          // 突击队任务：ai_state / ai_goal / nav_*
        EngageMech.run,           // 枪：三种平台各一个 schema，拦截或地面
        EngageVehicle.run,
        EngageFoot.run,
        Wander.run,               // 平民
    };

    public static List<Action<World, float>> range() => /* battle() 去掉 Policy.Raid.run */;
    // 机动不在这里：`Pipeline` 只管机组。驱动与物理是共享流水线里的
    // `Movement.Foot.run` / `Movement.Mech.run` / `Movement.Vehicle.run`（§7）。
    public static List<Action<World, float>> defence() => new()
    {
        Threats.run, Intercept.run, EngageMech.run,          // 只拦导弹的机组（靶场哨位）
    };
}
```

顺序里每一处都是语义：

| 顺序 | 为什么必须在这个位置 |
| --- | --- |
| `Threats.run` 最先 | 后面两个环节共用一张表；一张表一个 tick，挂在 `world.threats` 上，用 `_threat_tick` 守卫（和 `world._sph` / `_sph_tick` 同一个模式） |
| `Intercept.run` 在感知之前 | `ai_cover` 是本 tick 的**最高优先级**：`Policy.Front` 和 `Engage.*` 都要能读到它（今天就有的行为：被弹追着打的时候不理会地面目标、继续开） |
| `Sense.run` 在策略之前 | 策略读的是「已经定好的目标」 |
| `Policy` 在交战之前 | 策略只写 `ai_state` / `ai_goal` / `nav_*`（意图），不碰枪也不碰车；它要判断的一切（目标是否还在、通视、距离）都在这一段里做完 |
| `Engage.<Platform>` 在机动之前 | 一个平台一个系统，并且是**该平台 `trigger` 的唯一写者**：`ai_cover` 决定瞄拦截还是瞄地面，一个字节，不需要靠顺序仲裁；`target < 0` 且没有 `spot_t` 的机组（靶场哨位）自己写 0 |
| 机动不在 AI 里 | `Movement.*` 在共享流水线里（AI 是模式的 `pre_systems`，机动是 `gunnery` 之后那一段）。机组写完意图就走人，谁把它变成轮子转速是下一层的决定 |

靶场那个只拦导弹的哨位因此不需要私有入口：它是 `ai_crew` + `mech_ai`、没有
`crew_engage`（所以永远没有地面目标），模式注册 `Pipeline.defence()` 三段它就活了——
`Modes/Range/Interceptor.cs` 里那句 `Systems.Ai.intercept(world, dt)` 直接删掉。

---

## 6. 策略层（本工作的架子重点）

### 6.1 状态词表：`Systems/AI/Support/Tags.cs` 里的 `AiState`

`ai_state` 这个组件今天已经存在（突击队用它做 MARCH / ASSAULT / HOLD），本工作把它
提升为**所有 AI 机组共用的一个词表**，并补齐将来要用的名字：

| 状态 | 含义 | 谁写 | 今天是否实现 | 实现它需要的数据（都已在世界里） |
| --- | --- | --- | --- | --- |
| `ADVANCE` | 沿任务路线前进 | Front / Raid | ✅ 兵线 | `path_i`、`lane`、`ai_goal` |
| `ENGAGE` | 目标在射程内且通视：边打边走（远）/ 停下打（近） | Front | ✅ | `target`、`dist`、`nav_speed` |
| `SUPPRESS` | 丢目标：打最后看到的那一点 | Front | ✅ | `spot_x/y/z`、`spot_t` |
| `MARCH` | 突击队：走自己的路线 | Raid | ✅ | `raid_paths` |
| `ASSAULT` | 突击队：站进占领环 | Raid | ✅ | `Waves.CAPTURE_RADIUS` |
| `HOLD` | 突击队：这一城是我们的，守住 | Raid | ✅ | `raid_t`、`towns` |
| `ATTACK_FRONT` | 正面压上去（= 今天的 ADVANCE，语义收紧） | Front | 结构就位，本工作按 ADVANCE 走 | 兵线折线 |
| `ATTACK_FLANK` | 绕侧：给同一段路一个横向偏移 | Front（将来） | ❌ 只留 case | `world.lanes` + 偏移量；路线就是一条点列，侧翼是另一条，`Nav.add` 登记后写进 `nav_route` |
| `CAPTURE` | 站进最近的敌方城镇环 | Front（将来） | ❌ 只留 case | `world.towns`；站环的逻辑从 `Raid` 抽成共用件 |
| `WITHDRAW` | 血太少：往回撤到友方城镇 | Front（将来） | ❌ 只留 case | `hp / hpmax` + 一条反着走的路线（`Nav.add` 现成的） |
| `RESUPPLY` | 弹打空：回基地补 | Front（将来） | ❌ 只留 case | `mag`、`ammo`、最近的己方基地；`Shop` / `Rearm` 目前只服务玩家 |

**规则**：新战术 = 一个新的 `AiState` 常量 + 策略里一个新的 `case` + **一个新的意图**
（换一条路线、换一个目标点、停住）——执行它的是司机（§7），AI 侧一行都不用改。

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

public static Situation sense(World world, in CrewColumns c, int i);   // Support/Policy.cs
```

**意图只有四个写法**，策略层只通过它们下达命令（全部是 `nav_*`，见 §7）：

```csharp
Policy.route(r, id);                 // nav_route = id：跟这条路线走
Policy.point(r, x, z, speed);        // nav_x / nav_z / nav_speed：去这个点
Policy.cruise(r, etype);             // nav_speed = defs.UNIT_DEFS[etype].speed
Policy.halt(r);                       // nav_speed = 0：停住
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
                if (s.covered) { r.state = AiState.ADVANCE; Policy.cruise(r, s.etype); continue; }
                decide(world, c, r, s, dt);
            }
        }
    }

    /// 战术状态机：一 tick 一次，输出「干什么」(ai_state) 和「去哪、多快」(nav_*)。
    private static void decide(World world, FrontColumns c, FrontRow r, in Situation s, float dt)
    {
        // ---- 目标在射程内且通视 -------------------------------------------
        // 后面四个 case 是空位：数据都在 Situation 里，每个只要一次 `Policy.*` 意图，
        // 怎么开过去是司机的事（§7）。
        //   WITHDRAW  : s.hp_frac < defs.AI_WITHDRAW_HP     → Policy.route(r, 反向路线)
        //   RESUPPLY  : s.mag_left == 0                     → Policy.point(r, 己方基地)
        //   FLANK     : 本路正面城镇 contested               → Policy.route(r, 侧翼路线)
        //   CAPTURE   : 最近的敌方城镇在 RAID_* 距离内        → Policy.point(r, 环心)
        switch (r.state)
        {
            case AiState.SUPPRESS: Policy.halt(r);  break;   // 打最后看到的那一点，不走
            case AiState.ENGAGE:   /* 远：Policy.cruise；近：Policy.halt */ break;
            default:               Policy.cruise(r, s.etype); break;   // ADVANCE
        }
    }
}
```

今天的判定原样搬进去（值都不改）：`dist < d.ai_range && Sight.clear(...)` → `ENGAGE`，
`nav_speed = dist > d.ai_range * 0.55f ? defs.speed : 0`；`spot_t > 0` → `SUPPRESS`
（`nav_speed = 0`，`spot_t -= dt`）；其余 `ADVANCE`（`nav_speed = defs.speed`）。`spot_*` 的写入也在这一层
（`ENGAGE` 时刷新，`covered` 时不刷新——和今天一致）。

### 6.4 突击队策略 = 同一个架子的第二个实例

`Modes/Battle/Raids.cs` 整段迁到 `Systems/AI/Strategies/Raid.cs` + `Strategies/RaidPath.cs`，
改动只有三处：

1. `STATE_MARCH/ASSAULT/HOLD` → `AiState.MARCH/ASSAULT/HOLD`（数值不变，0/1/2）。
2. `raid_goal` → `ai_goal`（改名，255 = 敌方基地）。
3. 它自己的 `_drive` 循环改成查 `raider` + `ai_crew` 的 schema；`_one` 不再自己算
   `vel_cmd`（§7），而是把规划出来的点列 `Nav.add` 登记成一条路线，MARCH 时
   `Policy.route(r, id)`，ASSAULT / HOLD 时 `Policy.point(r, 环心, 0)`；
   `raid_i`（路线游标）交给司机的 `path_i`。
4. 「被顶住了」不再由 `Raid` 自己算：司机记 `blocked_t`（它就是干这个的），策略读到它
   累加成 `raid_stuck`，超时就重规划 / `_rescue`。

**枪一律不碰**：今天注释里写的「guns stay `ai.run`'s」在新结构里就是「`Raid.run` 只写
`ai_state` / `ai_goal` / 意图，`EngageMech` 照样按 `crew_engage` 把它的枪打了」。
寻路、逼停、`_rescue` 全部留在 `Strategies/RaidPath.cs`（Raid 的私有件），表层永远只有系统。

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

## 7. 意图与模拟：AI 说要什么，怎么做到是机器的事

这是本次重构真正想立住的界线，也是 `Systems/Ai.cs` 今天最错位的地方：
`_advance_route`、`_corner_speed`、`_route_speed`、`_arrive_radius`、`_passed` 是
**操稳**（一辆 11 m 转弯半径的车在弯道上开不到 100 km/h），`_advance` 的撞墙扇形是
**驾驶**，而它们全都住在 AI 里，于是「想全速推进」这个意图和执行它所需要的物理被混成
一段代码。

### 7.1 谁写什么

| 层 | 写 | 读 |
| --- | --- | --- |
| 机组（`Systems/AI/`） | `sight_yaw` / `sight_pitch`（往哪看、往哪瞄）、`turret_yaw`（车的炮塔）、`trigger`、`yaw`（人与车不同，见下）、`ai_state` / `ai_goal` / `spot_*`、`nav_route` / `nav_x` / `nav_z` / `nav_speed` | 自己声明的 schema |
| 司机与机器（`Systems/Movement/`） | `pos`、`vel`、`vel_cmd`、`leg_yaw`、`wheel_speed`、`backout_t`、`blocked_t`、`path_i`（路线游标） | `nav_*`、`moves` 的物理件 |
| 玩家（`PlayerInput`） | `vel_cmd`（`owner >= 0`，所以和司机互斥） | 摇杆 |

`yaw` 的归属按底盘分，和今天一样：**车**的 `yaw` 是转向状态（司机写），机甲的 `yaw`
由 `Gunnery` 随视线转，**人**的 `yaw` 是机组写（他的身体就是枪，`Gunnery` 把
`sight_yaw` 从 `yaw` 镜像出去）。

### 7.2 意图只有一件事：去哪、多快

```csharp
ecs.register("nav_route", Kind.I, 1, -1);   // 跟着哪条路线走；-1 = 用下面的点
ecs.register("nav_x", Kind.F);              // 想去哪个点（占环、回基地、平民游荡）
ecs.register("nav_z", Kind.F);
ecs.register("nav_speed", Kind.F);          // 想要的巡航速度，m/s；0 = 停住
```

* 「沿兵线推」= 出生时/换任务时写一次 `nav_route`，每 tick 只写 `nav_speed`。
* 「站进环里」= `Policy.point(r, 环心, 0f)`。
* 「全速」是合法的意图：一个轮式车到底能不能兑现，是 `Vehicle.run` 按
  `Defs.APC_LAT_ACCEL` / `APC_CUT` / 弯道视线自己算的（今天的 `_route_speed` 原样搬过去）。
* 我也把 `ai_move` 这个中间量去掉了：`nav_speed` 是不是 0 就是「动不动」。
  **一处有意的行为修正**：今天徒步机组在射程内「不写命令」，于是保持上一条命令继续往前
  走；新模型里 `ENGAGE` 近距写 `nav_speed = 0`，步兵会站住打。想保留旧行为就在
  `decide` 里什么都不写（意图保持上一 tick 的值）。

### 7.3 路线是数据，不是 AI 的状态

`Core/Nav.cs`（库，和 `Grid.cs` / `Ballistics.cs` 并列）：

```csharp
public sealed class Route { public List<(float x, float z)> pts; public bool used; }
public static int add(World w, IEnumerable<(float, float)> pts);   // 登记一条，复用空槽
public static void free(World w, int id);
public static (float x, float z) at(Route r, int i);               // 越界夹到最后一点
public static int advance(Route r, int i, float px, float pz, float radius);
public static bool passed(Route r, int i, float px, float pz, float radius);
public static float arrive_radius(float speed);                     // 轮式：随速度长的接受半径
public static float corner_speed(float ang, float top);
public static float route_speed(Route r, int i, float px, float pz, float top);
```

`world.routes` 一张表，`World` 建好地图后把每条兵线的**两个方向**各注册一条，末点接敌方
指挥部——于是「兵线走完了怎么办」这个今天写在 `_advance` 里的分支消失了：路线本来就通到
终点，到了终点是策略该看到的事。突击队、将来的侧翼/撤退路线由策略 `Nav.add` 登记，
`nav_route` 就是它的下标；`Nav.free` 在队伍没了时回收。

`path_i` 从「AI 的推进指针」变成**司机的游标**（组件不动，语义变成"这条路线我走到第几个点"）。

### 7.4 三个底盘三个系统

| 系统 | 行集 | 它负责的物理 |
| --- | --- | --- |
| `Movement.Foot.run` | `moves` + `person` | 命令即速度（他的机器就是腿），`leg_yaw` 随速度 |
| `Movement.Mech.run` | `moves` + `arm_hp` | `MECH_ACCEL` 斜坡、腿对齐系数、失能降速 |
| `Movement.Vehicle.run` | `moves` + `vehicle` | 后轴自行车模型、`APC_LAT_ACCEL` 侧向上限、路线跟随（接受半径 / 越线判定 / 弯道限速）、`blocked_t` / `backout_t` 倒车脱困 |

共用件在 `Movement/Support/`：`Drive.cs`（意图 → 下一个点 → `vel_cmd`；徒步的撞墙扇形、
车交给 `Vehicle` 自己的纯追踪）与 `Integrate.cs`（`vel_cmd` → `vel` → `pos`、地形贴合、
碰撞滑动、坡面分量）。倒车脱困、弯道限速这些只有车有的东西留在 `Vehicle.cs`。

`Vehicle.run` 的形状（意图在左，物理在右，中间没有一句「AI 觉得」）：

```csharp
public static void run(World world, float dt)
{
    foreach (var g in world.ecs.groups(VehicleColumns.names))      // moves + vehicle
    {
        var c = new VehicleColumns(g);
        foreach (var r in c)
        {
            if (r.nav_speed <= 0f) { r.vel_cmd = Vec3.Zero; r.wheel_speed = 0f; continue; }
            float speed = MathF.Min(r.nav_speed, Defs.UNIT_DEFS[r.etype].speed);   // 调速器
            // 意图 → 下一个点：有路线就跟路线（游标 path_i），否则直接瞄那个点
            var route = Nav.get(world, r.nav_route);
            float ax, az, radius = Nav.arrive_radius(r.speed);
            if (route is not null)
            {
                r.path_i = Nav.advance(route, r.path_i, r.pos.x, r.pos.z, radius);
                (ax, az) = Nav.at(route, r.path_i);
                speed = Nav.route_speed(route, r.path_i, r.pos.x, r.pos.z, speed);  // 弯道包线
            }
            else (ax, az) = (r.nav_x, r.nav_z);
            // 机器：打方向、给油、撞上了倒一把
            Drive.wheels(world, r, ax, az, speed, dt);       // 后轴自行车模型 → vel_cmd/wheel_speed
        }
    }
}
```

（`Drive.wheels` 里的东西就是今天的 `Movement._vehicle` 加上 `_advance_route` 的
方向盘部分，一行没改。）

### 7.5 边界怎么检查

* `Systems/AI/` 里 grep 不到 `vel_cmd` / `path_i` / `terrain.blocked` / `APC_`。
* `Systems/Movement/` 里 grep 不到 `target` / `trigger` / `sight_yaw` / `nav_` 之外的 AI 件
  （`nav_*` 是它要读的意图）。
* `--check diag` 打出来的「几台 AI 车在干什么」应该一模一样：这一段是纯搬家。

---

## 8. 一个系统长什么样（示例）

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
                // 一个平台的枪只有这一个写者：先看有没有弹在朝我来（`Intercept.run`
                // 定好的 `ai_cover`），再看有没有地面目标 / 记住的那个点
                if (r.cover != 0) { Engage.intercept(world, c, r, dt); continue; }
                if (r.target < 0 && r.spot_t <= 0f) { r.trigger = 0; continue; }
                Engage.ground(world, c, r, dt);             // 提前量 + 摆视线 + 等炮膛
            }
        }
    }
}
```

`Support/Engage.cs` 里放三种平台共用的那部分：今天 `_engage` 的上半段（`Engage.lead_aim`：
取**能打到目标的臂里最慢的平均弹速**，`MathUtil.mean_speed`）、`_aim_and_fire` 的规则头
（`Engage.arm_reach`：`rocket`/`saclos` 2.2 km、其余 `ai_range`，并和 `wd.range` 取小；
`Engage.semi_edge`：半自动臂隔 tick 松扳机；`Engage.bore_gate`：机甲的 `err < 0.14` 且
`|d pitch| < 0.02`），以及今天 `_missile_step` 的后半段（`Engage.intercept`：按引信半径算
容差 `atan((prox + hit_r) / d)`，那条比打机身严得多的门限）。

三个平台文件各自持有的东西就是它们的 **schema**：机甲有 `arm_hp`（哪条臂还在、哪条臂的
弹打空了），轮式只转 `turret_yaw`，步兵只有 `yaw`。今天 `_aim_and_fire` 里那段
`if (etype == MECH) ... else if (etype == APC) ... else ...` 就是这三个文件的分界线。

---

## 9. 分期实施

每一期都能编译、能跑、能用 `--check` 验证；任何一期出问题都可以单独回退。

| 期 | 内容 | 验证 |
| --- | --- | --- |
| **P0 标签与组件** | `Support/Tags.cs`；`Components.register` 新组件；`spawn_unit(crew:)` 及各 spawn 点；`World.spawn_unit` 的组装（含 `extra`）；靶场 `Targets` / `InterceptCheck` 的 `crew: false`。`Systems/Ai.cs` 里把所有 `owner >= 0` 换成「有 `ai_crew`」——**行为完全等价**（两组集合相同） | `--check battle --minutes 3`、`--check range`、`--check intercept` |
| **P1 感知与威胁** | `Support/Crew.cs` / `Support/ThreatTable.cs` / `Sense.cs`（`Sense.run`）/ `Threats.cs`（`Threats.run`）/ `Intercept.cs`（`Intercept.run`，= 今天的雷达 + 引信臂 + `_threat` 选择）；`Ai.run` 里删掉 `_missiles`/`_threat`/`_acquire` 与 `ai_timer` 段 | `--check battle --minutes 5`、`--check diag --minutes 1` |
| **P2 交战** | `Support/Engage.cs` + `EngageMech/Vehicle/Foot.cs`；`Ai.run` 里删掉 `_engage`/`_suppress`/`_aim_and_fire`/`_missile_step` 的**瞄准段**（它按 `ai_cover` 落进各平台的 `Engage.*.run`）；`Ai.intercept` → `Pipeline.defence()` | `--check range --minutes 2`（AI 机甲的枪打得中）、`--check intercept`（36 发、34 中以上） |
| **P3 意图与移动拆分** | `Core/Nav.cs` + `world.routes`（兵线正反各一条）；`nav_*` 四个组件；`Systems/Movement/` 拆成 `Foot` / `Mech` / `Vehicle` + `Support/Drive.cs` / `Support/Integrate.cs`，`_corner_speed` / `_route_speed` / `_arrive_radius` / `_passed` / 撞墙扇形 / 倒车脱困**原样搬进物理层**；`Ai.run` 只写意图（这一步不引入策略层，先在旧循环里把 `vel_cmd` 全部换成 `nav_*`） | `--check diag --minutes 2`（几台 AI 车在干什么，应与 P2 完全一致）、`--check battle`（占领推进节奏）、`--check range` |
| **P4 策略层** | `Support/Policy.cs`、`Support/Pipeline.cs`、`Strategies/Front.cs`；`Systems/Ai.cs` 只剩空壳 → 删除；`BattleMode.pre_systems` / `RangeMode.pre_systems` 改为 `Pipeline.*`；删除 `Modes/Range/TestAi.cs`（靶场无人机 = `front_mission` + `crew_engage`） | 全量 `--check battle --minutes 12`、`--check range`、`--check intercept`、`--check diag`、`--check net` |
| **P5 突击队迁入** | `Modes/Battle/Raids.cs` → `Systems/AI/Strategies/Raid.cs` + `Strategies/RaidPath.cs`；`STATE_*` → `AiState`；`raid_goal` → `ai_goal`；`raid_i` → 司机的 `path_i`，路线走 `Nav.add`；`raid_stuck` 改读 `blocked_t` | `--check battle --minutes 12`（raid 周期仍然发生） |
| **P6 收尾** | 注释/文档：仓库里对 `Systems/Ai.cs` 的引用（`Weapons.cs`、`Defs.cs`、`BattleMode.cs`、`Interceptor.cs`、`Raids.cs`、`AGENTS.md`）；`docs/plans/roadmap.md` 的「AI代码ECS化」指向本文件 | 编译零警告 |

---

## 10. 验证与不变量

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
2. **一个 tick 里 `trigger` 只有一个写者**：该平台的 `Engage<Platform>.run`。拦截与地面战
   在同一段代码里靠 `ai_cover` 一个字节选择瞄哪，靠顺序仲裁的规则一条都不留。
3. **一 tick 一张弹表**：`world.threats` 由 `_threat_tick` 守卫，`reset_match` 里复位。
4. **AI 不写机动件的任何一个字**：`pos` / `vel` / `vel_cmd` / `path_i` 只由 `Movement/*`
   （或 `player_input`）写；AI 只写 `nav_route` / `nav_x` / `nav_z` / `nav_speed`。
   这是这次重构最硬的边界，抓代码评审时先看它。
5. `ecs.validate()` 每个 `--check` 都会跑，行指针越界会当场失败。

**判定标准**：`--check battle` 的火力/占领/存活量级不变（不是逐位相同——`--check` 的
RNG 是单线程可复现，但重构不应改变抽取顺序；若曲线明显不同，说明有系统换了遍历顺序，
先查 `groups()` 的原型顺序）；`--check net` 的 kB/s 应该**一位不动**（AI 的组件不上线）。

---

## 11. 风险与对策

| 风险 | 对策 |
| --- | --- |
| 标签拆分让 archetype 数量变多（`mech_player` / `mech_ai` / `mech_ai+raider` …） | 组合数有限（平台 3 × 任务 3），且每类单位都只会出现一次；`--check battle` 的 tick 时间是验收指标 |
| 拦截和地面战抢扳机 | `trigger` 的写者是平台唯一的那个 `Engage<Platform>.run`，`ai_cover` 一个字节决定瞄哪；顺序表（§5）写死在一个地方 `Support/Pipeline.cs`，`--check intercept` 是它的体检 |
| 突击队搬迁期间行为漂移 | P5 单独一期、只做搬家（常量同值、逻辑一字不改），`--check battle` 前后对比 raid 次数与死亡分布 |
| 意图/物理的界线又被磨平（某天有人把限速写回策略里） | §7.5 的 grep 约定 + P3 明确「纯搬家、行为逐位一致」；界线写在 `AGENTS.md` 的那一节里 |
| 靶场的「被动」依赖 `[crew:false]`，漏一处就变成「靶子会开枪」 | 三个 spawn 点写在一处清单（§3.2）；`--check range` 里靶场 unit 的击杀数应保持 0 |
| 弹表从 `query()` 改成 schema 后，`tgt_mem` 只有攻顶弹有 | 用两条 schema（`ThreatColumns` / `SeekerColumns : ThreatColumns`，后者多绑 `tgt_mem`），非导引弹的 `mem` 读 0——与今天 `FView.get` 填默认值完全等价 |
| `Ai.run(world, dt, hulls)` 的过滤参数被删掉，靶场 `TestAi` 依赖它 | 用标签替代：靶场无人机带 `front_mission` + `crew_engage`，靶场注册 `Pipeline.range()`，`TestAi.cs` 删除 |

---

## 12. 不在本工作范围内

* 新战术的具体实现（`ATTACK_FLANK` / `CAPTURE` / `WITHDRAW` / `RESUPPLY`）——只留词表、case 与注释。
* 行为树：策略层的接口（`Situation` 进、`AiState`/`nav_*` 出）就是将来换行为树的接缝。
* 玩家控制、`Gunnery`、`Weapons` 的改动。`Movement` 只做**搬家与拆分**（三个底盘 + 意图 → 命令），
  操稳参数一个都不改。
* 把 `ai_*` 组件从 `UNIT_COMPS` 挪进只属于机组的 `CREW_COMPS`（可以让玩家机甲的行更窄）。
  这是本工作之后的**可选收尾**：做完需要 `--check net` 逐字节核对，也牵扯 `spawn_unit`
  对 `ai_state = 0` 的初始化与 `Harness/Check.cs::diag` 的读法。
* 任何玩家可见的变化 —— 因此 `docs/product/` 不动，只需要按仓库约定把版本号第三位 +1。

---

## 13. 文档与版本

* 本文件：`docs/plans/AI_plan.md`。
* `docs/roadmap.md`：「AI代码ECS化」已移到 DONE，并指向本文件；「AI行为树」仍在 Features。
* `AGENTS.md`：`Systems/` 那段注释里关于 `Systems/Ai.cs` 的描述（「AI crew fires the arms
  it has」「AI missile defence」等）改写为 `Systems/AI/` 的目录与流水线，并把 §3 的标签、
  §5 的顺序写成设计规则；「Wheeled hulls」那条要把**弯道限速/接受半径归到 `Movement.Vehicle`**、
  AI 只写 `nav_*` 这件事写清楚（那条注释今天正好把 `ai._advance_route` 当成机动的主人）。
* 版本：`MechServer.csproj` 的 `<Version>` `1.17.2` → `1.18.0`（步兵开始还击，玩家看得见）。
* `docs/product/`：只读核对。若 P5 之后突击队的行为出现玩家可感的差异（本该没有），
  则按 `docs/product/CHANGELOG.md` 的格式补一条。
