# AI 火控复检：作弊嫌疑与缺陷清单（针对 1.21.0 `8463ae7`）

> **状态：已复核并修完（在 1.21.0 这轮工作之内）。** 本报告写于 `8463ae7`
> （`AI: one fire control law for the crews and the pilot`），当时是**只读复检**。
> 结论：新火控的解算律本身是对的（`./run_tests.sh` 42/42、`--check fcs` 六条全
> `ok`），但**围绕这条律的三处接线**有硬伤，其中一条让 APC 与步兵完全打不中人。
> **逐条判定与修法见 §10「复核结果」**（D2 的门限是物理正确、但**选哪条臂开火**是真
> 缺陷，E1/E2 按设计保留，E3 在客户端修）；`fcs_unify.md` 里"每击毁耗弹 134 → 86 发"
> 这类基准数在 D1 修好之前不可用，修完重测见 §10.9。
>
> 下面 §1–§9 是**当时的复检原文**，保留下来作为判定过程的记录；§10 是这轮的结论。
>
> | 级别 | 条目 | 一句话 |
> | --- | --- | --- |
> | **D1 回归** | 非机甲炮口落在车体原点（地面） | APC／步兵每一发都在自己脚下炸开；6 分钟战斗射击数 14461 → 5956 |
> | **D2 设计** | 副臂门限被炮口横距吃掉 | 机甲永远只有主臂开火（静止目标实测 33:0） |
> | **D3 回归** | 瞄准高度取射手自己的 `height/2` | 机甲打步兵瞄 3.25 m（人高 1.55 m），靠空炸引信兜住，RPG 直接飞过 |
> | **D4 缺陷** | `ai_tacc` 的"新鲜度"从不复位 | 换目标 0.5 s 之后加速度前置量恒为 0 |
> | **D5 规则违背** | 目标的 LOS 每 0.34–0.61 s 才复查一次 | 烟／脊线一合，机组仍按目标的**真实**位置解算，玩家侧的环、框、锁立刻掉 |
> | **E1–E3** | 其余信息／决策上的不对称 | 完美零位、锁定武器免锁、垂直速度前置（判定见 §7） |
>
> 复检方式：`/tmp` 里一个引用 `MechServer.csproj` 的探针工程驱动真实 `World`（无 socket、
> 无墙钟），并用 `git worktree` 在 `8463ae7^`（`c31c90b`）上跑同一份探针做对照。探针源码
> 见附录 A，未进入仓库——它找到的三条（D1、D3、D4）现在各有一行常驻在
> `./dotnet.sh run -- --check fcs` 里，见 §10.8。

---

## 1. 复现与量法

```bash
./dotnet.sh build
./run_tests.sh                                   # 42/42 一致：两侧算的是同一个函数
MECH_SEED=7 ./dotnet.sh run -- --check fcs       # 集成：机组 vs 玩家，六条 ok
MECH_SEED=3 ./dotnet.sh run -- --check battle --minutes 6
```

三个既有 harness 都**没有**覆盖到 D1：`--check fcs` 的射手固定是 `ETYPE_MECH`
（`Harness/FcsCheck.cs::Shoot`），`--check hit` 的射手也是 mech
（`Harness/InterceptCheck.cs::hits`），`--check intercept` 打的是 range 的机甲防空哨。
所以 D1 只能在探针里看见——这也是本文存在的原因。

探针的量法（与 `Harness/FcsCheck.cs` 同一套）：range 世界、`seed: 7`、平地
`(1200, 2100)`、一个 `crew: true` + `crew_engage` 的射手、一个 `crew: false` 的目标，
目标每 tick 被脚本按住不动（或按固定速度横穿），每 tick 扫掉其余实体，读
`world.events` 里的 `shot`（按 `arm` 分列）与 `boom`（记爆点到射手的地面距离）。
数字按量级读，单次运行。

---

## 2. D1（回归，最严重）：非机甲的炮口在车体原点，炮弹在脚下自爆

### 2.1 机制

`Systems/Weapons.cs::launch_geometry` 是本次新抽出来的"炮口唯一出口"，最后一段是：

```csharp
var origin = e.fv("pos").get3(eid);
if (e.uv("etype")[eid] != Defs.ETYPE_MECH) return origin;   // APC / 步兵 / 工事
```

改动前（`8463ae7^`）`Weapons.fire` 取的是 `var (origin, _, _) = aim_of(world, eid);`，也就是
**目视眼位**：APC 是 `pos.y + height * 0.85`（2.38 m），步兵 `pos.y + height * 0.8`，机甲是
`MathUtil.sight_eye`（5.05 m）。抽 `launch_geometry` 时把非机甲这一支改成了车体原点，
而车体 `pos.y` 就**恰好等于地面高度**。

`Core/Terrain.cs::ground_hits_many` 的第一句是：

```csharp
start_hit[k] = ... || p0[k * 3 + 1] <= height_at(x0, z0) + 1f;   // t = 0 就爆
```

