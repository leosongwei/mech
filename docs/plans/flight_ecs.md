# 飞行体的 ECS 拆分：一个累加器与七个 stage

这一页记下把 `Core/Flight.cs::_airframe` 拆成多个 system 的来由、拆法、必须守住的不变量，
以及新增一种投放物（无人机）在拆分完成之后是什么形状。覆盖 1.41 开发期。

## 1. 动机

`_airframe` 是一个 80 行的函数，里面同时住着四类东西：**识别**（这颗弹飞哪条律）、
**派生量**（`vhat / speed / q / cl / alpha / nose`）、**四个力**（升力、阻力、推力、重力）、
**积分**。要回答"这颗弹这一 tick 受什么"，只能从头到尾跟一遍函数体，并记住每个局部变量
在哪个分支里被改过。

更实际的代价是新增投放物：无人机的推力是一条持续小推力、它的升力需求不是参考弧而是一个
盘旋圆、它的油门要随速度误差调整。这三件事今天的形状是"在共用积分器里加参数、加分支"，
于是每一颗子弹、每一发散弹都要带着为无人机加的判断走一遍。

目标形状：**"哪些力作用在这个实体上"就是它的组件集合**，于是它可被查询、可被分别书写、
可被文档按 stage 索引。这与 `AGENTS.md` 的两条一致——组件集合即实体的类型；用组件表明身份，
路由到不同系统上。`acc`、`a_dir`、`cl` 这几个中间量因此留在组件里：一个同时拥有它们的实体
是"一副会算气动的飞行体"，拥有 `acc` 而没有 `a_want` 的实体不是。

## 2. 现状：为什么不能一个力改一次 `vel`

`_airframe` 内部有三处因果，决定了它不是五个可分离的力：

1. **`CL` 来自制导需求**（`CL = m·a_want/(q·S)`，钳 `cl_max`），而 `CL` 同时决定
   **诱导阻力**（`cd0 + k_cl·CL²`）与**攻角**（`α = CL/cl_alpha`）；`α` 决定机头，
   机头决定**推力方向**。
2. **末端钳位是三力的共同产物**：`if (speed > maxvel) v *= cap/speed` 同时削掉重力、
   推力与阻力各自贡献的那一份。
3. **中间量只有一个速度**。若拆成"各自改一遍 `vel`"的系统，一个 tick 内 `vel` 被改写
   四次，顺序即语义，结果与一次相加不同（显式欧拉分步 ≠ 同刻求和）。

对照机甲：`Mech/{Steer,Thrust,Slope,Drag,Ground}` 五个 system 各自写同一个 `vel`，是因为
五个项彼此独立、单位相同、且每项都只是"加一个与 `dt` 成正比的量"，`dt` 全 tick 一致，
所以顺序无关。飞行体没有这个可分性。

结论：拆法的单位不是"四个力各一个系统"，而是**先算派生量（同一份 tick 前状态），再把
四个力累加进一个加速度缓冲，最后积一次**。这正是今天这条式子的结构，只是摊平了。

## 3. 目标形状

### 3.1 新组件

七个，全部是 `Kind.F`，**出生即有、终生不变**（`Components.AIRFRAME_EXTRA`）：

| 组件 | 形状 | 单位 | 含义 | 写者 |
| --- | --- | --- | --- | --- |
| `acc` | 3 | m/s² | 本 tick 的加速度累加器 | `Arm` 归零；Lift/Drag/Thrust/Gravity 相加 |
| `a_dir` | 3 | 单位向量 | 制导要求的升力方向 | 制导 stage；`Arm` 归零 |
| `a_want` | 1 | m/s² | 制导要求的升力大小（钳位前） | 制导 stage；`Arm` 归零 |
| `vhat` | 3 | 单位向量 | 速度方向 | `Airflow` |
| `q` | 1 | Pa | 动压 `½ρv²` | `Airflow` |
| `cl` | 1 | — | 本 tick 实际做出的升力系数 | `AoA` |
| `nose` | 3 | 单位向量 | 推力轴（速度线绕升力方向转 `α`） | `AoA` |

留在纯函数里、不占列的派生量：`speed = sqrt(dot(v,v))`、`S = π(dia/2)²`、
`lift_max = q·S·cl_max/m`、`alpha = cl/cl_alpha`、`cd`。它们是单行推导，写进列只会
让同一个事实有两份。

### 3.2 原型

