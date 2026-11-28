# 一 tick 的流水线

> **读者**：维护者（写后端系统的人）
> **一句话**：`World.update` 里系统按什么顺序跑、每一段为什么排在那里——顺序本身就是语义，
> 新系统插错位置是一个安静的行为改动。
> **代码**：`Core/World.cs`（`_build_pipeline` / `update`）、`Net/Server.cs`（`game_loop`）、
> `Modes/Mode.cs`、`Systems/AI/Support/Pipeline.cs`、`Systems/Datalink.cs`、
> `Systems/Visibility.cs`、`Systems/Sweep.cs`、`Systems/Impact.cs`
> **核对**：1.45.0 / 2026-11-25

玩家看得见的那部分（一 tick 的价值、命中怎么判）不在这里：索引见
[../product/README.md](../product/README.md)。

## 1. `World.update` 的骨架

```csharp
public void update(float dt) {
    time += dt; tick += 1;
    this.dt = dt;                 // 本 tick 中途建的表按它积分
    ecs.destroyed.Clear();        // 重建死槽的系统看不到上一 tick 的死亡
    grid.build(ecs, "moves", "combat", "unit");   // 谁在附近：重建在任何查询之前
    foreach (var system in systems) system(this, dt);
    if (winner >= 0) { end_timer -= dt; if (end_timer <= 0f) reset_match(); }
}
```

`systems` 是 `_build_pipeline` 拼出来的**一个扁平列表**（`List<Action<World, float>>`），
`World` 里没有第二处调用点，所以"顺序"只有一份定义。拼的顺序就是下面 §2–§8 的顺序。

跑完之后 tick 循环做广播，并把 `world.tick` 交给网络层；`broadcast` 不在 `World` 里。

## 2. shared 段：`rearm` → `player_input` → `datalink`

| 系统 | 为什么在前 |
| --- | --- |
| `Rearm.run` | 换装工时先走一秒，`work_remaining_time` 归零的那一刻**本 tick** 就装上 |
| `PlayerInput.run` | 飞行员的意图写进组件（`sight_yaw`/`sight_pitch`、`vel_cmd`、`trigger`），后面所有人读它 |
| `Datalink.run` | 玩家输入已落位；用 tick 起始网格重建车辆观察并集，AI 与快照同 tick 消费 |

`player_input` 在工时未归零时把移动与两个扳机位清零，所以"换装"是一次真的停火——它必须在
`weapons` 之前，否则本 tick 已经打出去了。

## 3. 模式的 pre-systems（battle 的 AI 块）

`mode_mod.pre_systems()`。这一段的含义是"机组说它要什么"：位置、速度意图、视线、扳机。

battle 的块是 `Systems.AI.Pipeline.battle()`，一个扁平列表，顺序写在 `Pipeline.cs` 里：

```
Threats  本 tick 的制导弹丸，全场只算一次     → ai_threat
Intercept 谁有弹冲着自己来                    → ai_cover / ai_threat
Sense.link_wheel 扫描钟到点的机组写数据链方位    → tgt（低权重，不存目标 ID）
Watch.run 注意力用上一 tick 的信念决定看哪儿    → ai_gaze / gaze_cmd，并读四张方位轮盘
Watch.turn 孔径按 150 °/s 转过去               → gaze_yaw
Sense     每机组一次瞭望，只在这个窗口里找      → ai_tgt（信念）与 target
Front     车道的状态机                        → nav_route / nav_speed
Raid      破袭队的规划与任务                   → ai_state / 路线
Paths     跑不动了：面片导航网要一条绕行         → 在已有航点前插入 detour
Engage{Mech,Vehicle,Foot}  炮：那台底盘唯一的扳机写者
Wander    平民
```

`Watch` 排在 `Sense` 之前是刻意的，两边都靠这一点：`Watch.run` 读到的 `belief_age == 0`
意思是"上一 tick 看见了"（只有 `Sense` 会把年龄往前推），于是它不需要任何容差；孔径在本
tick 转完，`Sense` 随后的扫描用的就是刚转到的方位。**火控与炮座一行没动**：`Engage*` 仍
然写 `sight_yaw` / `turret_yaw` / `yaw`，`gunnery` 仍然让车体与炮臂追它们——孔径是第四根角，
不参与那条链（[../product/systems/ai.md](../product/systems/ai.md) §1.4）。