于是炮口贴地的弹丸在第一 tick 的 leg 起点就被判为地面命中，`FirstHit` 写下
`HIT_GROUND t=0`，`Impact` 当场起爆——**射手的每一发都炸在自己的脚下**，射程、提前量、
仰角全部无关。（`+ 1f` 的余量意味着即使把炮口抬到 1 m 也还会自爆，真正的修法是炮口高度，
不是这个余量。）

### 2.2 实测

射手 `crew: true` + `crew_engage`，目标静止、脚本按住。`爆点` 是 `boom` 事件到射手的
地面距离中位数。

| 射手 → 目标（距离） | 现在 | `8463ae7^` |
| --- | --- | --- |
| APC `MG127` → APC 800 m | 299 发 / 299 爆，爆点 **0 m**，目标满血 | 285 发 / 264 爆，爆点中位 636 m |
| 步枪兵 `RIFLE556` → 步枪兵 300 m | 44 发 / 44 爆，爆点 **0 m** | 46 发 / 38 爆，爆点中位 630 m |
| AT 兵 `RPG` → APC 300 m | 2 发 / 2 爆，爆点 **0 m** | — |
| APC `MG127` → 步兵 400 m | 260 发，爆点 **0 m**，步兵满血 | — |
| 机甲 `AC30` → APC 1200 m（对照） | 24 发 / 17 爆，爆点 1200 m，`KILLED` | 49 发 / 46 爆，爆点中位 632 m，alive |

整场对照（`MECH_SEED=3`，6 分钟，同种子）：

| | 现在 | `8463ae7^` |
| --- | --- | --- |
| `shot` 事件 | **5 956** | 14 461 |
| `boom` 事件 | 5 989 | 14 444 |
| 步兵存活（etype 1 + 2） | 73 + 119 = **192** | 61 + 88 = 149 |
| tick 成本 | 0.99 ms | 1.04 ms |

步兵数量反过来涨了，正好是这个缺陷的指纹：步兵打不动任何人，只能等机甲和爆炸。

### 2.3 影响面

* 战场上的 APC（任何时刻十几台）与步兵（九成实体）**只是靶子**；它们仍然开火（`shot`
  事件照发、`cool` 照走、弹药照扣），所以从事件流上看不出来。
* `fcs_unify.md` 的行为基准数（134 → 86 发/击毁）是在这个状态下量的，只能反映机甲那一半。
* 客户端不受影响（玩家只出机甲），所以带去打一局也不容易发现——除非盯着 APC 打人。

### 2.4 修法方向（未实施）

`launch_geometry` 继续做唯一的门，但对三种底盘都给真正的炮口：机甲走 `muzzle_point`，
载具给炮塔座圈（`turret_yaw` + 炮塔高度），步兵给肩高。判据是"画出来的炮口 == 开火的
炮口 == `Engage.ShotOf` 解算的炮口"，这条 `AGENTS.md` 已经写明，现在只是三种底盘里断了
两种。补一条 harness：同一张武器表分别从 mech / APC / foot 三个射手打出去。

---

## 3. D2（设计）：副臂的门限被炮口横距吃掉，机甲永远只有主臂开火

### 3.1 机制

* 炮口横向相距 **2.86 m**：`Defs.MECH_ARM_MUZZLE` 里 `MOD_LEFT` / `MOD_RIGHT` 各是
  ∓1.43 m（同一高度 3.55 m）。
* `FireControl.solve` 返回的 `point` 是从**眼位**沿**炮口方位**投影出来的点，
  `EngageMech.lay` 把它写进 `sight_yaw`，车体 `yaw` 追这条视线 → 车体停在**主臂炮口**的
  方位上。
* `Engage.trigger` 的方位门限是 `atan((radius + hit_r) / dist)`：对 APC 是
  `atan((1.70 + 0.08) / d)`，半宽 1.78 m。副臂的方位差是**整个** 2.86 m（不是一半），
  与距离无关，永远大 1.6 倍。

实测（静止 APC 1200 m，8 s，`arm0 / arm1` 发数）：

| 机甲挂载 | arm0 | arm1 | 说明 |
| --- | --- | --- | --- |
| `AC30 + ROCKET` | 33 | **0** | 主臂 = 30 mm（最早到达） |
| `AC30 + TOPATTACK` | 23 | **0** | |
| `AC30 + SACLOS` | 33 | 1 | 偶发 |
| `AC30 + G105` | **0** | 1 | 主臂 = 105 mm，30 mm 反而沉默 |
| `G105 + AC30` | 1 | 0 | 同上 |
| `AC30` 单臂 | 26 | — | 正常工作（24 发/击毁 1 台） |
| `AC30 + AC30`，横穿 12 m/s | 9 | 10 | 两条解**相同**，车体扫过时两臂都开火 |

表里最后一行是关键：横穿目标时车体一直在转，`yaw` 会扫过两臂各自的解，于是副臂蹭到几发；
静止目标下车体稳定停在主臂的方位上，副臂**一发不发**。也就是说副臂现在不是"按自己的解
开火"，而是"等车体偶然扫过来"。实测那一发的 `err_yaw = 0.0021 rad`，而
`tol = atan(1.78 / 1200) = 0.00148 rad`。

