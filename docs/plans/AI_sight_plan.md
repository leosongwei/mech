# AI 视野：威胁度打分、独立视线与注意状态

日期:2026-11-21
状态:已实施（2026-11-24，六步全部完成）
范围:只动 AI 机组(`ai_crew`)，加上"让玩家看得见视线"这一条线路与客户端的改动。
玩家输入、火控定律、双端契约(`tests/cases/*.json`)与既有弹道一律不变。
版本:实施时为新功能 → `MechServer.csproj` 第二位 +1(1.41.0 → 1.42.0)，并写 `docs/product/CHANGELOG.md` 条目。

一句话目标:

1. 机组按**威胁度**挑目标，而不是"最近的那个"(`ThreatScore`：机甲 > APC > 火箭兵 > 步兵，含距离项);
2. 机组有一个**独立的视线**(观瞄孔径)：以 150 °/s 转向意图，孔径 40° 半角
   (与激光设计ator 的窗口同一个常数)，它**不是**炮口指向;
3. 一个**注意状态**(搜寻 / 交战)决定看哪儿：看见目标或被攻击即交战，视线内无目标持续 8 s 回归搜寻，
   搜寻按"右 90° → 正前 → 左 90° → 正前"每 3 s 换一段;
4. 被攻击(弹药飞掠、爆炸)看向弹药来向的地平线方向，带防抖。

---

## 0. 落地结果(2026-11-24)

六步按第 8 节的顺序全部落地，版本 1.42.0。与原计划的偏差写在各行里。

| 步 | 计划 | 结果 |
| --- | --- | --- |
| 1 | `ThreatScore` + `Sense` 排序与滞回 | 如期。`SIGHT_CASTS` 的 6 条视线也改为投给分数最高的 6 个 |
| 2 | 五个组件 + `Watch` 两段 + `spawn` 初始化 | 如期。组件按计划进 `CREW_FCS_COMPS` 与 `CrewColumns.names` |
| 3 | 孔径门 + 搜寻扇区 + 8 s 时钟 | 如期。**一处新事实**:搜寻从"右侧 90 度"这一段开始，所以正前方是第 3 秒才被看到 |
| 4 | `Suppress` + 防抖 + 来向 | 如期。机组表用 `groups()` 两趟 + 平表，落盘走 `CrewRow` 的 `ref` 访问器 |
| 5 | 线格式 + 客户端 | **收窄**:只给 mech / apc 两行加 2 B `gaze`(u16)，步兵行不上线;`flags` 第 6 位没有用上——没有机组的壳子写自己的炮口方位顶替，客户端不需要区分 |
| 6 | 文档、CHANGELOG、版本 | 如期，另加 `docs/dev/client.md` 的 `postAt` 一段 |

实测(第 7 节的验收顺序):

| 项 | 读数 |
| --- | --- |
| `--check fcs` / `hit` / `intercept` / `beam` / `drive` | 全部通过;`fcs` 的六个 scenario、三条 chassis 行、七套 arsenal 模板全 `ok`。**落地时才看清真正的阻碍不是孔径而是靶场布景**:Harness 的射手是随机朝向的机组，靶标摆在正东，于是先加了 `Check.aim_east`(布景把射手的四根角一起指向靶标)，否则量的是"发现"而不是火控 |
| `--check gaze`(新) | 六行全 `ok`:正前方 0.03 s 发现、正后方 20 s 从不发现、90 度 0.60 s、来向 1.30 s、12 发 0 次额外转向、威胁度算例与实现一致 |
| `--check battle --minutes 12` | 528 杀 / 25386 发(48 发一杀)对 1.40.0 的 536 / 28742(54 发一杀):**交火密度没掉**;2.85 ms/tick、535 个单位 |
| 浏览器 | 机甲头顶与装甲车炮塔顶的小观瞄头可见、可动;一台机甲 `gaze` 2.35 rad 而 `syaw` 0.79 rad，两个头明显不同向 |

两处与原计划不同的判断:

* **`flags` 第 6 位不做**(步 5):原计划用它让客户端认出"这是机组"，落地时发现不需要——网线上的
  `gaze` 对没有孔径的壳子写的是它自己的炮口方位，语义已经连续。
* **步兵不上线**(步 5):人体模型没有可转的部件，而步兵是场上最多的一行;产品文档如实写明
  "步兵的视线看不见"，需要时再补一个贴地指示。

---

## 0.1 六条决定