```
bullet   PROJ + bullet                                         （无翼，不进这条链）
rocket   PROJ + AIRFRAME_EXTRA + unguided_missile
saclos   PROJ + AIRFRAME_EXTRA + beam_ch + saclos
top      PROJ + AIRFRAME_EXTRA + tgt_mem + tgt_vel + top_attack
drone    PROJ + AIRFRAME_EXTRA + orbit + … + drone             （下一版）
```

`Components.AIRFRAME_COMPS = Cat(PROJ_COMPS, AIRFRAME_EXTRA)` 与现有的
`SEEKER_COMPS` / `BEAM_ROUND_COMPS` 是同一个写法：工厂按集合并集拼装，重复名字由
`e.create` 按键合并。工厂的判据是一条表（`Defs.KIND_AIRFRAME`：`rocket`、`saclos`、
`top`、`drone`），不是 `kind == "top"` 那样的分支。

子弹是绝大多数实体，它不在这些原型里，所以加宽原型的成本只与**在飞的有翼弹数**有关。

### 3.3 stage 顺序

`Collect` 不存在：每个 stage 自己按列查询遍历原型，行号在飞行块内稳定（见 §5）。

| # | system | 查询 | 读 | 写 |
| --- | --- | --- | --- | --- |
| — | `Flights.Expire`（已有，不动） | 全部弹 | `proj_life`、`proj_trav` | `first_hit_record`；`sweep.begin()` |
| 1 | `Flights.Arm` | 全部弹 | `pos`、`first_hit_t`、`proj_age` | `proj_prev`、`first_hit_t` 重新上膛；`acc`、`a_dir`、`a_want` 归零 |
| 2 | `Flights.Ballistic` | `bullet` | `vel`、`proj_wid` | `vel`、`pos`、`proj_trav`、`sweep` |
| 3 | `Guidance.Top` | `top_attack` | `pos`、`vel`、`proj_age`、`proj_trav`、`tgt_vel` | `a_dir`、`a_want` |
| 4 | `Guidance.Saclos` | `saclos` | `pos`、`vel`、`proj_age`、`beam_ch`、发射机五列 | `a_dir`、`a_want` |
| 5 | `Airflow` | `AIRFRAME_EXTRA` | `vel` | `vhat`、`q` |
| 6 | `AoA` | `AIRFRAME_EXTRA` | `a_dir`、`a_want`、`q`、`proj_wid` | `cl`、`nose` |
| 7 | `Lift` | `AIRFRAME_EXTRA` | `a_dir`、`cl`、`q`、`proj_wid` | `acc` += |
| 8 | `Drag` | `AIRFRAME_EXTRA` | `vhat`、`cl`、`q`、`proj_wid` | `acc` −= |
| 9 | `Thrust` | `AIRFRAME_EXTRA` | `nose`、`proj_age`、`vel`、`proj_wid` | `acc` += |
| 10 | `Gravity` | `AIRFRAME_EXTRA` | — | `acc`.y −= `Flight.GRAVITY` |
| 11 | `Integrate` | `AIRFRAME_EXTRA` | `acc`、`pos`、`vel`、`proj_trav` | `vel`、`pos`、`proj_trav`、`sweep` |

三条落位规则：

* **无制导火箭没有制导 stage**。它履行的是"没有制导 tag 的飞行体"这个身份，
  `a_dir`/`a_want` 恒为 0，`Flights.Rocket.run` 整体消失。
* 判死弹不飞，判定只有一处定义：`Flights.flying(r)`（`first_hit_t > 1`）。它读的是
  `first_hit_record` 本身，不另设 `live` 标记——同一个事实只有一个归属。
* `Expire` 仍是 `sweep.begin()` 的位置，`Integrate` 仍是唯一 `sweep.add()` 的地方。
  腿列表的起止因此与今天完全一致。

### 3.4 term 是纯函数，系统只是一层遍历

每个 stage 的数学写在 `Core/Flight.cs`，签名与今天的小函数同级：

```csharp
public static (Vec3 vhat, float q) airflow(Vec3 v);
public static (float cl, Vec3 nose) aoa(Vec3 vhat, Vec3 a_dir, float a_want,
                                        float q, WeaponDef wd);
public static float lift_accel(WeaponDef wd, float q, float cl);
public static float drag_accel(WeaponDef wd, float v, float q, float cl);   // 含跨音速项
public static float thrust_accel(WeaponDef wd, float age, float v, float throttle);
public static Vec3 cap(Vec3 v, WeaponDef wd);
```

`Flight.step` / `direct` / `slide` 保留，实现改成"按上表次序调用这些 term"，因此火控
（`Ballistics.flight_at`）、`FcsCheck` 与 `--check` 系列继续直接使用它们，不必搭一个世界。