### 3.2 为什么这是接线错、不是物理错

车体装甲上的炮管不可能各转各的方位，所以副臂那一发本来就沿**车体轴线**平行飞出去，
从 1.43 m 的横向偏置出发，落点偏 1.43 m——**在 1.78 m 的门限之内，是能命中的**。
现在的门限拿"炮口→目标的方位"去比"车体方位"，比的是横距而不是落点偏差，于是把一个
能命中的解判成不合法。

### 3.3 修法方向（未实施）

门限判**弹丸自己的落点偏差**，而不是炮口方位差：从该臂炮口沿车体 `yaw` + `aim_elev`
外推，取到目标最近点的横向偏差，与 `radius + hit_r` 比较。这套几何仓库里已经有了
（RWR 的 `rayMiss`，"两条射线一个容差"），不需要新写一份。俯仰那一半继续保持
`PITCH_TOL`（机械到位判据），理由不变。

---

## 4. D3（回归）：瞄准高度取射手自己的 `height/2`

`Systems/AI/Support/Engage.cs::ground_aim`：

```csharp
var d = Defs.UNIT_DEFS[c.etype[i]];                    // 射手
...
tetype = e.uv("etype")[tid];                           // 目标（只用来取 radius）
aim.at = new Vec3(tpos.x, tpos.y + d.height * 0.5f, tpos.z);
var track = new FireControl.Track(tpos, tvel, tacc, d.height * 0.5f, aim.radius);
```

`d` 是**射手**的类型定义：`aim_range`、`sight` 用它是对的，但 `height/2` 也跟着用了射手的。
客户端这一侧是目标的（`frontend/src/game.js::_ringSolve`、`_leadMarkers` 都读
`this.defs.units[e.type].height * 0.5`），改动前服务端是固定 `+2.0 m`。

代价（实测）：机甲（6.5 → 瞄 3.25 m）打步兵（1.8 m，命中球顶 1.55 m）时，落点在人头顶
1.7 m：

| 场景 400 m 静止 | 结果 |
| --- | --- |
| 机甲 `AC30` → 步枪兵 | 2 发即杀（**靠 30 mm 的 `prox` 空炸兜住**） |
| 机甲 `RPG` → 步枪兵 | 2 发，目标存活（RPG 无引信，直接飞过） |
| APC `MG127` → 步枪兵 | 260 发全落空（叠加 D1） |

反过来小射手打大目标瞄得偏低（步兵打机甲瞄 0.9 m，进底盘球），伤害还能接受；机甲打
APC 瞄 3.25 m 落在顶部球（中心 2.4 / 半径 1.2）里，余量只剩 0.35 m。也就是说这不是
"打不中"级别的 bug，而是"打哪一块"级别的偏差——真正被它废掉的是机甲的非引信弹。

同一处还有一处相关的不一致：`tetype` 在 `spotting`（`SUPPRESS` 或失目标）时被留成
`ETYPE_APC`，于是记忆点的 `radius` 与高度都是 APC 的。

---

## 5. D4（缺陷）：`ai_tacc` 的"新鲜度"从不复位，加速度前置量只活 0.5 s

`Systems/AI/Support/Crew.cs::measure_accel`：

```csharp
if (tid < 0 || (int)tacc[b + 5] != tid) { ... tacc[b + 4] = 0f; ... return; }  // 换目标才清零
...
tacc[b + 4] += dt;          // 之后每 tick 只增不减
```

`accel_of` 的判据是 `tacc[b + 4] > ACCEL_STALE`（0.5 s）→ 默认 0。可 `[b + 4]` 记的其实是
"距离**换目标**过了多久"，不是"距离最近一次新鲜样本"。所以同一个目标抱住半秒之后，
`accel_of` 永远返回 0。探针里持有同一目标 3 s 后读到 `age = 2.967`。

后果：`8463ae7` 的提交说明里"`ai_tacc` + `Sense` 按客户端的量法测目标拉力"只在换目标的
头半秒成立；转弯目标上的加速度前置量（客户端实测值 1.35 → 0.67 m）对 AI 是缺的。
这一条方向上是**对玩家有利**的——它不是作弊，是 AI 少了一块能力。

修法：每次 EMA 更新时把 `tacc[b + 4] = 0f`。

---

## 6. D5（违背"一条视线"）：目标的 LOS 每 0.34–0.61 s 才复查一次

这是本轮唯一一条**AI 看得见、玩家看不见**的东西，也是与 `AGENTS.md` 里"有一个视线，就是
`Sight.cs`；每一只眼都走它"直接冲突的一条。本节只按代码判读，未做实测（探针做到一半就停）。

### 6.1 机制（四级链条，都在代码里）

1. `Systems/AI/Sense.cs::run`：`r.timer -= dt; if (r.timer <= 0f) { r.timer = ACQUIRE *
   uniform(0.75f, 1.35f); r.target = _acquire(...); }`。`ACQUIRE = 0.45 s`，抖动后
   **0.34–0.61 s** 一次。