| # | 问题 | 决定 | 理由 |
| --- | --- | --- | --- |
| D1 | 视线与炮口是不是一根轴 | **不是。** `gaze_yaw`(感知孔径)与 `sight_yaw` / `turret_yaw` / `yaw`(火控与炮座)是两条独立的轴 | 现代装甲车辆的车长独立观瞄镜就是这个样子：观瞄头可以在炮塔转到别处时自己扫。合成一根轴会同时坏掉两个既有事实(§1.2) |
| D2 | 视线与意图 | 两个组件：`gaze_yaw`(当前)与 `gaze_cmd`(意图);一个系统转、一个系统决定 | 意图是"想让它指哪儿"，转速是"金属能多快转过去"，两者各有唯一写者 |
| D3 | 注意状态机放哪 | 新组件 `ai_gaze`，与任务状态 `ai_state` **正交** | 时钟不同(信念 2 s / 注意 8 s)，且没有任务需要知道视线；合成一个词会让"停车开火"与"看哪边"互相牵制 |
| D4 | 40° 孔径管什么 | 只管**发现**(`Sense::_acquire`)，不管**跟踪**(`Sense::_sees`) | 已经锁住的目标按 `Crew.MEMORY` 继续打——停车开火的手感就来自这条，不动它 |
| D5 | 火控要不要跟着改 | **一行不改。** 三份 `Engage*.lay`、`Gunnery`、`PlayerInput` 全部保持原状 | 视线是新增的一层，火控与炮座的行为完全不受它影响；`--check fcs` / `hit` / `intercept` 因此可以逐项对照(§6) |
| D6 | 近失从哪读 | `Suppress` 读**扫掠段**(`proj_prev → pos`)与本 tick 的爆，插在 `Impact` 之后、`Blasts` 之前 | 一发 1 km/s 的弹一 tick 走 33 m，点采样会整帧跳过 30 m 的近失球 |

---

## 1. 与现有设计的关系

### 1.1 今天有几根角

| 角 | 组件 | 写者 | 含义 |
| --- | --- | --- | --- |
| 行驶方向 | `leg_yaw`(机甲 / 步兵)/ `yaw`(装甲车) | `Integrate.turn_legs`、装甲车自己的驾驶员 | 腿/车轮朝哪走 |
| 车体 | `yaw` | `Gunnery` 让机甲车体追 `sight_yaw`(80 °/s) | 金属车体朝哪 |
| 炮座 | `turret_yaw`(装甲车)/ `yaw`(步兵身体) | `EngageVehicle`(2.2 rad/s)/ `EngageFoot`(3.2 rad/s) | 炮装在哪 |
| 火控线 | `sight_yaw` / `sight_pitch` | `PlayerInput`(玩家，瞬时)/ `EngageMech`(机组，瞬时) | **打哪儿** |

四条轴各自有主，界面上的含义也各有一条规则 (`docs/product/systems/firecontrol.md`)。本计划加两条，
不碰这四条:

| 新轴 | 组件 | 写者 | 含义 |
| --- | --- | --- | --- |
| 视线(感知) | `gaze_yaw` | `Watch.turn`(150 °/s) | **看哪儿** |
| 视线意图 | `gaze_cmd` | `Watch.run` | 想让它看哪儿 |

### 1.2 为什么不能合成一根轴(第一稿的教训)

第一稿把"视线"当成 `sight_yaw` 本身，只加一个意图组件。它有两个直接后果，都不是调参能修的:

* **机甲的扫视会把车体拽着转。** `Gunnery` 让车体以 80 °/s 追 `sight_yaw`，于是一个搜寻中的机甲
  会一边沿车道走一边把车体左右摆 ±90°。机械上无害(`yaw` 不参与行驶，腿走 `leg_yaw`，
  落地脚印是圆)，但外观是坏的。
* **步兵的转身被限速。** 步兵的身体就是他的炮座(3.2 rad/s ≈ 183 °/s)。视线一旦与炮口同轴，
  150 °/s 的孔径就把他转身压到 150 °/s，等于用感知层的数改了火控层的数。

分开之后两个都消失:孔径转多快与车体、与炮座无关。这也正是现实中的形状——现代装甲车辆的
车长独立观瞄镜，炮塔指向与观瞄头指向是两根轴。

### 1.3 三条轴在一次接战里的分工

| | 搜寻 | 交战 |
| --- | --- | --- |
| 视线 `gaze_yaw` | 3 s 一段的搜寻扇区(相对行驶方向) | 目标方位；有弹打过来且手里没目标时是来向 |
| 火控线 `sight_yaw` / `turret_yaw` / `yaw` | **没人写，停住**(最后那个目标的方位) | `Engage*` 照旧瞬时写拦截点 |
| 车体 / 炮座 | 机甲车体追火控线(Gunnery 不变) | 同左 |
| 行驶 `nav_*` | 任务层(`Front` / `Raid`，不变) | 任务层(不变，停车开火仍是 `Front` 的决定) |

三条后果，都要写进产品文档:

* **搜寻时枪不跟眼睛**：一台机甲扫着观瞄头、炮口朝前走过去，这是真实车辆的样子，也保住了
  "停车开火"的手感(`Front` / `Policy` 一行不动)。
* **发现不等于打得到**：目标可能出现在孔径边缘，炮座要转过去——机甲的火控线瞬时、装甲车炮塔
  2.2 rad/s、步兵身体 3.2 rad/s，随后 `Gunnery` 还要让车体与炮臂跟上(80 / 50 °/s)。这段延迟是
  本设计有意留下的，"看得见"和"打得准"因此是两件事。