系统是**唯一权威**：它把同样的 term 调用分散到十一个 pass 上，中间量落在组件里。
两边的算术序列相同，所以漂移只有一个来源，见 §6。

### 3.5 文件布局

```
Core/Flight.cs          常量、arc/lift_dir/pn_accel 等需求函数、六个 term、step/direct/slide
Systems/Flights.cs      列定义（RoundColumns / AirframeColumns / 派生）、Expire、Arm、
                        Ballistic、Guidance.Top、Guidance.Saclos
Systems/Airframe.cs     Airflow、AoA、Lift、Drag、Thrust、Gravity、Integrate
```

列语义照现有写法：每个 stage 一个 `Columns<TRow>` 子类，`Names` 就是它查询用的那份名字表，
`bind` 只绑它要用的缓冲区，行类型返回 `int` 行号即可（`RoundRow` 那样的大结构体留给需要
多列的地方）。于是 `Gravity` 的 `Names` 是一个名字、三行代码，`Drag` 的 `Names` 是五个
名字、六行代码，各自可读，互不影响。

## 4. 这些值放在组件里

中间量写在组件上，与现有做法一致：`proj_prev` 每 tick 被覆盖、`beam_mem`/`beam_vel`/
`beam_acc`/`beam_ok` 是发射机每 tick 刷新的激光指定状态、`ai_tacc`/`gaze_yaw` 是乘员每
tick 刷新的解算结果。同一个原型里的实体各自带着自己的那一份是本仓库既有的形状，`acc`、
`a_dir`、`cl` 照此办理。

这样做的直接效果是查询即身份：一个同时拥有 `acc`、`a_dir`、`cl` 的实体是"会算气动的飞行
体"，拥有 `acc` 而没有 `a_want` 的实体不是；Gravity stage 的 `Names` 是 `{"acc"}`，因此
它无需知道自己在作用于哪种弹。

代价与边界：

* 每个有翼弹多 15 个 float。有翼弹同时在飞的数量是个位数（靶场实测 ≤ 20），开火最密的
  火箭巢一次也只有几发在飞。
* 组件集合终生不变，所以这些列**必须在出厂时一次给齐**（`spawn_round`），任何 stage 都不
  增删组件。这同时保证了一个 tick 内行号稳定。
* 新列不进入快照：`Netcode` 按列白名单序列化，客户端只插值 `pos`/`vel`，这些只在服务器侧。

## 5. 不变量

写进 `Systems/Airframe.cs` 的头注释，并按此评审：

1. **力 stage 只写 `acc`**；唯一改 `vel` 的是 `Integrate`（`Ballistic` 除外，它不改
   `acc`，自己积一步）。
2. **`Arm` 是 `acc`、`a_dir`、`a_want` 的唯一归零点**，所以 tick 起点这三个值恒为 0，
   每一 tick 的力和都是完整的、不携带上一 tick 的残余。
3. **飞行块内不创建、不销毁实体**（`Impact`、`Blasts`、`Mortality` 都在块后），因此一个
   tick 内同一实体的行号在十一个 stage 之间不变。
4. **判死弹不飞**：`first_hit_t <= 1` 的行在每个 stage 都被同一行 `if (!Flights.flying(r))
   continue;` 跳过。
5. **age 的约定不变**：`Expire` 已计入本 tick 的 `dt`，所以读"飞行年龄"的 stage
   （`Thrust`、`Guidance`）用 `proj_age - dt`，与今天三处调用 `Flight.*` 的写法一致。

## 6. 数值等价与验证

两边共用 term，唯一差异是质量除法的结合顺序：今天算 `((push·nose − drag·vhat) +
lift·a_dir) / m`，拆后算 `lift/m − drag/m + push/m`。差在浮点末位。

验证用一条新的自检，不是基线差分：

```
--check airframe     每种弹从同一初速起飞，同时走系统管线与纯函数 step/direct/slide，
                     30 s 内 pos/vel 的最大偏差
```

判据 `1e-3 m`（远小于 30 mm 弹的散布与任何命中球）。这一行量的是"两套写法是不是同一条
定律"。现有 `--check hit / intercept / beam / battle` 继续当作量级守卫：落点偏差、末速、
`profile` 行不应出现可见变化。

## 7. 迁移

四个提交，每个都独立可跑：