2. `_acquire` 是**唯一**调用 `Sight.clear` 的地方。对已经持有的目标，每 tick 只做两件事：
   `measure_accel`（读实时 `vel`）和"死了／是自己人 → 清空"。**没有视线**。
3. `Systems/AI/Support/Engage.cs::ground_aim`：非 `spotting` 分支每 tick
   `e.fv("pos").get3(tid)` / `e.fv("vel").get3(tid)`——读的是**真值**，不是 `spot_*` 记忆。
4. 于是烟／脊线切段视线之后，机组照旧按目标的真实位置与真实速度解算、照旧开火，直到下一次
   timer 到期；到期时 `_acquire` 返回 -1（或换成一个真有视线的目标），平台侧才
   `trigger = 0`（`EngageMech` / `EngageVehicle` / `EngageFoot.run` 里的
   `if (r.target < 0 && r.spot_t <= 0f)`）。

丢掉之后才回到 `spot_*`：`spotting = state == SUPPRESS || tid < 0`，`tvel = 0`，仍然**不查
视线**（唯一限制是 `ai_range`）。这一段是"对着记忆点压制射击"，合理；但要注意它也没有
LOS 门，而且记忆点的 `radius` 与高度都还是 APC 的默认值（见 §4）。

对照玩家侧：`frontend/src/game.js::_leadMarkers`（约 1250 行）与预瞄框（约 1288 行）每帧
过 `sightBlocked`，`Systems/Locking.cs` 每 tick 走 `Sight.clear`。**烟一合，玩家的环、框、
锁立刻消失，而 AI 的还在。**

### 6.2 为什么这是缺陷而不是"AI 更强"

烟雾的设计目的就是切断 5 s 的瞄准与锁定（`Smoke.cs`，45 m 球、5 s）。机组在这 5 s 里白拿
最多 0.61 s 的**真值**：不是"记得它在哪"，而是"它现在在哪"。方向上等于给 AI 一个短促的
透视窗，而窗的长度由 `ACQUIRE` 的抖动决定，与玩家能不能看见无关。

窗口的量级：0.34–0.61 s ≈ 10–18 tick；对被遮挡的目标通常足够走完 `gunnery` 的视线跟随并
放出一两个点射（30 mm 是 3 tick 一发）。窗口不是"必中"，因为 `lay` 的那条视线还要求车体
转过到门限内，但门限是**几何**的、与遮挡无关。

### 6.3 修法方向（未实施）

* 最小改动：把 LOS 从"找目标"里拆出来——`_acquire` 的 timer 只决定"要不要换目标"，每 tick
  的校验里加一条 `Sight.clear(eye, targetPos + 1.5)`；不通过就清 `target`（等价于落到
  `SUPPRESS`，`spot_*` 那套已经在了）。代价是每班组每 tick 一条射线，与 `Locking.cs`、
  `_sees` 走同一条门（短射线占 `_march` 调用的 87.6 %），可以先用 `world.grid` 的 box 粗筛。
* 不要做成"遮挡就重置 timer"：那会让班组在目标每次短暂遮挡时反复重扫，而 `_acquire` 本身是
  排序 + 最多 `SIGHT_CASTS`（6）次射线。
* 顺带：`spot_*` 记忆也应该带类型（见 §4 的 `tetype` 残留问题），否则压制射击的
  `radius`／高度永远是 APC 的。

---

## 7. E：其余信息与决策上的不对称（作弊嫌疑逐条判定）

新火控把 AI 的输入变成了**精确值**（真位置、真速度、真距离），玩家那一侧是快照、插值、
RTT 与估计。逐条过一遍，判定如下。

| 编号 | 不对称 | 判定 |
| --- | --- | --- |
| **E1** | AI 的零位是 `zero = ｜muzzle − target｜`（`ShotOf`），永远精确；玩家的零位是 `pl.zero_range` 转盘 | **真实优势，属于设计**：客户端有环和落点标记可以做同样的补偿，但"猜距离"这件事 AI 不用做 |
| **E2** | `weapons.run` 把 AI 的 `target` 直接塞给 `lock` 弹，绕开 `pl.lock_target` 与 `lock_progress` | **真实优势，`AGENTS.md` 已写明是有意的**；AI 仍然要过 `Sense` 的 LOS |
| **E3** | 客户端给地面目标的 `vy` 传 0（`_ringSolve` 里 `vy = 0`），AI 用真实 `vel.y` | 小优势（上坡目标的垂直前置），也是两侧同一"律"的一处输入分歧 |
| — | 完美加速度前置 | **不成立**：D4 已把它废掉 |
| — | 瞄准高度 | **不成立**：D3 让 AI 比玩家差 |
| — | 无反应时间 | 观察：AI 在门限打开的同一 tick 开火，玩家有人的延迟；本轮未量化 |

E1／E2 是设计层面的选择（"AI 没有瞄准具可读""AI 没有转盘可拧"），写在这里是为了让下次调平衡
时知道差在哪；真正需要动的信息不对称是 D5。

---

## 8. F：小项