* **导弹防御不受孔径约束**：`Threats` / `Intercept` 走雷达方程(`d² ≤ K·v_c`)，雷达是全向的。
  `ai_cover` 的扳机链路一行不变，靶场那个防御哨位照常工作。

### 1.4 注意状态(`ai_gaze`)与任务状态(`ai_state`)的边界

| | `ai_state` | `ai_gaze` |
| --- | --- | --- |
| 谁写 | `Front` / `Raid` | `Watch.run` |
| 回答的问题 | 去哪、停不停 | 看哪边 |
| 词 | `ADVANCE` / `ASSAULT` / `HOLD` / `ENGAGE` / `SUPPRESS` | `SEARCH` / `ENGAGE` |
| 时钟 | 任务自己的(`RAID_HOLD`、`STUCK_*`…) | 最后真的看见之后 8 s / 每个搜寻扇区 3 s |

两个词里都有 `ENGAGE`，但说的不是一件事：`AiState.ENGAGE` 是"这辆车站住开火"，
`AiGaze.ENGAGE` 是"这双眼睛在目标上"。分开的实证是既有代码:`Front` 在 `s.covered`(导弹来袭)分支里
把 `ai_state` 写回 `ADVANCE` 并继续开进——车在走，眼睛仍应该在目标上；合成一个词就无法表达。

### 1.5 保留"停车攻击"

`Front.decide` 与 `Policy` 一行不动：看见目标且距离超过 `ai_range × 0.55` 就继续开进，否则站住打，
丢掉目标就 `SUPPRESS` 朝记忆点继续打。本计划只改三件事:**谁被挑中**(威胁度)、**能不能被看见**
(孔径)，**孔径往哪转**(`Watch`)。

---

## 2. 组件(ECS 视角)

五个新组件，全部只挂机组：加进 `Components.CREW_FCS_COMPS`，所以玩家的机器、靶场固定靶、
脚本傀儡在每一处查询里天然不存在(`Ecs/Components.cs` 既有的划分理由:机组的火控状态，
飞行员一个都没有)。

同时加进 `CrewColumns.names`(`Systems/AI/Support/Crew.cs`)，理由与 `ai_timer` / `ai_state` /
`ai_cover` 相同:它们是机组级别的状态，机组 pass 需要读，而 schema 是"这次 pass 读什么"的声明，
不是"允许谁写"的声明。

| 组件 | 类型 / 宽度 | 默认 | 唯一写者 | 读者 | 含义 |
| --- | --- | --- | --- | --- | --- |
| `gaze_yaw` | `F` × 1 | 0 | `Watch.turn` | `Sense`、`Watch.run` | 视线方位(水平孔径的轴线) |
| `gaze_cmd` | `F` × 1 | 0 | `Watch.run` | `Watch.turn` | 视线意图方位 |
| `ai_gaze` | `U8` | 0 | `Watch.run` | `Watch.turn`、`Harness` | 注意状态:`AiGaze.SEARCH` = 0，`AiGaze.ENGAGE` = 1 |
| `gaze_t` | `F` × 2 | 0 | `Watch.run` | `Watch.run` | `[hold, clock]`：距最后一次真的看见多少秒 / 搜寻时钟多少秒 |
| `flinch` | `F` × 2 | 0 | `Suppress` | `Watch.run` | `[t, yaw]`：被打的余温还剩多少秒 / 来向方位 |

* 一个组件一个写者，跨 tick 传递(与 `ai_tgt` 的信念、`first_hit_record` 的形状一致)。同 tick 内的
  两个写者一个都不存在，所以流水线顺序不承担语义。
* `gaze_t` 用一条 stride 2 的缓冲装两个计时器(`ai_tacc`、`ai_tgt` 是同一写法)，不新增两个组件名。
* `flinch[0]` 是**剩余秒数**(递减)，所以"正在被打"就是 `flinch[0] > 0`。
* **孔径只有方位，没有俯仰。** 视线是一根水平孔径：地面目标在视线上的仰角，最坏情形(200 m 处
  落差 120 m)是 31°，本就在 40° 半角之内，加一个俯仰锥只会把远距离目标误挡掉；而"看向弹药来向的
  地平线方向"这个要求本身也是水平的。少一个组件、少一份线格式。

### 2.1 状态词的归属

`AiGaze` 与 `AiState` 并列写在 `Systems/AI/Support/Tags.cs`——那个文件是机组的词汇表，
一个词加一行，不新增文件。

```csharp
public static class AiGaze
{
    public const byte SEARCH = 0;   // 孔径里没有目标：在搜寻扇区之间扫
    public const byte ENGAGE = 1;   // 有目标或有弹打过来：孔径在目标 / 来向上
}
```

---

## 3. 系统与流水线位置

