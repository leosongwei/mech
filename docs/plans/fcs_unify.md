# 火控统一与修复计划：一条解算律，两个宿主

> **状态：已落地（2026-11-10，版本 1.21.0）。** S1–S6 全部完成并实测：
>
> | 项 | 结果 |
> | --- | --- |
> | 单元测试 | `tests/cases/fcs.json` **42 条**，两侧各跑一遍全绿；`./run_tests.sh` 一条命令跑完 |
> | 两侧一致性 | **42 条全部一致，0 条不同**；最大差异 点 **1.48e-2 m** / 角 **2.31e-5 rad** / 飞行时间 **5.09e-6 s**（容差 0.25 m / 1e-3 rad / 0.02 s） |
> | 集成 `--check fcs` | 6 个场景（800/1600 m 横穿 APC、机甲、静止车、火箭巢、105 mm）逐条 `ok`，班组与玩家两列都在 1–3 m 内 |
> | 12 分钟战斗 | 改前 4 场 / 改后 3 场：炮弹 **5.4 万 → 3.5 万发**，击毁 **402 → 411 台**（每击毁耗弹 134 → 86 发）；tick 成本 1.3–1.6 ms（与改前同量级） |
> | `--check intercept` | **22–26 / 32**（同一张表改前 **15 / 32**；旧文里的 34/36 是更早那张 9 组表） |
> | 线上字节 | `--check net` 不变（只多了 `mech.pitch_min/pitch_max` 两个一次性常数） |
>
> 与本节草稿的出入，按实现更正：目录是 `tests/cases/` + `tests/frontend/` +
> `tests/backend/`（不是 `tests/fcs/` + `tests/Fcs.Tests/`）；`Harness/FcsRef.cs`
> **不需要**——B 枪直接用 `FireControl.solve` 配 `pl.zero_range`，两条链走的是同一个
> 函数，正好就是要证明的事；门限的俯仰那一半最后仍然是机械常数 `Engage.PITCH_TOL`
> （0.02 rad），因为发射仰角来自 `aim_elev`，机器只要"转到位"（理由见 §4.4 与
> `Systems/AI/Support/Engage.cs` 的头注释）。
>
> 下面的原文保留，作为这次改动的设计与推导记录。

---

### 一页纸

| 类别 | 条目 | 位置 | 实测影响 | 本次 |
| --- | --- | --- | --- | --- |
| **D1** | 火箭巢 `burn` 未设 → `mean_speed` 退化成发射速度 55 m/s，整条视线按 14.5 s 提前量瞄 | `Defs` 火箭巢 + `MathUtil.mean_speed` | 800 m 机炮**偏前 164.7 m** | **修** |
| **D2** | AI 的仰角是「对着视点（射线尽头）调零」，不是对着目标 | `Ballistics.zero_pitch`（`Weapons.fire:272`） | 2.6 km 高 **3.3 m**，过山脊会跳 | **修** |
| **D3** | 开火门限是定值 `0.14 rad`，与目标角径无关 | `Engage.trigger`（`Support/Engage.cs:119`） | 2 km 上容差 **280 m** | **修** |
| S | 一条视线 vs 每臂一个解 | `Engage.ground_aim` / `game._leadMarkers` | 修好 D1 后仍 **7–26 m** | **修**（主手策略） |
| — | 瞄准高度取几何中点（机甲 3.25，躯干球中心 4.15） | 两侧 | 机甲靶余隙 **0.02 m** | **不修**（§8.1） |
| — | 散布、`salvo`、弹道积分、命中判定 | `Weapons.fire` / `Flights` / `FirstHit` | — | **不动**（§4.7 只画边界） |

---

## 1. 现在的两条链

### 1.1 客户端（参考实现）

```
input.yaw/pitch ──(aim assist 命中一个环)──▶ _sightAim()          game.js:1063
                                               │  解 ring: _ringSolve -> map.js leadPoint
                                               ▼
                           线上 `in` 消息的 yaw/pitch = 视线（快照之外唯一的输入量）
```

* `game.js::_leadMarkers`（1205）对每只**直射**臂（`bullet` / `rocket`）各解一个环：
  `leadPoint(wd, zeroRange, maxElev, muzzle, eye, 目标@开火时刻, 目标速度, ax, az)`。
* `map.js::leadPoint`（675）：`p`（视线仰角）内层迭代 4 次、提前量外层 4 轮，每一轮都
  重解水平方位；内层每次都用 `zeroAt(wd, yaw, p, muzzle, zeroRange)` → `flightTo(...)`
  （1/30 s 半隐式欧拉、同一条推力曲线、同一条重力）求**落点高度与飞行时间**，再把 `p`
  修正到「弹丸在目标距离处的高度 = 目标高度」。发射器机械偏置 `bias` 被解算吸收。
* 一切从**开火瞬间的位姿**解：`_firePose`（1028）= 最新权威位置 + 速度 × `_fireLag()`
  （半个快照周期 + RTT/2 + 半个 tick）；`_muzzle`（979）逐臂给炮口，含 `reach` /
  `rack.elev` / `rack.bolted`；`_sightEye`（1040）给眼位。
* 目标状态走**线上实测**：位置 `px/pz` + `vel × lag`，速度取平滑后的 `mv*`，
  加速度由 `net.js` 两次速度差分测得（0.25 s EMA，> 25 m/s² 判为传送，> 0.5 s 过期作废），
  只在飞行前 1 s 内生效（`LEAD_ACCEL_S`），上限 12 m/s²。