range 的块是 `Targets` + `Interceptor` + `Pipeline.range()`（同样的顺序，没有 Raid）；
一个只打导弹的模式跑 `Pipeline.defence()`（只有 `sense` + `EngageMech`/`EngageVehicle`）。

**它必须排在整个共享段之前**：机组的速度意图和扳机要落在同一 tick 里被 `gunnery`、`weapons`、
`movement` 消费掉，否则机组永远慢一帧开火、慢一帧起步。

## 4. `gunnery` → `locking` → `smoke` → `weapons`

| 系统 | 为什么在这里 |
| --- | --- |
| `Gunnery.run` | 视线已经定好，现在让**机器**追上来：`yaw` 追 `sight_yaw`、`pitch` 追 `sight_pitch`，所以任何读炮口的东西（开火、客户端、碰撞球表）看到的都是**架好的炮** |
| `Locking.run` | 锁定与设计ator 的候选与通道，早于开火：本 tick 扣扳机时先认已经持有的那条线，没有才现 claim |
| `Smoke.run` | 罐子飞行、到期爆成烟团——烟团从这一刻起就参与 `Sight`，锁、瞭望与导引头都从它后面失去目标 |
| `Weapons.run` | 只执行**已经存下的意图**（扳机位）：弹匣、装填、冷却，按 `bore` 生成弹丸 |

一发弹沿着 `bore` 出去，而 `gunnery` 是唯一让 `bore` 追上 `sight` 的地方——这就是它必须是这一段
第一个系统的全部原因。

## 5. `unstick` → 驱动

```
Unstick.run
Movement.Foot.run
Mech.Steer / Mech.Thrust / Mech.Slope / Mech.Drag / Mech.Ground
Movement.Vehicle.run
```

* `Unstick.run` **紧贴驱动之前**：一个壳子自己的脚印压在建筑/岩石/基地台上，它就没有一步可走
  （`Integrate.step` 的五个采样点都含这个重叠）。把清空的地面与 `vel` 归零放在这里，驱动读到的
  就是一个能走的位置；每 `Unstick.EVERY`(10) tick 才看一次，因为这个病不会自愈。
* 驱动是**每种底盘一个**，机甲是五条定律的链（`Steer`/`Thrust`/`Slope`/`Drag`/`Ground`），顺序就是
  它们互相依赖的顺序：意图与腿 → 推力与巡航 → 坡的重力 → 平方阻力 → 与地面的积分。它们是
  `vel` / `pos` / `vel_cmd` 的唯一写者（`ecs.md` §7）。
* 机组与飞行员在这一段被**同一个机器**处理：机组只写 `nav_*`（§3），飞行员只写 `vel_cmd`（§2）。

## 6. 弹的一 tick

```
Sweep.Spheres.run        本 tick 的命中球表
Beams.run                激光：四个通道的跟踪与记忆
Flights.Expire.run       电量与导线，在一切移动之前
Flights.Arm.run          重新装填记录，清零本 tick 的飞行状态
Flights.Ballistic.run    子弹：自己积一条抛物线
Flights.Guidance.Top.run    攻顶：一条参考弧与末段 PN
Flights.Guidance.Saclos.run 驾束：骑在激光线上
Flights.Guidance.Loiter.run 游荡：保高 + 盘旋搜索 + 末段 PN
Flights.Airflow.run      气动一：速度方向与动压
Flights.AoA.run          气动二：升力系数与机头
Flights.Lift.run         力一：升力
Flights.Drag.run         力二：阻力
Flights.Thrust.run       力三：推力（点火窗口）
Flights.Gravity.run      力四：重力
Flights.Thrust.Commanded.run  力三之二：常开的调速器（游荡弹药）
Flights.Integrate.run    积一步，公布这一腿
FirstHit.{Ground,Structures,Units,Fuse}.run
Impact.run
```