两个新系统(`Watch` 一个文件里两段)、一个新采集器(`Suppress`)、一个静态类(`ThreatScore`)、
一处对 `Sense` 的改写。

| 系统 | 文件 | 流水线位置 | 写 | 读 |
| --- | --- | --- | --- | --- |
| `Watch.run` | `Systems/AI/Watch.cs` | AI 块:`Intercept` 之后、`Sense` 之前 | `ai_gaze`,`gaze_t`,`gaze_cmd` | `ai_tgt`,`flinch`,`leg_yaw`,`yaw` |
| `Watch.turn` | 同上 | 紧跟 `Watch.run` | `gaze_yaw` | `gaze_cmd` |
| `Sense`(改) | `Systems/AI/Sense.cs` | AI 块不变(`Sense` 仍在这里) | `target`,`ai_tgt` | 新增 `gaze_yaw` |
| `Suppress.run` | `Systems/AI/Suppress.cs` | 伤害链:`Impact` 之后、`Blasts` 之前 | `flinch` | 弹道段(`proj_prev`,`pos`,`vel`,`faction`)、`blast` |
| `ThreatScore`(不是系统) | `Systems/AI/Support/ThreatScore.cs` | — | — | — |

实施后的 AI 块(`Pipeline.sense` 里两行，三个模式共用):

```
Threats → Intercept → Watch.run → Watch.turn → Sense → Front → Raid → Paths → Engage* → Wander
```

**为什么 `Watch` 排在 `Sense` 之前**:`Watch.run` 要判断"本 tick 看见了吗"，而 `Sense` 把
`belief_age` 归零写进信念——所以 `Watch.run` 读的是**上一 tick** 的信念，判据仍是
`belief_age == 0`(`Sense` 只在自己跑的时候才把年龄走上去)；`Sense` 读的是**本 tick 转完**的孔径，
零延迟。两边都拿到手上最好的信息，不需要任何容差。

**共享段与 `Engage*` 一处不动**：火控线仍然由 `PlayerInput` / `EngageMech` / `EngageVehicle` /
`EngageFoot` 写，`Gunnery` 照旧让车体与炮臂追。D5 的理由就在这一行。

伤害链:

```
Impact → Suppress → Blasts → Mortality → Economy → Debris
```

### 3.1 类型化列绑定(照 `Systems/Flights.cs`)

每个 pass 声明自己读的组件集，用 `Columns<TRow>` / `IRow<TColumns>`，不硬访问 `component.xxx`:

```csharp
// Systems/AI/Watch.cs
public sealed class WatchColumns : CrewColumns
{
    public float[] leg_yaw = null!;              // 搜寻扇区相对的是行驶方向，不是车体朝向

    private static readonly string[] watch_names =
        Components.Cat(Components.Cat(names, "leg_yaw"), AiTags.CREW);
    public static readonly string[] OnWatch = watch_names;

    public WatchColumns(Group g) : base(g) { }

    public override string[] Names => watch_names;

    protected override void bind(Group g) { base.bind(g); leg_yaw = g.f("leg_yaw"); }
}
```

`CrewRow` 增加与新组件同名的访问器(全部是缓冲元素本身，没有回写):

```csharp
public float gaze      { get => _c.gaze[i]; set => _c.gaze[i] = value; }
public float gaze_cmd_mem { get => _c.gaze_cmd[i]; set => _c.gaze_cmd[i] = value; }
public byte  gaze_state_mem { get => _c.gaze_state[i]; set => _c.gaze_state[i] = value; }
public float hold       { get => _c.gaze_t[i * 2]; set => _c.gaze_t[i * 2] = value; }
public float search_clock { get => _c.gaze_t[i * 2 + 1]; set => _c.gaze_t[i * 2 + 1] = value; }
public float flinch_t   { get => _c.flinch[i * 2]; set => _c.flinch[i * 2] = value; }
public float flinch_yaw { get => _c.flinch[i * 2 + 1]; set => _c.flinch[i * 2 + 1] = value; }
```

平台分支是 **archetype 级**一次(`g.has(...)`)，不是逐行的类型判断——与 `Gunnery.SightColumns.has_vehicle`
和 `Flights.SaclosColumns` 完全同一个写法:`mech_ai` / `foot_ai` / `vehicle` 都在 archetype key 里。
`Watch` 只在"搜寻扇区相对哪个方向"这一处需要它:机甲与步兵的 `leg_yaw`(腿跟着 `vel_cmd`，
与视线无关)，装甲车的 `yaw`(它的转向状态，`Integrate.turn_legs` 跳过车)。

### 3.2 `Watch.run`:注意状态机与意图