* 瞄准高度 = `units[etype].height * 0.5`（`_ringSolve` 1078 / `_leadMarkers` 1205）。
* 服务端只做两件事：`PlayerInput` 把视线写进 `sight_yaw/sight_pitch`（`Gunnery` 让车体
  和手臂去追），`Weapons.fire` 用**玩家设定的零点** `zero_at(pl.zero_range)` 解仰角，
  然后**逐发**叠散布。

### 1.2 服务端 AI

```
Sense.run ──▶ target ──▶ EngageMech.run ──▶ Engage.ground_aim       Support/Engage.cs:43
                                             │  eye 目视解 solve_lead(速度=min mean_speed)
                                             ▼
                                    lay(): sight_yaw/sight_pitch（夹到 ±0.45/0.75）
                                             ▼
                        Engage.trigger(): |want_yaw - yaw| < 0.14 rad 且 |Δpitch| < 0.02
                                             ▼
                        Weapons.fire → zero_pitch()（对着视点调零）        Weapons.cs:272
```

* `Engage.ground_aim`：目标点 = `pos.y + 2.0`（记忆点 `+1.5`）；提前量速度 =
  **能打到它的臂里最慢的 `MathUtil.mean_speed`**（解析的「加速-巡航」平均速度），
  `solve_lead(eye, at, tvel, speed, gravity: 0)` 迭代 3 次；无加速度项。
* `Engage.trigger`：方位容差**定值 0.14 rad**（8°，2 km 上 280 m），俯仰 0.02 rad；
  第二只手按 `RACK_REACH 2200` / `ai_range` 放行。
* `EngageMech.lay`：视线夹到手臂机械行程 `MECH_PITCH_MIN/MAX`。
* `Weapons.fire`：无主（AI）的机甲用 `Ballistics.zero_pitch()` ——**沿炮口射线找第一个挡住
  的东西**（地面/墙/树/单位球），解仰角去命中**那个点**。

---

## 2. 逐条差异

| # | 项 | 客户端 | 服务端 AI | 后果 | 本次 |
| --- | --- | --- | --- | --- | --- |
| 1 | 提前量的弹速 | 每臂按**自己**的 `flightTo` 精确求飞行时间 | `min(mean_speed(臂))` 一个数给整条视线 | 一只慢弹把另一只手也带偏（§3 表 2） | 修 |
| 2 | `mean_speed` 的火箭 | — | 火箭巢 `burn` 未设 → 退化成 **55 m/s** | 800 m 偏前 **165 m**（D1） | 修 |
| 3 | 解算形式 | 解**视线仰角 `p`**（调零 + 弹道积分收敛） | 解**拦截点**，仰角交给 `zero_pitch` | AI 的仰角依赖射线撞到什么（D2） | 修 |
| 4 | 调零基准 | 玩家设定的零点距离（500–4000） | 视点距离（射线尽头）；平地 = 1.17 × 目标距离 | 2.6 km 高 3.3 m，且过山脊会跳 | 修 |
| 5 | 瞄准高度 | `height * 0.5` | 定值 `+2.0`（记忆点 `+1.5`） | 打机甲低 1.25 m，打 APC 高 0.6 m | 修成 `height*0.5`；**几何中点本身不修**（§8.1） |
| 6 | 炮口/眼位 | 逐臂 `_muzzle`（`reach`、`rack.elev`、`rack.bolted`）+ 眼位分开 | 全部从眼位解，炮口的 4.3 m 前移 / 1.43 m 侧移不进解算 | 近距几米级视差 | 修 |
| 7 | 目标的加速度 | `net.js` 实测（EMA 0.25 s，上限 12 m/s²，1 s 内生效） | 无 | 起步/刹车的车体、转弯的导弹按匀速解 | 修 |
| 8 | 时间基准 | 解**开火瞬间**（`_firePose` + `_fireLag`，60–200 ms） | 解**本 tick**（同 tick 内开火） | 两侧都正确，**不可统一**（§4.3） | 记为差异 |
| 9 | 触发门限 | 无（玩家看弹着标记自己决定） | `0.14 rad` / `0.02 rad` 定值 | 2 km 方位容差 280 m（D3） | 修 |
| 10 | 哪只手说了算 | 玩家在环上**选**（吸附） | 一只更慢的手（含制导弹）绑架整条视线 | 机炮永远打在别的提前量上 | 修（主手策略） |
| 11 | 制导弹的提前量 | 不画环，瞄视线 | 同样用 `mean_speed` 解 | SACLOS 的 `drag`、攻顶的气动两侧都没建模 | 统一同一份估计量即可 |
| 12 | 视点射线步长 | `MARCH = 24 m` | `MARCH = 8 m`（`Ballistics.cs:19`） | 顺坡时距离读数与视点不一致 | 客户端顺手对齐（低优先） |
| 13 | 视线俯仰夹取 | 硬编码 `(-0.6, 0.9)` | `MECH_PITCH_MIN/MAX`（-0.45 / 0.75） | 高处目标的可瞄范围不同 | 客户端读同一组常数 |

---

## 3. 实测（模型的数字，落地后由 §6/§7 取代）

方法：把两侧的求解律各自照抄成与服务器同构的 1/30 s 积分器（平地、无风、无烟、目标沿
+z 横穿 12 m/s、射手在原点、机甲朝 +x），两侧都用真实弹道从炮口打出去，量「弹道与目标
命中球的最近距离」。脚本是临时的、不进仓库。

**表 1 —— 30 mm 机炮，APC 靶（几何中心 1.4 m），横穿 12 m/s。每格：弹道到瞄准点的最近
距离 / 对命中球的判定（余隙 < 0 才算命中）：**