| 系统 | 为什么必须在这个位置 |
| --- | --- |
| `Spheres` | 在 `movement` **之后**：单位的 `pos` 已经是本 tick 的终点，`prev = pos - vel*dt`；在飞行系统**之前**：弹的 `pos` 还是起点，`disp` 由速度预测。两个块是在不同时刻采的，所以球表必须夹在中间，且一 tick 只建一次（`world.sphere_table` 按 `tick` 缓存） |
| `Beams` | 在 `movement` 之后（它按刚写完的位置跟踪），在飞行系统之前（本 tick 的驾束弹要读它） |
| `Expire` | **在所有移动之前**：它数 `proj_age` / `proj_life`，打光电池或导线的弹在这里写一条 t=0 的接触（`HIT_EXPIRE` / `HIT_VANISH`）就站住不动了。它同时 `world.sweep.begin()`，开本 tick 的腿列表 |
| `Arm` | 本 tick 唯一的归零点：给每一颗弹重新装填 `first_hit_record`（被 `Expire` 判死的弹保持它的接触，`first_hit_t <= 1`），把 `(proj_prev, pos)` 这条腿的起点写下来，并把有翼弹的 `acc` / `a_dir` / `a_want` 清零。因此十一个 pass 共享同一条判死规则（`Flights.flying`）。十一个 stage 的清单以这一段为准 |
| `Ballistic` | 子弹没有气动外形，七列气动状态不在它的 archetype 里，所以它自己积那一步抛物线 |
| `Guidance.*` | 按制导 tag 一个 pass 一种律，没有 `proj_kind` 分支；无制导的火箭没有这一步，`a_dir` / `a_want` 恒为零。`Guidance.Loiter` 是唯一一个自己找目标的：没有锁时它调 `Seeker.scan` 把首捕写进 `target` / `tgt_mem` / `tgt_vel`，有锁时才用 `Seeker.track` |
| `Airflow` / `AoA` | 读的都是**同一份 tick 前状态**：`vel` 没有被动过，所以四个力是"同刻求和"，而不是分四步各改一次 `vel`。中间量（`vhat` / `q` / `cl` / `nose`）落在组件里 |
| `Lift` / `Drag` / `Thrust` / `Gravity` | 四个力 stage **只往 `acc` 里加**，彼此不读对方。顺序因此只决定浮点加法的结合，不是语义。两个推力 stage 不冲突：`Thrust` 问的是"点火窗口开了没有"，游荡弹药没有窗口（`ignite = burn = 0`），由 `Thrust.Commanded` 按速度误差开油门 |
| `Integrate` | 唯一改 `vel` 的地方（子弹除外）：`vel += acc*dt`，再做一次 `maxvel` 归一化，最后把 `(proj_prev, pos)` 这条腿发布给球表。它也是唯一 `sweep.add` 的地方 |
| `FirstHit.*` | 每个只做一件事：用 `if (t < first_hit_t)` 写"这条腿最早撞到了什么、在哪"。地面是连续场（整 tick 一次批量采样），建筑是一整格（slab），树干是圆柱，单位是球。**它们什么都不销毁**，所以这四个之间的顺序只决定一次比较的开销 |
| `Impact` | 唯一的读者。它决定接触的含义（直接命中 + 爆、墙只吃爆、引信按测到的间隙先结掉那颗弹），然后销毁这颗弹。**这是唯一结束一颗弹的地方** |

## 7. 伤害链：`blasts` → `mortality` → `economy` → `debris`

这一段顺序是**load bearing**，也是唯一一处"顺序错了会算错数"的地方：