```
每个机组每 tick:

  # 1. 输入
  known  = has_belief                      # 有信念(含 Crew.MEMORY 的 2 s 外推)，上一 tick 写下的
  fresh  = known && belief_age == 0        # 上一 tick 真的看见了(`Policy.situation` 的同一个判据)
  hit    = flinch_t > 0                    # 最近 1.5 s 内有弹飞掠或爆在附近

  # 2. 状态与计时
  if fresh || hit:  hold = GAZE_HOLD       # 看见/被打，重置 8 s
  else:             hold = max(0, hold - dt)
  gaze  = (known || hold > 0) ? ENGAGE : SEARCH
  clock = (gaze == SEARCH) ? clock + dt : 0

  # 3. 意图(优先级自上而下，第一条成立的就写)
  if known:             gaze_cmd = bearing(belief_pos)              # 以目标为中心
  else if hit:          gaze_cmd = flinch_yaw                       # 来向的地平线方向
  else if gaze==SEARCH: gaze_cmd = heading + PATTERN[(int)(clock / GAZE_STEP) % 4]
  else:                 (不写：孔径停在最后看到的地方，hold 走完 8 s 再开始扫)
```

* `heading` = 机甲与步兵的 `leg_yaw`、装甲车的 `yaw`。**必须相对行驶方向**:机甲的车体是追着火控线
  转的，以它为基准会形成"看 90° → 车体转 → 下一个扇区又偏 90°"的正反馈。
* "不写"那一支是"守住记忆点":`Sense` 忘掉目标时，孔径正好停在最后看到的方向上，所以 8 s 的收尾
  不需要再存一份方位。
* 搜寻扇区 `PATTERN` 是 `{+90°, 0°, −90°, 0°}`:一个循环 12 s 覆盖左右各 90°，**正后方永不扫**
  (第 5 节把它记成玩法事实:侧翼接近不看背后，只有开火才会让机组回头)。
* 被打时**不打断手上的目标**:`known` 的分支在前。一颗子弹从侧后飞过不会让机组放弃它正在打的
  那一辆——想让它转移火力，得靠分数(第 4 节)与扫描钟。

### 3.3 `Watch.turn`:一条转速

```
每个机组每 tick:
  gaze_yaw = turn_toward(gaze_yaw, gaze_cmd, GAZE_RATE * dt)
```

一条定律、一张表、一个常数，没有平台分支——孔径是观瞄头，它不是炮塔也不是身体(§1.2)。
`MathUtil.turn_toward` 处理绕 ±π 的最短路径。

### 3.4 `Sense`:孔径门与威胁排序

两处改动，都在"发现"这一半(跟踪那一半不动):

1. `_acquire` 的候选循环前置一条孔径门:

```csharp
// 与激光设计ator 的窗口同一个常数:机组能握住一条激光的地方，就是它能看见的地方
if (MathF.Abs(MathUtil.wrap_pi(MathF.Atan2(dz, dx) - r.gaze)) > Watch.CONE) continue;
```

2. 排序键从"平面距离"换成 `ThreatScore`，并加切换滞回:

```csharp
// 手里的目标不参与重选，除非新来的明显更值钱
float cur = r.target >= 0
    ? ThreatScore.of((int)e.uv("etype")[r.target], dist_to_belief, own.sight) : 0f;
if (found >= 0 && ThreatScore.of((int)etype[found], cand_ds, own.sight) > cur * ThreatScore.SWITCH)
    …take it
```

`SIGHT_CASTS = 6` 的视线投射也改为投给**分数最高的 6 个**候选，而不是最近的 6 个——同一趟排序换一个键。
`sight_yaw` 已由 `CrewColumns` 绑定，孔径门只多读一个 `gaze_yaw`。

### 3.5 `Suppress`:近失与爆炸，带防抖

一次 tick 一次:先把机组登记成 k = 0..n−1，再让"响声"去写它(`ThreatTable` 的同一个形状:
一次 tick 一张平表，之后全是位置读)。

```
1. crew_idx: id → k(`groups(AiTags.CREW)` 一趟)，其余 -1
2. best = [∞ × n]，yaw = [0 × n]

3. 弹道段:遍历四种弹的 archetype(`WhizColumns : Flights.RoundColumns`，只多绑一个 `faction`)
     跳过 faction 相同的弹
     扫掠距离:机组中心到线段 (proj_prev, pos) 的最近距离 ≤ WHIZ 即"飞掠"
     方位 = 弹自己的来向 atan2(−vz, −vx):飞掠时弹的位置就在谁身上，速度才是方向

4. 爆:遍历 `blast`，跳过同阵营，`|blast.pos − crew.pos| ≤ HEAR`
     方位 = 爆心相对机组的方位

5. 落盘(每机组一次，防抖):
     if flinch_t > FLINCH_MEM − FLINCH_GAP && |wrap_pi(new_yaw − flinch_yaw)| < FLINCH_ARC
         flinch_t = FLINCH_MEM                       // 同一方向的一串弹:只续时，不回头
     else
         flinch_t = FLINCH_MEM; flinch_yaw = new_yaw // 换了一个方向:慢慢转过去
```

* 防抖的两条判据都必要:窗口内同向续时(一串 30 mm 打过来不该让孔径抽动十次)，窗口内换向要求
  方位差 ≥ `FLINCH_ARC`(第二个射手出现要立刻反应)，窗口之外一律接受(上一轮已经散了)。