| 编号 | 条目 | 说明 |
| --- | --- | --- |
| F1 | `FireControl.solve` 不报告未收敛 | `Solution.valid` 只看 `tof > 0`；内层 4 遍没收敛（`｜err｜ >= CONVERGE`）也照样返回一个"有效"解，机组会照着它开火 |
| F2 | `Track.radius` 无人读 | `Aim.radius` 才是门限用的那个；`Track.radius` 传了两处、改了三处，是死参数 |
| F3 | `EngageFoot` 头注释过期 | 仍写"0.14 rad of bearing error"，常量 `HULL_TOL` 已经删了 |
| F4 | `Harness/FcsCheck.cs` 两侧用的**不是**同一个输入 | 机组的 track 高度来自 D3（射手高），玩家的来自目标高；2 m 的容忍正好把 D3 盖住了。这条检查应该两边都给目标高度，D3 才有机会被抓到 |
| F5 | harness 覆盖缺口 | `--check fcs` / `--check hit` 的射手只有 mech，所以 D1 一条都没被抓到；`--check intercept` 的射手也是 mech |
| F6 | `intercept_aim` 不做 `wd.range` / `ai_range` 检查 | 靠 `trigger` 里的 `min(reach, wd.range)` 兜住；`Intercept.RANGE`（1500 m）也已先筛过一轮，暂时无害，但是两处判据 |

---

## 9. 优先级

1. **D1** —— 不修这一条，APC 与步兵等于不在场，`--check battle` 的一切基准数都脏。修完必须
   重跑 12 分钟战斗，把 `fcs_unify.md` 的 86 发/击毁重新量一遍。
2. **D2** —— 副臂现在只有在车体扫过时才开火，等于一半的火力是随机的。
3. **D3** —— 一行改到 `Defs.UNIT_DEFS[tetype].height`；记忆点那条要连同 `tetype` 一起存。
4. **D4** —— 一行；改完 `ai_tacc` 才真的是客户端那个量。
5. **D5** —— 唯一一条信息不对称，与"所有眼睛都走 `Sight.clear`"的原则直接冲突；烟雾的
   设计价值全靠它。
6. F1–F6 随笔修。

---

## 10. 复核结果（1.21.0 这轮工作）

逐条判定，以及最后落到代码里的东西。测量一律 `./dotnet.sh run -- --check ...`
（无 socket、无墙钟），数字按量级读。

### 10.1 D1 确认，已修——而且同一处还有第二个 bug

机制与 §2 一致：`launch_geometry` 对非机甲 `return origin`，而车体的 `pos.y` 就是地面。
修法：每种底盘给自己的炮口。

```csharp
var (mount, _, _) = aim_of(world, eid);
float reach = etype == Defs.ETYPE_APC ? wd.reach : FOOT_REACH;   // FOOT_REACH = 0.7 m
return mount + dir_from(yaw, tube_pitch) * reach;
```

**复核时抓到的第二处（同一处接线的另一半，报告里没有）**：`Engage.ShotOf` 用 `r.yaw`
放炮口，而 `r.yaw` 在机甲上是**炮口方位**（`Gunnery` 追着视线转）、在 APC 上是**车体
朝向**（炮塔在 `turret_yaw` 里）。`Weapons.fire` 用的是 `bore_of`（APC = 炮塔），于是
炮口点与实际开火方向差着整个炮塔角。修法：`ShotOf` 一律走 `Weapons.bore_of`。
实测（APC `MG127` → APC 800 m 静止）：

| | 报告里的 D1（`return origin`） | 只修 D1 | 再修 `ShotOf` |
| --- | --- | --- | --- |
| 一轮 40 发内 | 299 发全爆在 0 m，目标满血 | 142 发 / 12 hp（几乎全落空） | **一轮内 KILL** |

### 10.2 D2 判为**非缺陷**，不改

报告 §3.2 的推导漏了一步：`miss ≈ d·sin(err_yaw)`，所以现在的方位门限
`atan((radius + hit_r) / dist)` **已经**是在判"这一发的落点偏差"——§3.3 提的改法就是
现状。真正要判的是"副臂那发能不能落在目标身上"，而它的答案是**不能**：

* 两臂炮口横向相距 **2.86 m**（`MECH_ARM_MUZZLE` ∓1.43 m），车体只能指向一个方位；
  `lay()` 把车体/炮塔指向**主手**的解，于是主手弹精确命中，副手弹从自己那个炮口沿车体
  轴线出去，横向偏 **2.86 m**。
* APC 的命中球半径 **1.78 m**、机甲躯干 1.2–1.3 m、步兵 **0.65 m**。2.86 m 全都打不中。
  所以副手不开火是**物理正确**——"机甲永远只有主臂开火"不是接线错，是两管并列、
  车体单方位的必然结果。
* 报告 §3.2 里"落点偏 1.43 m，在门限内"只在**车体瞄目标中心**时成立，而那会让主手
  偏 1.43 m：打步兵（0.65 m）就成了主手也打不中，而且与客户端"每臂一个环、吸附其中
  一只"的模型对不上（客户端本来就承认两手朝向不同）。