| 距离 | AI | 客户端 |
| --- | --- | --- |
| 400 m | 1.62 m / miss | 0.07 m / **HIT** |
| 800 m | 1.50 m / miss | 0.42 m / **HIT** |
| 1200 m | 1.38 m / miss | 0.37 m / **HIT** |
| 1600 m | 1.52 m / **HIT** | 0.32 m / **HIT** |
| 2000 m | 2.08 m / **HIT** | 0.28 m / **HIT** |
| 2600 m | 3.64 m / miss | 0.41 m / **HIT** |

**表 2 —— 同一发机炮，但射手是「带火箭巢的 AI 机甲」（占 AI 机甲的 30 %）：**

| 距离 | AI（今天） | AI + 只修 D1 | 客户端 |
| --- | --- | --- | --- |
| 400 m | 81.7 m | 7.5 m | 0.07 m |
| 800 m | 164.7 m | 10.9 m | 0.42 m |
| 1200 m | 247.7 m | 14.3 m | 0.37 m |
| 1600 m | 330.7 m | 17.7 m | 0.32 m |
| 2000 m | 413.8 m | 21.2 m | 0.28 m |
| 2600 m | 538.3 m | 26.5 m | 0.41 m |

第三列说明：修 D1（`burn == 0` 时用 `(maxvel - vel)/thrust` 当燃烧时间）只解决「8 倍
提前量」，剩下的 7–26 m 是「**一条视线只能表达一个提前量**」本身的代价——机炮飞行时间
0.4–2.4 s，火箭巢接近 2–5 s，两者不可能共用一个拦截点。这正是 §4.4 主手策略要解决的。

**范围声明**：以上是模型，不是战斗统计；没有散布、斜坡、`Gunnery` 的滞后、烟幕。
它只说明量级和方向，判断「改完到底好没好」看 §6 与 §7。

---

## 4. 统一设计

### 4.1 一条解算律

> **一门炮有一把「调零距离」`Z`。火控解出的是：让一发按 `Z` 调零的炮弹，落在目标将要
> 出现的那一点上的那条视线。**

这条律客户端今天已经实现了（`leadPoint` 的内层 = `zeroAt(Z)` + `flightTo` + 对 `p`
迭代）。服务端 AI 改成同一条律，只需把 `Z` 从「视点距离」换成「拦截点距离」——AI 没有
准星可读，它想知道的就是那个点在多远处。`Z` 与解本身有关（等于「解出来那一点有多远」），
所以 AI 侧把它当**自洽项**迭代：`Z := |point − muzzle|` 再解一遍，两轮即收敛到厘米。
`Ballistics.zero_pitch`（对着视点调零）随之删掉，`sight_point` 只留着算准星的距离读数。

### 4.2 两侧的内核

```csharp
// Core/FireControl.cs —— 纯函数，只吃数字
public readonly struct Shot {
    public Vec3 muzzle;          // 炮口（Weapons.fire 那一套：reach / rack / 逐臂）
    public Vec3 eye;             // 视线的枢轴（眼位）
    public WeaponDef wd;
    public float zero;           // 调零距离 Z，m
    public float max_elev;       // 解算上限（Defs.ZERO_MAX_ELEV）
}
public readonly struct Track {   // 一个目标，按射手所知
    public Vec3 pos, vel, acc;   // 车体原点（贴地）、速度、加速度（已按新鲜度归零）
    public float aim;            // 应当命中的高度 = height * 0.5（§4.3）
    public float radius;         // 最大命中球半径（只给门限用）
}
public readonly struct Solution {
    public Vec3 point;           // 视线要压上的世界点
    public float yaw, pitch;     // 视线角（线上 `in` 消息就是它）
    public float elevation;      // 炮弹离开炮管时的仰角（开火直接用这个）
    public float tof;            // 到 point 的飞行时间
    public bool valid;
}
public static Solution solve(in Shot s, in Track t, int passes = 4);
```

解算顺序（与客户端今天逐行一致）：

1. `dm = |aim − muzzle|`（水平），`p := atan2(aim.y − muzzle.y, dm)`；
2. 内层 4 次：`yaw = bearing(muzzle, aim)`；`elev = zero_at(muzzle, yaw, p, wd, Z)`；
   `(alt, tof) = flight(wd, muzzle, yaw, elev, dm)`；`p += atan((aim.y − alt)/dm)`；
3. 外层 4 轮：`aim = pos + vel·tof + ½·acc·tof·min(tof, 1)`；
4. `point = eye + (cos yaw, tan p, sin yaw) · de`。

**`solution.elevation` 就是开火用的仰角**，`solution.pitch` 是视线角（机器该有的滞后长在
这两者的差里）。`Weapons.fire` 不再自己解第二遍；直射弹的机械偏置 `bias` 被解算吸收，
制导弹保持原样（`Weapons.fire` 今天就是这么分的）。散布**不在这里**（§4.7）。

JS 侧是同一份公式，住在新的 `frontend/src/fcs.js`（`map.js` 的 `leadPoint` / `zeroAt` /
`solveElevation` / `flightTo` 搬过去，`map.js` re-export 保持兼容）。今天它们已经是纯的
（只 import `math.js`），搬家零行为变化。

### 4.3 两个策略旋钮 + 一条规则

| 项 | 玩家 | AI 班组 | 为什么 |
| --- | --- | --- | --- |
| **零点 `Z`**（旋钮） | 刻度盘 500–4000，默认 1000 | 拦截点距离 | 玩家需要准星在山脊上不跳；AI 没有准星可读，直接要那一点 |
| **主手**（旋钮） | 玩家选中的那个环（吸附） | §4.4 的策略常量 | 一条视线一次只能表达一个提前量，谁说了算是产品选择 |
| **瞄准高度**（规则） | `height * 0.5` | `height * 0.5` | 两侧同一个式子；几何中点是否最优**本次不修**（§8.1） |