* 两个 pass 都是**整批缓冲**(`Columns`)，只有落盘那一次是散写:第 5 步对每台机组
  `ecs.slot(eid)` 取一次 `(archetype, row)` 再写 `flinch`(`docs/dev/ecs.md` §5 的第二扇门)。
  一台机组每 tick 至多被写一次，所以"谁赢"在第 2 步的 `best` 里就已经定下，落盘不再比较。
* 扫掠而不是点采样:D6。
* 弹道段在这里仍然活着(`Impact` 在它之后才销毁)，本 tick 的爆也都还在(`Blasts` 在它之后才递减
  与销毁)——这是把它插在 `Impact` 与 `Blasts` 之间的全部原因。代价是"直接命中我自己的那一条腿"
  已经不在了，但直击要么带爆(被爆捕获)，要么是枪弹一串里其余那些在飞掠时被捕获。

### 3.6 `ThreatScore`:打分的唯一出处

`Systems/AI/Support/ThreatScore.cs`，静态类 + 一张表，新项以**乘数**追加(日后的"正在打我"、
闭合速度、剩余血量、对方武器口径):

```csharp
public static class ThreatScore
{
    // 平台基准:一架机甲是一辆 APC 的两倍，是一支步枪的十倍
    public static readonly float[] W = { … };            // 按 etype
    /// <summary>归一化距离的这一份时分数减半:0.7 是"视野半径的 70 %"。</summary>
    public const float HALF_AT = 0.7f;
    /// <summary>换目标要超过现值这么多。</summary>
    public const float SWITCH = 1.25f;

    public static float of(int etype, float dist, float sight)
    {
        float x = dist / MathF.Max(sight, 1f);           // 0 = 贴脸，1 = 视野边缘
        return W[etype] * (1f / (1f + (x / HALF_AT) * (x / HALF_AT)));
    }
}
```

| `etype` | `W` | 说明 |
| --- | --- | --- |
| `ETYPE_MECH` | 1.0 | |
| `ETYPE_APC` | 0.5 | |
| `ETYPE_INF_ROCKET` | 0.25 | 火箭筒能打穿装甲 |
| `ETYPE_INF_RIFLE` | 0.1 | |
| `ETYPE_BASE` | 1.0 | 敌方指挥部是胜负条件;今天"最近可见者"会打它，打分不能把它踢出候选 |
| 其余 | 0 | 尸体、残骸、平民、掩体没有 `combat`，本来就进不了候选(`Ecs/Components.cs` 的 tag 规则) |

算例(一台机甲机组，`sight` = 3500 m，同时看见):

| 目标 | 距离 | `x` | `f(x)` | 分数 |
| --- | --- | --- | --- | --- |
| 敌机甲 | 3000 m | 0.857 | 0.400 | 0.400 |
| 敌 APC | 800 m | 0.229 | 0.903 | **0.452** ← 打这一辆 |
| 敌步兵 | 200 m | 0.057 | 0.994 | 0.099 |

平台权重在视野半径内**压得过距离**(1 : 0.5 的权重差要 2 倍以上的距离差才翻盘，1 : 0.1 要 10 倍):
"机甲 > APC > 步兵"因此是一条稳定的规则，距离只在同型之间分辨先后——这是选择这张权重表时接受的
设计后果，不是副作用。想让远处(3000 m)的机甲压过近处(800 m)的 APC，把 `HALF_AT` 调大即可。

---

## 4. 线格式与客户端:让玩家看得见视线

视线如果只存在于服务端，这个机制就是不公平的:玩家没有任何办法知道"那台机甲在看哪边"，
侧翼与潜行变成掷骰子。所以这一步是本计划的必做项，不是可选项。

| 项 | 改动 | 代价 |
| --- | --- | --- |
| `flags` 第 6 位 | `flags_of` 置位 = 这辆车的乘员是机组(`ai_crew`) | 0 B，字节里还剩 6、7 两位(mech / apc 行有 `flags`，infantry 行没有) |
| `gaze` 字段 | 加到 mech / apc 两种行尾，`u16` 量化到 `TAU`(分辨率 0.0055°) | **+2 B/行**，`ROW_MECH` 74 → 76、`ROW_VEHICLE` 46 → 48 |
| `Net/Netcode.cs` | 两处写行 + 两个 `ROW_*` 常数;没有 `ai_crew` 的壳子写 `sight_yaw`(玩家自己的机器没有孔径) | — |
| `frontend/src/net/rows.js` | 两张表各加一个 `['gaze','u16']`，`bytes` 同步 | — |
| `frontend/src/net/view.js` + `render/` | 解码成 `-π..π`;机甲画头顶观瞄头的转向，装甲车画炮塔顶上的传感器 | 见下 |

渲染规则:

* **机甲**:观瞄头是车体上方一个独立的小罩子，绕自身竖直轴转到 `gaze`——与 `syaw`(炮口)是两根轴，
  这就是这个机制的可读形态。
* **装甲车**:炮塔顶上一根短传感器，同样绕竖轴转。
* **步兵不上线**:`ROW_SOFT` 保持 20 B，人体模型上没有可转的部件，而步兵是场上数量最多的一种行，
  为此加 2 B/行买不到可读性。产品文档如实写明"步兵的视线看不见";日后要补，是在人身边画一个
  贴地的朝向指示，与本次的行宽改动无关。

线格式的实测:`--check net --minutes 30 --from 8`(一名玩家的线路流量，按分钟统计)。
两台机甲 + 一辆 APC + 若干步兵的行宽各 +2 B，预期是每帧几十字节，与机身/速度字段相比是一小项。

---

## 5. 数值表

| 常数 | 值 | 含义 |
| --- | --- | --- |
| `Watch.RATE` | 150 °/s = 2.618 rad/s | 观瞄孔径的转速 |
| `Watch.CONE` | `Defs.BEAM_CONE` = 0.69813 rad(40° 半角) | 孔径;与激光设计ator 的窗口同一个常数，不另立一个 40° |
| `Watch.HOLD` | 8 s | 最后一次真的看见之后，注意状态保持"交战"的秒数 |
| `Watch.STEP` | 3 s | 每个搜寻扇区的停留时间 |
| `Watch.PATTERN` | {+90°, 0°, −90°, 0°} | 相对行驶方向的四段循环 |
| `Suppress.WHIZ` | 30 m | 弹道与机体中心的最近距离，小于它算"飞掠" |
| `Suppress.HEAR` | 80 m | 爆心到机体的距离 |
| `Suppress.MEM` | 1.5 s | 一次近失让孔径朝来向保持的秒数 |
| `Suppress.GAP` | 0.5 s | 防抖窗口 |
| `Suppress.ARC` | 25° | 防抖窗口内换向所需的方位差 |
| `ThreatScore.HALF_AT` | 0.7 | 归一化距离的分数减半点 |
| `ThreatScore.SWITCH` | 1.25 | 换目标的滞回 |

既有的、本计划不改的:`ACQUIRE` 0.45 s、`SIGHT_CASTS` 6、`Crew.MEMORY` 2 s、`ai_range` / `sight`、
`MECH_HULL_RATE` 80 °/s、`MECH_ARM_RATE` 50 °/s、`TURRET_RATE` 2.2 rad/s、`BODY_RATE` 3.2 rad/s。

---

## 6. 需要改动的既有文件

| 文件 | 改动 |
| --- | --- |
| `Ecs/Components.cs` | `register` 五个新组件;加进 `CREW_FCS_COMPS` |
| `Systems/AI/Support/Tags.cs` | `AiGaze` 两个词 |
| `Systems/AI/Support/Crew.cs` | `CrewColumns.names` + bind + `CrewRow` 访问器 |
| `Systems/AI/Watch.cs` | 新:`run` / `turn` / `WatchColumns` |
| `Systems/AI/Suppress.cs` | 新:`run` / `WhizColumns` |
| `Systems/AI/Support/ThreatScore.cs` | 新 |
| `Systems/AI/Sense.cs` | 孔径门 + 威胁排序 + 切换滞回 |
| `Systems/AI/Support/Pipeline.cs` | `sense()` 里插两行(`battle` / `range` / `defence` 共用) |
| `Core/World.cs` | 伤害链插 `Suppress.run`;`spawn_unit` 给机组初始化 `gaze_yaw` / `gaze_cmd` = 车体 `yaw` |
| `Net/Netcode.cs` | `flags` 第 6 位;mech / apc 两处行尾 `gaze`(u16);两个 `ROW_*` 常数 |
| `frontend/src/net/rows.js`、`view.js` | 行表与解码 |
| `frontend/src/render/` | 机甲观瞄头、装甲车传感器(§4) |
| `Harness/` | 新检查 `--check gaze`(§7) |

不改:`Front`、`Raid`、`Policy`、`Engage`、`EngageMech/Vehicle/Foot`、`Gunnery`、`PlayerInput`、
`Locking`、`Weapons`、`Intercept`、`ThreatTable`。

`Core/Defs.cs` 不加常数:本计划的常数都是**机组的策略**，按 `AGENTS.md` "调参数值统一放在
`Core/Defs.cs` 与各模式系统的顶部"放在各自系统的顶部(`Watch`、`Suppress`、`ThreatScore`);
感知窗口复用既有的 `BEAM_CONE`。

---

## 7. 风险与实测