1. **组件与 term**：注册七列、加 `AIRFRAME_EXTRA`/`AIRFRAME_COMPS`、六个 term 纯函数化，
   `_airframe` 改成按序调用 term。世界行为不变（此时还没有 stage）。
2. **stage 接线**：`Arm` 取代五处 `_begin`；`Airframe.cs` 七个 stage 取代三处 `_move`；
   `Flights.{Rocket,Saclos,Top}` 只留制导那几行，改名 `Guidance.*`；`World.cs` 的 tick 顺序
   按 §3.3 重排。
3. **自检**：`Harness/AirframeCheck.cs` 与 `--check airframe`，读数记入 `docs/dev/checks.md`。
4. **文档**：`docs/product/systems/flight.md` 的 1.3–1.5 节按 stage 重写叙述（数值一字不改），
   `docs/dev/pipeline.md` 的 tick 表补上十一个 pass。

这是一次内部重构：不写 `CHANGELOG` 条目、不递增版本号。第 4 步只改文档的结构，不改任何
玩家可读的结论。

## 8. 无人机在拆分之后的位置

拆分完成之后，无人机是三个新增，而不是一次参数扩张：

* **`Guidance.Loiter`**：新 tag `drone` + 一个 `orbit` 列（圆心、半径、高度）。它写
  `a_dir`/`a_want`，其余十个 stage 一行不改。盘旋需求 = 高度误差给垂直分量、切向给转弯
  分量，与 `_direct_demand` 同构、同样被 `lift_max` 钳位。
* **`Thrust.Commanded`**：油门列 + `throttle` tag，第二个推力 stage，与 `Thrust` 一样只
  往 `acc` 加。两个推力 stage 不冲突，因为一个飞行体只带其中一个 tag。
* **参数与数量级**：攻顶弹的气动外形在 100 m/s 时 `lift_max ≈ 19 m/s²`、平飞阻力加速度
  `≈ 5.9 m/s²`，所以盘旋只需要 6 m/s² 量级的持续推力（对比攻顶弹的 110 m/s² 烧 3.4 s）。
  `Flight.RHO` 是常量的理由写在注释里——顶点在 1 km 以下；无人机若在 1.5 km 以上盘旋，
  这一条会变成可见误差（阻力偏大 25–35%），届时先改密度再谈续航。

## 9. 与机甲五定律的对照

两处都叫"多系统累积组合成行为"，形状不同，原因是单位不同：

| | 机甲 | 飞行体 |
| --- | --- | --- |
| 累加器 | 就是 `vel` 本身 | 独立的 `acc` 组件 |
| 中间量 | 无（每一项直接是 m/s² 量级的 `vel` 增量） | `cl` 与 `nose`（制导需求要先变成气动量） |
| 顺序 | 五项可交换，只有 `Ground` 必须最后 | 固定次序：派生量 → 力 → 积分 |
| 为什么 | 力与速度同单位，且各自可分离 | 力依赖派生量，派生量依赖同一份 tick 前状态 |

这一节存在的目的，是让下一个人不必重新问"为什么机甲不建 `acc`"。

## 10. 范围与风险

不做的事：不拆 `Seeker`（它有跨 tick 记忆、遮蔽查询与事件）；不动 `Expire`、`FirstHit`、
`Impact`；不给子弹加 `AIRFRAME_EXTRA`；不为尚未存在的无人机预先加列。

已知风险：

* **遍历次数**：从"五次 pass"变成"十一个 pass × 三到四个原型"。行数是个位数到几十，
  但 `--check battle` 的每 tick 开销那一行要前后对比，异常时必须先解释再继续。
* **清单维护**：`AIRFRAME_EXTRA` 与 `AirframeColumns` 是同一份事实的两处写法，由
  `Columns` 的注册表校验兜住（名字拼错会在第一次绑定时抛）。
* **读代码的路径变长**：从"读一个函数"变成"读 `World.cs` 的顺序表再读十一个小函数"。
  这个仓库的产品文档已经按 tick 顺序描述语义（`docs/dev/pipeline.md`），所以这是收敛
  而不是发散——第 4 步把两边对齐。

## 11. 验收

1. `./dotnet.sh build` 零警告；`./npm.sh run build` 通过（前端不受影响）。
2. `--check airframe` 三种弹的最大偏差 ≤ 1e-3 m。
3. `--check hit`、`--check intercept`、`--check beam` 读数与改动前同一量级；`--check battle
   --minutes 12` 的每 tick 开销不劣化。
4. `--check fcs` 全绿（火控仍走 `Flight.*`，所以它同时证明纯函数一侧未被动过）。