**时间基准不可统一**（§2 第 8 行）：玩家那一枪在 60–200 ms 之后才离开炮口，所以客户端
解的是「开火瞬间」的位姿；AI 在同一 tick 内解、同一 tick 内开火，用当拍状态就是正确的。
两侧各自供应自己的时刻，求解器只吃数据。

### 4.4 主手（一条视线，两个提前量）

可选策略（先用 §6 的用例打分，再定）：

* `SLOWEST`（今天的行为）：视线压在飞行时间最长的那只手上。快弹按慢弹的提前量打就是
  **偏前**（§3 表 2 第三列：7–26 m）。
* `FASTEST`：视线压在飞行时间最短那手上，慢弹那只手只在「自己的解与视线夹角 < 自己的
  门限」时开火（慢弹基本不对移动目标开火）。
* `PUNCH`：按「单发期望伤害 × 命中概率」选（105 mm / SACLOS / 攻顶优先）。

配套规则：**每只手各自解一份 `Solution`**，`point` 用来算环（客户端）与门限（AI）；
只有主手的 `point` 写进 `sight_yaw/sight_pitch`。策略常量进 `Defs`（`AI_MASTER_ARM`）。

### 4.5 触发门限：按目标的角径

```
tol = atan((track.radius + wd.hit_r) / dist)     // 方位与俯仰同一个式子
```

`Engage.trigger_intercept` 今天已经是这个式子（`atan((prox + hit_r)/d)`），地面战没跟上。
0.14 rad 在 2 km 上是 280 m——AI「洒水」的一部分就来自这里。改完开火时机的尺度与
「打不打得中」的尺度一致，代价是远距离开火次数下降，这要在 §7 里量（`--check battle`
击杀数不许降）。

### 4.6 反导同源

导弹防御不需要第二套数学：`Track{ pos = threat.pos, vel = threat.vel, acc = 0,
aim = 0, radius = wd.hit_r }`，`Z` 同样取拦截点距离，`Solution.point` 就是炮口该压的
那一点；门限本来就是角径式。改完之后 `MathUtil.solve_lead` 不再有调用者，
`--check intercept`（今天 34/36）是这一步的验收。

### 4.7 火控与散布的边界（本次只画线，不动实现）

| 属于火控（本次改） | 属于物理/散布（本次不动） |
| --- | --- |
| 视线角 `yaw/pitch`、仰角 `elevation`、拦截点、飞行时间 | `wd.spread`、每发的高斯扰动、`salvo` 的逐发递减 |
| 目标位置/速度/加速度的**取值规则**（谁在哪一刻知道什么） | 弹道积分本身（`Flights`）、命中判定（`FirstHit`）、伤害（`Combat`） |
| 门限：目标角径 + `hit_r`（纯几何） | `rng.gauss` 的一切用法；`Attack`/`Support` 的弹药与射速 |

落地的三条约束：

1. **求解器没有随机数输入**，也不读 `wd.spread` —— 这是 §6 能写成确定性单元测试的前提；
2. **散布仍在 `Weapons.fire` 里逐发叠加**（`Weapons.cs:280-285`：`sp = wd.spread * (k == 0 ? 1 : 0.6)`、
   `dy = yaw + gauss(0, sp)`、`dp = elev + gauss(0, sp)`），位置不变、行为不变；
3. **门限里不出现散布**（早先版本曾想把 `2 × spread` 当门限下限，那会把弹道学揉进取舍
   决策，撤销）。

**服务端现在本来就是分开的**：`Weapons.fire` 先解仰角（`Weapons.cs:272` / `274`），
之后才在 `k` 循环里叠散布，`Engage` 的门限只用几何量。所以这一步不需要剥离代码，
只需把它**记成约束并守住**：`FireControl.solve` 的签名里没有 `Rng`、没有 `spread`
字段，§6.6 的用例表里也没有散布这一维——评审时看签名与用例表即可。

### 4.8 客户端要改的三件小事

1. 视线俯仰夹取从硬编码 `(-0.6, 0.9)` 换成与 `MECH_PITCH_MIN/MAX` 同源的一组常数。
2. `map.js::sightPoint` 的 `MARCH` 24 → 8，与服务端一致（同一个视点、同一段距离读数）。
3. `leadPoint` / `zeroAt` 迁进 `fcs.js`，`map.js` re-export；此后这一份是 §6 的被测对象。

**不改的东西**：玩家的火控仍在客户端解算，服务端不下发「理想瞄准点」
（边界见 `docs/plans/client_stack_report.md` §6.1）。统一的是**求解律**，不是权限。

---

## 5. 修复服务端 AI 火控：三处硬缺陷，三步

（S1–S6 全部已落地，见文首；下表是当时的步骤与验收口径。）