| 风险 | 表现 | 处置 |
| --- | --- | --- |
| **变盲** | 感知从 360° 瞬时变成 80° 每 12 s 扫一遍，交火密度下降、一场战斗打不完 | 先在靶场量"从进入孔径到被锁定"的秒数;`--check battle` 的每分钟交战与击杀;掉太多就调 `STEP`(3 → 2 s)或把 `PATTERN` 扩到 ±120° |
| 发现到开火有延迟 | 炮座在孔径边缘要转过去(装甲车 2.2 rad/s、机甲车体 80 °/s) | 这是有意的设计(§1.3);若手感太钝，把搜寻扇区收窄到 ±45°，转速不动 |
| 目标抖动 | 两个同分目标之间来回切，孔径跟着左右甩 | `ThreatScore.SWITCH` 1.25 的滞回;`--check diag` 看单机组的目标切换次数 |
| 视线不可读 | 玩家看不出被看 | §4 的线格式与渲染。若客户端步骤推迟，产品文档要写明"视线暂不可见"，不能默认玩家不知道 |
| `Suppress` 的开销 | 每 tick 遍历所有在飞弹 × 一个 128 m 网格盒 | 与 `Sense` 已有的搜索盒(半径 28 格)相比是小项;用 `MECH_PERF=1 --check battle` 的每 tick 开销核对 |
| 直击自己那一条腿不被采样 | 被枪弹直接命中时没有"飞掠"事件 | 一串里其余弹会飞掠;若实测发现机组被打不回头，再加"`hp` 下降也是一个事件" |

验收与度量顺序(按 `AGENTS.md`:"机制的度量在靶场进行，而非战斗"):

1. **火控必须逐项相同**:`--check fcs` / `hit` / `intercept` 在步骤 1–3 之后与今天一致(本计划不改
   火控与炮座);差异只允许来自"挑了另一个目标"。
2. 新 `--check gaze`:(a) 靶标在孔径正中 2 km 时被锁定的秒数;(b) 靶标在正后方、机组搜寻时
   永不被锁定;(c) 一发从侧后掠过的弹让孔径转向的秒数与方位;(d) 一串同向弹只产生一次转向;
   (e) `ThreatScore` 的算例表与实现一致;(f) 孔径确实以 150 °/s 转(转过 90° 用 0.6 s)。
3. `--check battle --minutes 12`、`--check stuck`、`--check diag`、`--check net`:交战密度、
   受困、切换次数、新增流量。

---

## 8. 分步落地

每一步都能单独构建、单独读数，后一步以前一步为基线。

| 步 | 内容 | 读数 |
| --- | --- | --- |
| 1 | `ThreatScore` + `Sense` 的排序与滞回(无孔径) | `--check battle`、`--check diag` 的目标分布 |
| 2 | 五个组件 + `Watch.run` / `Watch.turn`(`gaze_cmd` 恒等于车体朝向，等同于"视野无方向性") | 与今天逐项相同的读数——这一步只铺管道 |
| 3 | `Sense` 的孔径门 + 搜寻扇区 + 8 s 时钟 | `--check gaze` (a)(b)(f)、`--check battle` |
| 4 | `Suppress` + 防抖 + 来向 | `--check gaze` (c)(d) |
| 5 | 线格式 `flags` + `gaze`、客户端解码与渲染 | `--check net`、浏览器实测(§`docs/dev/browser.md`) |
| 6 | 文档、`CHANGELOG`、版本 | 全套检查 |

---

## 9. 不做的事

* **不把视线与炮口合成一根轴**:D1、§1.2。
* **不加俯仰孔径**:§2 的仰角算例。
* **不改任务层**:`Front` / `Raid` / `Policy` 一行不动;视线不参与"去哪"。
* **不改火控**:三份 `lay`、`Gunnery`、扳机门控一行不动(D5)。
* **不改导弹防御的判据**:雷达全向，孔径只挡地面目标的发现。
* **不把被攻击者的身份接进分数**:`blast` 只有 `blast_pid`(玩家 id)，不足以还原射手实体;
  枪弹的 `proj_src` 可以，但只有一半来源，留作日后的乘数项。
* **不给玩家加视野**:玩家的眼睛就是他自己的鼠标。
* **不做"被打时找一个掩体"**:那是任务层的另一种状态(`AiState.WITHDRAW` 已经在等实现者)，
  与视野无关。

---

## 10. 文档与版本(实施时)

1. `docs/product/systems/ai.md`:§1.3 感知加孔径与威胁排序;新增一节"视线与注意状态"
   (两根轴的分工表、150 °/s、40° 窗口、8 s、3 s 扫视、来向防抖、搜寻时枪不跟眼睛);
   §2.1 数值表加第 5 节的各行;
2. `docs/product/systems/net.md`:行宽 +2 B 与 `flags` 第 6 位;
3. `docs/product/hud/` 或 `docs/dev/client.md`:观瞄头的绘制规则(§4);
4. `docs/dev/pipeline.md`:AI 块与伤害链各加一行(带"为什么在这个位置");
5. `docs/dev/checks.md`:`--check gaze` 一行;
6. `docs/product/CHANGELOG.md`:新条目(≤ 8 行);
7. `MechServer.csproj`:`1.41.0` → `1.42.0`。