**但这一定性带来了一个真缺陷（回复"两条手臂怎么打"时量出来的）：**既然只有主手打得
响，**"选哪条臂当主手"就等于"用什么武器"**，而 1.21.0 的 `AI_MASTER_ARM = Fastest`
（最先到达的那一发）永远选中 30 mm——于是**另一条手臂整场哑火**。实测
（`--check fcs` 新增的 `arsenal` 行：6 种 `Defs.AI_LOADOUTS` 配装 × 12 s × 静止 APC）：

| 配装 | 修前（主手 / 另一臂） | 修后 |
| --- | --- | --- |
| 30 mm + 火箭巢 | 0 / **50**（火箭一发未发） | **40** / 31 |
| 30 mm + 30 mm | 0 / 58（另一门同位置，本来就没差别） | 53 / 0 |
| 30 mm + SACLOS | 0 / **55**（一枚导弹未发） | **16** / 37 |
| 30 mm + 攻顶 | 0 / **53**（一发未发） | **1** / 58（15 s 发射间隔里机炮在打） |
| 30 mm + 105 mm | **4** / 0（105 的 12 发打完，整台机甲一门炮都不响） | **4** / 47 |
| 30 mm + 12.7 mm | **52** / 0（加特林被 30 mm 压住） | 58 / 0 |

修法两条，都在 `Systems/AI/Support/Engage.cs`：

* **`Defs.AI_MASTER_ARM = Punch`**（按 `ad + ed` 比：火箭 / SACLOS / 攻顶 / 105 mm 都
  排在 30 mm 前面），主手因此是"这个目标上打得最疼的那条臂"；
* **`Engage.MASTER_HANDOFF`（0.5 s）**：手上没弹、正在装弹、或者在等发射间隔超过半秒的
  那条臂**把视线让出去**（`_can_fire`），另一条臂接上。于是 105 mm 装填的 3 s、攻顶的
  15 s 发射间隔里机炮一直在响；收弹（弹药打光）之后整台机甲也不会变哑巴。
  SACLOS 例外：它骑的是**视线本身**（`aim_of`），所以带 `kind == "saclos"` 的臂不因
  冷却让出视线，否则飞行中的导弹会丢制导。

同型双臂（双 30 mm）、以及被 30 mm 全面压住的 12.7 mm 仍然只响一门：两门炮在一个位置、
两个门限都过，换手没有意义——这不是缺陷，是那个几何里的上限。

### 10.3 D3 确认，已修（并带上同类的一处）

`ground_aim` 的瞄准高度、命中半径、以及记忆点的类型都改成**目标自己**的：

```csharp
var own  = Defs.UNIT_DEFS[c.etype[i]];              // 只用来取 ai_range
var tdef = Defs.UNIT_DEFS[r.belief_type];           // 目标：高度、半径
aim.at = r.belief_pos + new Vec3(0, tdef.height * 0.5f, 0);
aim.radius = MathUtil.max_radius(r.belief_type);
```

`tetype` 残留（压制射击时是 `ETYPE_APC`）随 `ai_tgt` 一起没有了：信念里存的就是
看见时的真实 `etype`。

同类第二处：`Systems/AI/Support/Policy.cs::sense` 同样把 `height/2` 之类取到自己身上
（并且自己做视线投射）。现在它只读信念，一个字都不看 ECS 的目标位置。

### 10.4 D4 确认，已修

`measure_accel` 末尾改成 `tacc[b + 4] = 0f;`——`[b+4]` 从此真的是"距上次新鲜样本"。
`--check fcs` 的 `pull` 行（目标绕半径 60 m、12 m/s 的圆，真实值 2.4 m/s²，读
`ai_tacc`）实测 **2.40 m/s² … ok**；把这一行改回 `+= dt` 立刻变 **0.00 … GAP**。

### 10.5 D5 复核：报告的机制成立，但"四级链条"有两处需要更正；已按用户要求改成信念

更正：

* §6.1 第 3 条（`Engage` 每 tick 读目标真值）成立；第 1、2 条（`_acquire` 是唯一
  `Sight.clear`）成立。但 `Policy.sense` **本来就每 tick 自己投了一次视线**（报告说
  "没有视线"是看漏了），所以"窗口长度"不等于"AI 拿真值多久"——两条路径同时存在，
  而且**它们读的东西不一样**：tactic 读 `Policy` 的答案，`Engage` 读真值。
* 靶场的防空哨位与 raider 的两处分支确实吃真值（`Intercept` 的 `target`、
  `Raid` 的 `_gradient`），窗口更长，因为没有 `Policy` 那一层。

修法按用户的提议做，和攻顶导弹的 `Core/Seeker.cs` 同构——**把引擎真值与"AI 所知的
目标"分开**：

| | 旧 | 新 |
| --- | --- | --- |
| 存哪 | `spot_x/y/z/t`（4 个 float，AI 侧自己维护） | `ai_tgt` = `[x,y,z, vx,vy,vz, age, etype]`（`CREW_FCS_COMPS`） |
| 谁写 | 好几处 | **`Sense.cs` 一个写者** |
| 看得见时 | 记忆点被刷新 | 位置/速度/类型每 tick 抄一遍真值（`measure_accel` 也读这份速度） |
| 看不见时 | 记忆点不动，`Policy.SPOT_MEMORY` 4 s 后作废，`Engage` 仍在读真值 | 按最后的速度外推（`walk_belief`），拉力自然过期，**2 s**（`Crew.MEMORY`）后 `forget()` |
| 解算读谁 | `fv("pos")` / `fv("vel")` | **只读信念**（`Engage.ground_aim` 里没有任何目标真值） |