| 步 | 缺陷 | 修法 | 单元测试（§6） | 集成验证（§7） | 回滚 |
| --- | --- | --- | --- | --- | --- |
| **S1** | D1 | `mean_speed`：`burn == 0` 时按 `(maxvel − vel)/thrust` 估燃烧时间（火箭巢的电机在 `Flights.Rocket` 里本来就是「一直推到 `maxvel`」，所以不改 `burn` 本身，只修估计量） | C# runner 里一条常数断言：`mean_speed(rocket, 800) ≈ 450 m/s`（S3 之后随函数退场删掉） | `--check fcs` 表 2 的 AI 列降到 7–26 m | 一行 |
| **S2** | — | 抽内核 + 建测试：`Core/FireControl.cs`、`frontend/src/fcs.js`、`frontend/src/package.json`（3 行，§6.5）、`tests/cases/` + `tests/frontend/` + `tests/backend/` + `run_tests.sh`、`Harness/FcsCheck.cs`，以及 `MechServer.csproj` 里 `tests/**` 的 `Compile`/`Content`/`None` 三条 `Remove`（少了 content 那两条，Web SDK 会把这个工程自己的 `bin/` 拷进输出，一层一层地套）。AI 仍走旧路径，测试给出**基线** | `./run_tests.sh` 全绿；基线表 | `--check fcs` 基线 | 全是新文件 |
| **S3** | D2 + 结构差异 | AI 改用 `FireControl.solve`：`Z = 拦截点距离`（自洽两轮）、`aim = height*0.5`、炮口用 `Weapons.launch_geometry`、目标加速度（`ai_tacc`，按 0.25 s EMA、> 25 m/s² 丢弃、> 0.5 s 过期）、主手策略 `Defs.AI_MASTER_ARM`；删 `Ballistics.zero_pitch` 与随后的死代码 `sight_point` / `_unit_hit` / `World.aim_spheres` | L1 语义 + L2 一致性全绿 | `--check fcs` 两列都在 1–3 m；`--check battle` 击毁 402 → 411（炮弹少 35 %） | `AI_MASTER_ARM` 换档即可 |
| **S4** | D3 + 反导同源 | 门限改角径式（方位）；俯仰留机械常数 `PITCH_TOL`；`Engage.intercept_aim` 改走求解器 | 门限用例（角径式在 400/2000 m 的取值） | `--check battle` 击杀/分钟；`--check intercept` 22–26 / 32（改前 15 / 32） | 常量回退 |
| **S5** | — | 客户端三件小事（§4.8）+ `map.js` 的 `MARCH` 24 → 8（与服务端采样同步） | L1/L2 对 JS 侧全绿 | 靶场肉眼 + 截图 | 前端独立 |
| **S6** | — | 文档与版本：`docs/product/weapons.md`（§火控自动调零 / §地面预测 / §预瞄解算）、`screens.md` 2.8、`CHANGELOG.md`、`MechServer.csproj` 1.20.0 → 1.21.0 | 版本号在启动页与构建一致 | — | — |

S3 落地后 `Ballistics.cs` 的职责收窄为：`sight_point`（视点/距离读数）、
`solve_elevation`（闭式直射 + 数值火箭）、`flight`（积分），都是 `FireControl` 的零件。

---

## 6. 验证 I：独立单元测试（不挂在 `--check` 上）

### 6.1 内核是纯函数

| 侧 | 内核 | 现在住在哪 | 抽出来以后 |
| --- | --- | --- | --- |
| C# | `FireControl.solve(in Shot, in Track)` | `Ballistics.cs` + `MathUtil.solve_lead` + `Engage.ground_aim` | `Core/FireControl.cs`，不碰 `World` / ECS / `Rng` / 日志 |
| JS | `solve(shot, track)` | `map.js` 的 `leadPoint` / `zeroAt` / `solveElevation` / `flightTo`（已经纯） | `frontend/src/fcs.js`，不碰 `Game` / `net` / `MapData` |

两侧各 ~120 行，公式逐行对应。它们不是同一份源（JS 与 C# 无法共享），但**必须是同一个
函数**——由用例表（输入）与代码里的容差常数（断言）钉住，而不是靠纪律。

```
tests/
  cases/fcs.json               # 输入用例：两侧唯一的输入源
  frontend/support/tol.mjs     # 容差常数（JS 侧）
  frontend/fcs/cases.mjs       # 读用例 + 自己的积分器（L1 语义）
  frontend/fcs/run.test.mjs    # node:test runner
  frontend/fcs/emit.mjs        # 写 /tmp/mech-fcs-js.json（L2 用）
  backend/Mech.Tests.csproj    # console，ProjectReference 到 MechServer.csproj
  backend/Program.cs           # 参数解析 + 入口（--filter / --json / --cases）
  backend/Support/MiniTest.cs  # [Test] + 反射发现 + Assert + Tol（~70 行）
  backend/Support/Cases.cs     # 读同一份 cases.json
  backend/Fcs/FcsTests.cs      # 求解器用例：读用例逐条跑
  compare.mjs                  # 跨侧比对，同一组常数
frontend/src/package.json      # {"type":"module"}，3 行，给 Node 用（§6.5）
run_tests.sh                   # 统一入口（§6.5）
```

### 6.2 用例 = 纯输入

`tests/cases/fcs.json` 只放**输入**与一个语义标签，`shot` / `track` 的字段与 §4.2 的
结构一一对应：

```json
{
  "id": "ac30_800_cross12",
  "tag": "",
  "shot": {
    "muzzle": [4.3, 3.55, -1.43], "eye": [-0.62, 5.05, 0.0],
    "zero": 1000, "max_elev": 1.05,
    "weapon": { "kind": "bullet", "vel": 1080, "g": 9.81, "thrust": 0,
                "maxvel": 0, "life": 9.3, "bias": 0, "hit_r": 0.08 }
  },
  "track": { "pos": [800, 1.4, 0], "vel": [0, 0, 12], "acc": [2, 0],
             "aim": 1.4, "radius": 1.7, "acc_age": 0 }
}
```

* **没有 `expect` 块**：期望值不写在数据里，全部由代码里的常数判定（§6.3）。
  数据只负责「这一枪是什么条件」。