* 弹与其落地的那次爆炸**在自己所在的系统里**写 `hp`（`Combat` 是个库，不是系统）。
* `Suppress` 夹在 `Impact` 与 `Blasts` 之间，因为它是**唯一**同时需要这两边的东西：本 tick 的
  弹道段（`proj_prev → pos`，`Impact` 之后就被销毁了）与本 tick 的爆（`Blasts` 之后就被判掉
  并销毁了）。它写的是机组的方位轮盘（`alarm` 与 `laser` 两张表），AI 块下一 tick 才读——一个
  tick 的延迟，换来"最响的东西不会被点采样跳过去"。同一个系统也读本 tick 的 `beam_lit`
  （`Beams` 在它之前跑），激光那条来源因此与近失弹一样是一次 tick 的延迟。
* `Blasts.run` 晚一个阶段判爆：它读上一段留下的爆，也包括上一 tick 的死亡留下的爆。
  所以链式爆炸天然隔一 tick，炸不到已经变成残骸的那个壳子。
* `Mortality.run` 是**唯一的判官**：一次问所有单位 `hp <= 0`，然后走类型自己的死亡配方
  （销毁、残骸、爆、击杀线、赏金、复活计划、胜负）。在它之前，hp=0 的壳子仍然在记录里。
* `Economy.run` / `Debris.run` 收尾：收入与复活等待、残骸与尸体的倒计时。

放在这里的两条推论：`hp` 是"本系统自己那一段的 hp"（早读晚读不一样）；没有任何系统在遍历自己的
列表时销毁实体。

## 8. 模式的 post-systems（battle 的 `waves`）

`mode_mod.post_systems()`。battle 只有 `Waves.run`，它数壳子占点、发增援——所以它必须在
`mortality` 之后，否则占点会漏掉本 tick 刚死的那个。range 只有 `Record.run`（`MECH_RECORD` 的追踪）。

## 9. 30 Hz 与空服暂停

`Net/Server.cs` 的 `game_loop` 是唯一的调用者：

* `TICK_DT = 1f / 30f`，`next_t += TICK_DT` 累加而不是每次重置，所以一 tick 慢了会补、
  追不上就重新基准（`next_t = now`）而不是永久落后。`world.update` 与 `broadcast` 在同一把
  `_wlock` 里，所以快照永远是一 tick 的完整状态。
* `TPS` 按墙钟每秒结算一次，塞进时钟块——跑不满 30 的服务器报 < 30，而不是照着标称报数。
* **空服就是暂停服**：`PAUSE_WHEN_EMPTY`（= `!DEBUG`）且 `CONNECTIONS` 为空时，循环
  `await Task.Delay(IDLE_SLEEP_MS)`（100 ms）后 `next_t` 重新基准再 `continue`：不 tick、不广播、
  不追时间。所以空了一小时的服务器不会在下一个飞行员进来时交给他一场打完的战争。
  `MECH_DEBUG=1` 关掉这个暂停，因为那正是"外面有脚本在看着跑"的那种运行。
  `GET /events/kills` 的读者是听众、不是飞行员：它拿到心跳，世界照旧停着。
* 直接驱动 `world` 的工具（`Harness/`）**不走这个循环**，两边都不受影响。

## 10. 改这条流水线的规矩

1. 新系统先问"它读谁写的、谁读它写的"，然后把它插到那个位置；顺序的**理由**写进
   `_build_pipeline` 的注释，因为下一处改动会照着那行注释判断。
2. 只有模式的规则才进 `pre_systems` / `post_systems`；共享机制一律 `Systems/`，两者之间不许有
   `if mode == ...`。
3. 判定按形状/种类分工的系统（`FirstHit.*`、`Flights.*`）之间不许有顺序依赖——它们的顺序只该是
   开销的取舍。真想加一条语义依赖，就把它挪进唯一判官（`Impact` / `Mortality`）。
4. 加一个系统之后跑一遍 `./dotnet.sh run -- --check battle --minutes 12` 与 `--check stuck`
   （读数见 [checks.md](checks.md)）；只改文档或注释的改动不跑。

* 存储、三扇门、不变式：[ecs.md](ecs.md)
* 每段怎么量、基线读数：[checks.md](checks.md)
* 伤害、命中判定、阵亡的规则：[../product/rules/damage.md](../product/rules/damage.md)
* 弹的气动与制导律：[../product/systems/flight.md](../product/systems/flight.md)