视线从"找目标"里拆了出来：扫描时钟只决定**要不要换目标**，已经持有的目标每 tick 复查
一次（`_sees`：先按 `ai_range` 筛距离，再用**目标自己**的 `height * 0.6` 做视线点）。
丢目标后的战术状态是 `SUPPRESS`（朝推断点站着打），2 s 后彻底放弃。

回归行 `--check fcs` 的 `smoke`：目标 700 m 静止、第 2.5 s 在 120 m 处放一个烟球，
数三个阶段的弹数（`proj_src == shooter`）——

```
5-8 rounds before the screen (21-64 hp landed), blocked for 1.7-2.0 s,
1-3 rounds at the memory, 0 after it
```

判定 `before > 0 && blocked >= 0.5 && during > 0 && after == 0`。把 `Sense` 的
`walk_belief` 换回"读真值"那一版，`after` 立刻变正（机组继续打穿烟幕）。

代价：每班组每 tick 一条 `Sight.clear`，`MECH_PERF=1` 的 12 分钟战斗从
**1.3 ms/tick 涨到 1.5 ms/tick**（约 0.2 ms）。可以接受，而且它是这条规则的价格。

### 10.6 E1／E2：按设计保留

用户判定属设计，不改。报告里的理由（"AI 没有准星可读""AI 没有转盘可拧"）与
`AGENTS.md` 的写法一致；E2 的"锁定武器免锁"仍要走 `Sense` 的视线，所以它不是透视。

### 10.7 E3：客户端的问题，已在客户端修

`frontend/src/game.js::_ringSolve` 里 `vy = 0`（`_leadMarkers` 里另有一处字面 `0`），
而服务端 AI 用的是真实 `vel.y`。现在两处都取 `e.mvy`（`net.js` 对快照速度的平滑值），
与 `leadPoint` 的 `vy` 参数接上。对地目标只有在**上坡/下坡**时才看得出差别（几米量级），
但那是两侧同一套"律"的一处输入分歧，修掉比留着强。

### 10.8 F：逐条

| 编号 | 处理 |
| --- | --- |
| F1 | **只改注释**。`Solution.valid` 的语义是"这条弹道存在"（有飞行时间、仰角在 `ZERO_MAX_ELEV` 内）；内层 4 遍、0.15 m 的收敛阈值**是律的一部分**，两侧跑的是同一次迭代，把"未收敛"另立一档只会让两侧多一个可以分叉的判据 |
| F2 | 已修：`Track` 去掉 `radius`（现在是 4 参数），门限用的那个换成 `Aim.radius`；`tests/backend/Support/Cases.cs` 的 `Case.radius` 从用例表读，`Tol.Miss(k.radius)` |
| F3 | 已修：`EngageFoot` 头注释改成"每臂自己解出来的角径门限" |
| F4 | 已修：`--check fcs` 两侧现在都给**目标**的高度（D3 修完后两侧本来也一致了，这条检查于是真的能抓到回归） |
| F5 | 已修：`--check fcs` 加了三行 `chassis`（APC 机枪 / 步枪兵 / 机甲 RPG，各自打自己的目标），D1 的两种形态都被它们钉住了 |
| F6 | 已修：`Engage.intercept_aim` 加 `if (aim.dist > wd.range) return aim;` |

新增的常驻回归行（`Harness/FcsCheck.cs`）：`chassis`（三种底盘各一行：弹数、距射手最近的
爆点、目标掉的血）、`arsenal`（六种 AI 配装各一行：两条手臂各打了几发，见 §10.2）、
`pull`（`ai_tacc` 实测 2.40 m/s²）、`smoke`（§10.5 的三段弹数 + 量出来的记忆长度
1.2–2.0 s）。它们与既有的六条场景行一起，就是本报告所有 D 条的机械保证。

### 10.9 基准数重测（本轮修完之后）

`--check battle --minutes 12`，三次（seed 3 / 7 / 11）：

| | 报告写下的 `8463ae7`（4 场） | 只修 D1／D3（3 场） | 再修两条手臂（3 场） |
| --- | --- | --- | --- |
| 炮弹 | 5.4 万发 | 1.6–2.2 万发 | **1.4–1.5 万发** |
| 击毁车辆 | 402 台 | 432–439 台 | **374–473 台** |
| 每击毁耗弹 | 134 发 | 37–49 发 | **32–38 发** |

炮弹降下来的部分是 D1 的空炮（APC / 步兵每一发都炸在自己脚下，却仍然计入发射数）；
最后一轮的下降是 `Punch`：导弹与火箭替掉了大量机炮弹，所以击毁数没掉、耗弹更少
（`intercept` 事件从 145–191 涨到 169–276，就是这些导弹真的上天了、也被拦下来）。