* `tag` 是边界用例的意图标签，只有四个值：`""` / `"below_min_dist"` / `"out_of_life"` /
  `"stale_acc"`。runner 对带 tag 的用例断言 `valid == false`（`stale_acc` 则断言解在
  `acc = 0` 下仍然有效且与同条件的 `acc=0` 用例一致）——这是「两侧在回退分支上一致」
  的那一格。
* `weapon` 是 `WeaponDef` 的一个子集（求解器只读这些字段），两侧各自从自己的 defs 里
  填，测试里直接给数字，跑测试不需要 `Defs` 初始化，也不需要启动服务器。

两侧输出同格式，`/tmp/<side>.json` 一行一条：

```json
{ "id": "ac30_800_cross12", "valid": true,
  "point": [808.7, 1.36, 9.15], "yaw": 0.01335, "pitch": -0.00341,
  "elevation": 0.000797, "tof": 0.767 }
```

### 6.3 容差 = 两侧代码里的同一组常数

不设 `tolerance.json`：容差是常数，写在代码里，两侧各一份、数值一致（改就两边一起改，
review 时一眼能对）。JS 在 `tests/frontend/support/tol.mjs`、C# 在 `tests/backend/Support/MiniTest.cs` 的 `Tol` 类：

```js
const TOL = {
  point: (d) => Math.max(0.25, 1e-4 * d),   // m，随距离放宽
  tof: 0.02,                                // s
  angle: 1e-3,                              // rad（elevation / yaw / pitch）
  miss: (r) => 0.5 * r,                     // L1：落在目标半径的一半以内
};
```

| 字段 | 容差 | 为什么是这个数 |
| --- | --- | --- |
| `point` | `max(0.25 m, 1e-4 × dist)` | 1 km 上散布是 1.1 m，0.25 m 是它的 1/4 |
| `tof` | 0.02 s | 25 m/s 横穿 = 0.5 m，同样在散布之下 |
| `elevation` / `yaw` / `pitch` | 1 mrad（0.057°） | 1 km 上 1 m |
| L1 `miss` | `0.5 × radius` | APC 0.85 m / 机甲 0.72 m / 步兵 0.33 m |
| `valid` | 完全相等 | 回退分支最容易漂 |

**这个量级不是拍的**：本机实测，同一套算法分别用 float32（C# 的 `float`）与 float64
（JS 的 `number`）跑同一批用例，解算输出的 `point` 最坏差 **0.13 mm**（4 km 火箭），
4 km 弹道积分后的高度差 **< 0.1 mm**。所以容差不是给浮点精度留的，它吸收的是**两份
独立实现**的差异：收敛阈值（`leadPoint` 的 0.15 m）、迭代次数（4/4 vs 3）、视点步长
（8 m vs 24 m）。

### 6.4 三层断言

1. **L1 语义（每侧自证，主体）**：用**这一侧自己的**积分器把 `elevation` 从 `muzzle`
   打出去，量它与「目标在 `tof` 时刻的位置 `pos + vel·tof + ½·acc·tof²`」的距离，
   断言 `≤ TOL.miss(radius)`。不需要另一侧存在，是最纯的单元测试。
2. **L2 一致性（跨侧）**：两份 `<side>.json` 按 §6.3 逐字段比。
3. **L3 边界**：带 `tag` 的用例，两侧 `valid` 必须一致；无效时 `point` 为 `null`。

### 6.5 两侧各自怎么跑，和一个 `run_tests.sh`

**JS：Node 内建 `node:test`，零依赖。**

```bash
node --test tests/frontend/*/*.test.mjs                          # 直接跑（TAP，失败退出码非 0）
node --test --test-name-pattern ac30 tests/frontend/*/*.test.mjs # 只跑某几条
```

`t.test()` 一层一条用例，`node:assert` 做断言，够用而且不引任何包
（jest / vitest 为一组纯函数断言拖进几百个包，不划算）。两条本机实测过的坑要写进脚本：

1. **`node --test <目录>` 在这台 Node 22.18 上会走 `require` 路径**、报
   `Cannot find module '.../tests/frontend'`；必须给**文件 glob**（`tests/frontend/*/*.test.mjs`）。
2. `frontend/src/*.js` 是 ESM 源码，而 `frontend/package.json` **没有** `type` 字段
   （它必须有 CJS 语义，因为 `webpack.config.js` 用 `require`）。Node 22 会自动按语法
   识别成 ESM，但每次打一条 `MODULE_TYPELESS_PACKAGE_JSON` 警告并重新解析一遍。
   解决办法：在 `frontend/src/` 放一个 3 行的 `package.json`（`{"type":"module"}`）——
   离源文件最近的 package.json 就是它，警告没了；webpack 完全不受影响（那一个
   `frontend/package.json` 保持无 `type`，`webpack.config.js` 照旧是 CJS）。

**C#：一个普通 console 工程 + 自己 60 行 runner，零包。**

```bash
./dotnet.sh run --project tests/backend -c Release                      # 全部，报告打 stdout
./dotnet.sh run --project tests/backend -c Release -- --filter Fcs      # 按名字过滤
./dotnet.sh run --project tests/backend -c Release -- --json /tmp/cs.json  # 另写解算结果表
```

`--json` 写的是**解算结果表**（`{id, valid, point, yaw, pitch, elevation, tof}`，§6.2
的格式，给 `compare` 吃）；测试报告（`ok / FAIL` 与汇总）只打 stdout，两者不混。

* **不引 `Microsoft.NET.Test.Sdk` / xunit / NUnit / MSTest**：那是 3 个以上包，
  而本仓库至今零第三方依赖，不为跑测试破例。`dotnet test` 唯一多给的是 IDE 里的
  绿点，这里不需要。
* `MiniTest.cs`（60–80 行）：`[Test]` 特性 + 反射发现 + `--filter`，以及测试报告；
  `Assert.Close/True`；失败打印 `用例名: 实际 vs 期望 (tol 四舍五入到 R)`，退出码 1；
  §6.3 的那组 `Tol` 常数也在这个文件里（两侧同一组数）。
* 两条同样实测过的坑：
  1. `MechServer.csproj` 的 `EnableDefaultCompileItems` 会把 `tests/**/*.cs` 一起编译，
     两份 `Program.cs` 立刻撞成 **CS8802**。必须在 `MechServer.csproj` 里加一个
     **`ItemGroup`**：`<Compile Remove="tests/**/*.cs" />`（写进 `PropertyGroup` 会报
     `MSB4066`）。这是 S2 里唯一一处改动服务端工程文件的地方。
  2. 测试工程用 `ProjectReference ../../MechServer.csproj` 引用（Web SDK 的 exe 可以被
     引用，实测通过）。以后若按 `client_stack_report.md` §6.2 抽出 `Mech.Core`，
     这里改成引用那个库，一行。

**统一入口 `run_tests.sh`**（仓库根，与 `dev.sh` / `dotnet.sh` 并列）：

```bash
./run_tests.sh                 # cs -> js -> compare，任一失败即非 0
./run_tests.sh js              # 只跑前端
./run_tests.sh cs              # 只跑后端
./run_tests.sh compare         # 只做跨侧比对（读上一次两份输出）
./run_tests.sh --filter ac30   # 过滤（透传给 C#；JS 侧用 --test-name-pattern）
```

* 顺序固定 `cs → js → compare`，`set -euo pipefail`：先红先停，退出码即第一个失败者。
* 两侧各写 `${TMPDIR:-/tmp}/mech-fcs-{cs,js}.json`（同 §6.2 的解算结果表格式），
  `compare` 只读这两份文件 —— 所以「两边各跑各的、最后只比对」也成立。
* 全部走 `./dotnet.sh`（沙箱里裸 `dotnet` 会在只读 `$HOME` 上直接死）和 `node`：
  **不启服务端、不跑 `npm install`、不碰 `assets/`**，几条命令加起来是秒级。
* 与 `dev.sh` 分工清楚：`dev.sh` 管服务进程，`run_tests.sh` 管测试；CI / pre-commit
  直接调后者。

**为什么是这套最小的东西**

| 选择 | 依赖数 | 不选另一个的理由 |
| --- | --- | --- |
| `node:test` + `node:assert` | 0（Node 22 内建） | jest / vitest 为 60 条纯函数断言拖进几百个包 |
| console runner + 反射（60 行） | 0 | `dotnet test` = Test SDK + 框架 + 适配器，破零依赖 |
| `cases.json` + 两个 runner | 0（`System.Text.Json` 内建） | 跨语言共享一个测试框架本来就不存在，硬凑更重 |

### 6.6 用例清单（落地版本 42 条，两侧共用同一份）

| 维度 | 取值 | 为什么 |
| --- | --- | --- |
| 武器 | `ac30` / `mg127` / `g105` / `rocket` / `saclos` / `top` | 三种弹速 + 两种推力弹；`saclos` / `top` 走炮口偏移那条路 |
| 距离 | 400 / 800 / 1200 / 2000 / 3000 / 4000 | 覆盖 `MIN_DIST` 与 4 km 的 `life` 边界 |
| 零点 `Z` | 500 / 1000 / 2000 / 4000 | 玩家刻度盘全部档位 |
| 目标运动 | 静止 / 横穿 12 / 横穿 25 / 爬升 `vy = 40` | 爬升那组是导弹与攻顶弹的判据 |
| 目标类型 | APC（1.4 / r1.7）/ 机甲（3.25 / r1.45）/ 步兵（0.9 / r0.65） | 高度与半径都会改变解 |
| 炮口 | 左臂 / 右臂 / `rack.bolted`（攻顶 42°）/ `rack.elev`（SACLOS +0.18） | 炮口是解算输入之一，两侧必须在同一处开口 |
| 加速度 | `acc=0` / `acc=8` / `acc_age=0.6`（`stale_acc`） | 过期必须按 0 处理，最容易两侧不一致 |
| 边界 tag | `below_min_dist` / `out_of_life` / `out_of_elev` / `stale_acc` | 回退分支的 `valid` 必须一致；`stale_acc` 还要与同条件的 `acc=0` 那条逐字段相等 |
| 快慢弹同队 | `rocket` 与 `ac30` 同一次交战的两份解 | 主手策略的打分输入（§4.4） |

地形、建筑、树冠、烟幕**不进**单元测试（纯函数不吃地图），它们属于 §7。

### 6.7 两侧能有多近似

| 层 | 能不能共用 | 现状 |
| --- | --- | --- |
| 解算内核（~120 行） | 同一个函数，两份实现 | `Core/FireControl.cs` + `frontend/src/fcs.js`，由 §6.2/§6.3 钉住 |
| 输入适配 | **不能** | C# 读 ECS；JS 读快照 + `_fireLag()` + `net.js` 的加速度估计 |
| 时间基准 | **不能** | AI 同 tick 解同 tick 打；玩家解 60–200 ms 之后的位姿 |
| 点火策略 | **不能也不该** | AI 的主手与门限在服务端；玩家的环吸附在 `_sightAim` |
| 散布与随机 | 只在服务端 | `Weapons.fire` 的 `rng.gauss`，客户端只预测期望值 |