其余 harness 同轮复核（同一台机器）：`--check fcs` 六条场景 + `chassis` / `arsenal` /
`pull` / `smoke` 全 `ok`；`--check intercept` 24/32（同一张表，报告时是 15/32）；
`--check hit` 与改前同形状；`--check range --minutes 2` 正常；`--check net --minutes 12
--from 8` 无变化（`ai_tgt` 不上线）；`./run_tests.sh` 42/42 一致；
`MECH_PERF=1` 1.48 ms/tick（其中 0.2 ms 是班组每 tick 那一次视线）。

---

## 附录 A：探针（未入库，放在 `/tmp/fcsprobe`）

```csharp
// fcsprobe.csproj: <ProjectReference Include="…/MechServer.csproj" />，net10.0
using Mech.Server;
using Mech.Server.Systems;
using Mech.Server.Systems.AI;

const float DT = 1f / 30f;

static void Sweep(World world, int keepA, int keepB)
{
    var e = world.ecs;
    foreach (int id in world.units())
    {
        if (id == keepA || id == keepB) continue;
        if (e.uv("etype")[id] == Defs.ETYPE_BASE) continue;
        e.destroy(id);
    }
}

static void Run(string name, int shooterType, int[] arms, int targetType, float range)
{
    var world = new World(seed: 7, mode: "range");
    foreach (int id in world.units()) world.ecs.destroy(id);
    const float sx = 1200f, sz = 2100f;              // FcsCheck 找到的那块平地
    int shooter = world.spawn_unit(shooterType, Defs.FACTION_RED, sx, sz, -1,
                                   weapons: arms, extra: new[] { AiTags.ENGAGE }, crew: true);
    int target = world.spawn_unit(targetType, Defs.FACTION_BLUE, sx + range, sz, -1, crew: false);
    var e = world.ecs;
    int[] shots = new int[2];
    var boomD = new List<float>();
    bool dead = false;
    for (int tick = 0; tick < 300; tick++)
    {
        Sweep(world, shooter, target);
        var arch = e.arch_of(target);
        if (!e.is_alive(target) || arch is null || !arch.keyset.Contains("unit")) { dead = true; break; }
        // 目标脚本按住（横穿时把 z 换成 z0 + 12f * world.time，vel.z = 12f）
        e.fv("pos").set3(target, sx + range, world.terrain.height_at(sx + range, sz), sz);
        e.fv("vel").set3(target, 0f, 0f, 0f);
        world.update(DT);
        foreach (var ev in world.events)
        {
            var k = ev.GetValueOrDefault("k") as string;
            if (k == "shot" && ev.GetValueOrDefault("eid") is int sid && sid == shooter)
            {
                int arm = ev.GetValueOrDefault("arm") is int a ? a : -1;
                if (arm is 0 or 1) shots[arm]++;
            }
            if (k == "boom")
            {
                float bx = Convert.ToSingle(ev["x"]), bz = Convert.ToSingle(ev["z"]);
                boomD.Add(MathF.Sqrt((bx - sx) * (bx - sx) + (bz - sz) * (bz - sz)));
            }
        }
        world.events.Clear();
    }
    boomD.Sort();
    Console.WriteLine($"{name,-34} arm0={shots[0],4} arm1={shots[1],4} booms={boomD.Count,4} "
        + $"median {(boomD.Count == 0 ? -1 : boomD[boomD.Count / 2]):0} m  {(dead ? "KILLED" : "alive")}");
}

Run("apc   mg127 -> apc     800", Defs.ETYPE_APC, new[] { Defs.WID_MG127, Defs.WID_NONE }, Defs.ETYPE_APC, 800f);
Run("man   rifle -> man     300", Defs.ETYPE_INF_RIFLE, new[] { Defs.WID_RIFLE556, Defs.WID_NONE }, Defs.ETYPE_INF_RIFLE, 300f);
Run("mech  ac30  -> apc    1200", Defs.ETYPE_MECH, new[] { Defs.WID_AC30, Defs.WID_NONE }, Defs.ETYPE_APC, 1200f);
Run("mech  ac30+rocket 1200", Defs.ETYPE_MECH, new[] { Defs.WID_AC30, Defs.WID_ROCKET }, Defs.ETYPE_APC, 1200f);
```

跑法（沙箱里 dotnet 的状态要指到仓库的 `.dotnet_tmp/`，与 `dotnet.sh` 一致）：

```bash
cd /tmp/fcsprobe && \
DOTNET_CLI_HOME=…/mech/.dotnet_tmp/home NUGET_PACKAGES=…/mech/.dotnet_tmp/nuget \
XDG_RUNTIME_DIR=…/mech/.dotnet_tmp/run TMPDIR=…/mech/.dotnet_tmp/tmp \
dotnet run -c Release
```

对照 `8463ae7^` 用 `git worktree add /tmp/mech-pre 8463ae7^`、把 `ProjectReference` 指过去，
跑同一份 `Program.cs`（探针没有用任何 1.21.0 之后才有的 API）。