结论：**公式能独立出来、能一模一样；「什么时候扣扳机」不能共用**。想让内核也只留一份，
只有一条路——客户端换 C#（`client_stack_report.md` §6.1），那时 `FireControl.cs` 直接
`ProjectReference` 过去，本章退化成回归测试。

---

## 7. 验证 II：集成层（真实的 World）

单元测试证明「同一个函数」，集成层证明「同一个世界里的表现」。薄薄一层。

### 7.1 `--check fcs`（`Harness/FcsCheck.cs` + `Program.cs` 分派）

平地测试图（`Modes/Range` 的场地足够，或直接 `new World`），行 = `(射手配置, 武器,
目标类型, 距离, 目标速度, 坡度, 遮挡)`，每行打**两枪**，两枪都走服务端真实开火链
（`Gunnery` 追车体与手臂 → `Weapons.fire` → 弹丸飞行 → 命中判定），唯一区别是解从哪来：

* **A 枪 = AI 班组**：`Sense → EngageMech → Engage.ground_aim → Weapons.fire`；
* **B 枪 = 玩家**：`add_player` 的真机甲，`pl.zero_range = 1000`，harness 每一 tick
  用**同一个** `FireControl.solve` 解一次、写进 `pl.in_yaw/in_pitch`（即「准星压在环上」
  那一下），`pl.fire` 只在解出来的俯仰与炮管差 < 0.01 rad 时按下。两条链唯一的区别
  是"解从哪来"：A 是 `Engage` 自己解，B 是 harness 解——同一个函数，两套输入适配。

量弹道对目标命中球的**最近余隙**（`< 0` = 命中）。断言：

1. 每行 A、B 的中位余隙差 **< 1 m**，且每行 `median(clearance) < 0` 的比例 ≥ 90 %；
2. 相对 S2 的基线，两列只许变小；
3. 覆盖：400–3200 m 每 200 m 一格、速度 0/12/25、机甲与 APC 两种靶，另加至少一格
   迎面上坡（视点调零的毛病只在斜坡与遮挡下发作）；
4. 每行两枪同配置（同一把炮、同一个 `Z`、同一个 `aim`）。

### 7.2 产品级回归（每次改火控都要跑）

| 命令 | 看什么 | 门槛 |
| --- | --- | --- |
| `--check battle --minutes 12` | 城镇拿下数、击杀数（`events` 里的 `death=`）、`MECH_PERF` 的 ms/tick | 改前 4 场 / 改后 3 场：击毁 402 → 411、炮弹 5.4 万 → 3.5 万；1.3–1.6 ms/tick（同量级） |
| `--check intercept` | 导弹被击落数 | 实测 22–26 / 32，同表基线 15 / 32（旧文的 34/36 是更早那张 9 组表） |
| `--check range --minutes 2` | 靶场投弹、`MECH_RECORD` 弹道 | 正常写出 |
| `--check net --minutes 12` | 线上字节 | 不变（本次不加任何帧内字段） |

浏览器侧最后确认一次：`./dev.sh range`，`g.net.send({t:'debug_spawn', etype:3, dist:800})`
放一个目标，开瞄准辅助，看两只手的环压上去之后弹着点是否落在环心（截图存档）。

---

## 8. 本次不修 / 已知债

### 8.1 几何中点（明确不修）

命中球不是以车体几何中心对称的：机甲的腿 1.45(r1.45)、躯干 4.15(r1.30)、双臂
3.55(r0.85)，而 `height/2 = 3.25`，客户端那 0.42 m 的系统残差把它压到躯干球下缘之下，
实测余隙 **0.02 m**。本次**只把 AI 的 `+2.0` 统一到 `height * 0.5`**，让两侧同一个式子；
「瞄哪儿才最稳」留给以后（候选做法：`UnitDef.aim` 单位数据，`--check fcs` 量余隙选值）。
本文只留这段记录与 §6 的余隙输出，方便下一次开工时有基线。

### 8.2 制导弹的阻力与气动

SACLOS 的 `drag`、攻顶的 `flight.step` 都没有进 `flightTo` / `mean_speed`，两侧都只是
近似。统一后两侧**共用同一份估计量**（这是本次的目标），把气动模型做进火控是另一件事。

### 8.3 产品文档债（S6 结清）

`docs/product/weapons.md` 的「预瞄解算」一节落后于代码：说「快照里的速度是指令速度」
（实际 `vx/vy/vz` 是 `vel`，指令速度是 `vel_cmd`、不上线）、说「不检查遮挡」
（`_leadMarkers` 已走 `sightBlocked`）、说「目标未来高度取地形高度」（代码用 `py + half`）。
这三条在 S6 一起改。

---

## 9. 附录：实测口径

* 积分器：`vy -= g·dt; p += v·dt`，`dt = 1/30`，`thrust` 按 `min(maxvel, |v| + thrust·dt)`
  同比缩放（与 `Systems/Flights.cs` 一致）；
* 调零：直射走闭式 `atan2(v² − √(v⁴ − g(g d² + 2 h v²)), g d)`，推力弹走 6 次割线；
* 视线：平地，`sight_point` 等价于「射线与 y = 0 的交点」；
* 目标：车心 = 地面 + `height/2`；命中球取 `Defs.UNIT_DEFS` 的球（忽略左右臂球，
  弹道沿车体中线）；
* 余隙 = min over 球 of（最近距离 − 半径），`< 0` 记为 HIT；
* 浮点对比：同一算法分别用 `numpy.float32` 逐步舍入与原生 double 各跑一遍，
  比较输出 `point` 与 4 km 处的弹道高度；
* 平台：本机，与 `MECH_SEED` 无关（没有随机数）。
