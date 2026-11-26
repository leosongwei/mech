# 1.42.0 / 1.43.0（观瞄孔径与激光警报）缺陷调查

日期:2026-11-25
状态:调查完成。P1 / P2 / P3 / P5 / P6 已由 1.44.0 解决（见 §1.1），P4 / P7 保留为读数口径提醒
范围:核对的两个版本是 `f1bac29`（1.42.0，`ai: give crews an aperture beside the gun`）与
`f314c78`（1.43.0，`ai: answer a laser held on the hull`），基线是 `20cb608`（1.41.0）。
修复方案见 [AI_alarm_wheel.md](AI_alarm_wheel.md)，设计意图见 [AI_sight_plan.md](AI_sight_plan.md)。

---

## 1. 结论

1.43.0 修好了 1.42.0 的 `flinch` 时钟缺陷（§4），但两条新机制之间还留着一处同形的漏洞：
**近失弹警报每 tick 把注意状态的 8 s 时钟续满，于是在持续火力下机组的 12 s 扇区扫描基本
不再执行**（P1）。此外"手里没有目标的那 8 s"是冻结而非重扫（P2），`Suppress` 每 tick 分配
（P3），1.42.0 的验收读数取自带缺陷的构建（P4），以及五处文档与代码不一致（P5）。

| # | 问题 | 归属 | 严重度 | 证据 |
| --- | --- | --- | --- | --- |
| P1 | 被打使 `hold` 续满 8 s：交战中孔径几乎不再搜寻 | 1.42.0 引入，1.43.0 未修 | 高 | 每 tick `ENGAGE` 53 人中有 40 人手里没有目标，其中 47.6 % 的 `hold` 在 7.5 s 以上 |
| P2 | 无目标时孔径"冻结"在一个方位 8 s，不是重扫 | 1.42.0 引入 | 中 | `Watch.cs:112-130`：该分支不写 `gaze_cmd`；文档未描述 |
| P3 | `Suppress.run` 每 tick 分配 6 个容器 + `int[alive]` | 1.43.0 引入 | 中 | `Suppress.cs:124-125`；实测 0.146–0.178 ms/tick（占 2.33 ms/tick 的 7 %） |
| P4 | 1.42.0 的战斗验收读数取自带 `flinch` 缺陷的构建 | 1.42.0 | 中 | `git show f1bac29:Systems/AI/Suppress.cs` 只有 `flinch_t = MEM`，没有递减 |
| P5 | 五处文档 / 注释与代码不符 | 1.42.0 / 1.43.0 | 低 | §3 的 P5 表 |
| P6 | `--check gaze` 只测机甲机组 | 1.42.0 | 低 | `GazeCheck.cs:268-275` |
| P7 | 同种子两次 12 分钟战斗的射弹数与击杀数分叉 | 既有 | 信息 | 23029/534 对 31255/501 的对照只能看量级 |

全部检查通过（`gaze` / `fcs` / `stuck` / `range` / `net` / `tiers` / `run_tests.sh` 44 项 /
客户端构建），`ecs.validate()` 干净，构建零警告。

### 1.1 处置结果

| # | 1.44.0 之后 |
| --- | --- |
| P1 | **已解决。** 注意时钟随 `gaze_t` 一起删除；被打只往所属方位格写分数与 1.5 s 余温，"该看哪边"与"扫到哪了"不再共用一根时钟。`--check gaze` 的 `two-way` 一行读到孔径 44 % 的时间在转场、两格都到过 |
| P2 | **已解决。** 没有目标即落回 `SWEEP`，扫视取"最久没看过的一格"（`eye` 记上次看过的时刻），不存在"冻结 8 s"这一分支 |
| P3 | **已解决。** `SuppressScratch` 挂在 `world.suppress`，定位表 / 盒 / 激光 / 弹 / 爆五个数组按需扩容；`Ecs.query_into` 与 `Grid.box_into` 取代每 tick 的容器与 `int[alive]` |
| P4 | **保留。** 1.42.0 的验收读数带缺陷，不再被引用；此后每次报战斗读数都写明构建与 seed |
| P5 | **已解决。** `net.md` 的行宽补回 76 / 48，`rwr.md` §1.7 与 `ai.md` §1.4 按实现重写，`Suppress` 的注释随重写一并更新 |
| P6 | **已解决。** `--check gaze` 补了 `foot`（步兵无轮盘）一行；`laser-cross` / `mark` / `target-first` 覆盖此前没测的交互 |
| P7 | **保留。** 读数只比量级。1.44.0 与 1.43.0 同用 seed 11：2.6 对 3.79 ms/tick、24857 对 31255 发 |

## 2. 方法与读数

```
MECH_SEED=11 ./dotnet.sh run -- --check battle --minutes 12      # 1.43.0 与 1.41.0 各一次
MECH_PERF=1 MECH_SEED=11 ./dotnet.sh run -- --check battle ...    # 每分钟开销
./dotnet.sh run -- --check gaze | fcs | stuck | range | net | tiers
```

逐系统开销与注意状态分布是本报告一次性加的临时仪表（在 `Harness/Check.cs` 的战斗循环里包一层
计时器、每 tick 统计 `ai_gaze` / `ai_tgt` / `flinch` / `gaze_t`），读数取完即还原，仓库里没有
留下改动。1.41.0 的对照在同一套仪表上跑在 `git worktree` 里。

12 分钟、seed 11、四个机器人玩家：

| 项 | 1.41.0 | 1.43.0 |
| --- | --- | --- |
| ms/tick（含线路） | 2.41 | 3.79 – 3.84 |
| 射弹 | 23029 | 31255 |
| 击杀 | 534 | 501 |
| 发/杀 | 43 | 62 |
| 结束实体 | 476 | 583 |

8 分钟切口的逐系统读数（同一套仪表）：

| 系统 | 1.41.0 | 1.43.0 |
| --- | --- | --- |
| 合计 | 2.14 | 2.33 |
| `Suppress`（新） | — | 0.146 |
| `Watch`（新，两段） | — | 0.026 |
| `Sense` | 0.189 | 0.180 |
| `EngageFoot` | 0.482 | 0.465 |

两条推论:

* **新增 AI 工作的固定开销约 0.17 ms/tick（+8 %）**，不是 12 分钟总量差的来源。
* 12 分钟的量差主要来自**空中弹丸更多**：每分钟开销与 `proj` 数同向——第 9 分钟两版
  4.18 / 3.61 ms/tick（`proj` 95 / 103），第 12 分钟 2.39 / 8.47（`proj` 80 / 149）。逐分钟
  读数因此不可跨版本相减，见 P7。

10 分钟战斗里每 tick 的注意状态（1.43.0，seed 11，209 个机组）：

| 项 | 读数 |
| --- | --- |
| `AiGaze.ENGAGE` | 53.1 |
| 其中手里**没有**目标（`ai_tgt` 已清） | 40.1 |
| `AiGaze.SEARCH` | 156.1 |
| `flinch > 0` | 23.4 |
| 上述 40.1 人的 `hold` 均值 | 5.96 s |
| ...其中 `hold > 7.5 s` 的比例 | 47.6 % |

`hold` 只由两件事写满：`fresh`（上一 tick 真的看见）与 `hit`（`flinch > 0`）。这 40 人手里
没有目标，所以 `fresh` 必为假——**接近一半的"凝视"是被警报续着的**。

## 3. 问题逐条

### P1 被打把注意状态的 8 s 续满，交战中孔径不再搜寻

```csharp
// Systems/AI/Watch.cs:105
if (fresh || hit) r.hold = HOLD;      // HOLD = 8 s
else if (r.hold > 0f) r.hold = MathF.Max(0f, r.hold - dt);
bool engage = known || r.hold > 0f;
```

`flinch` 的余温是 `Suppress.MEM` = 1.5 s，但它每 tick 出现一次就把 `hold` 重置为 8 s；
而 `engage` 为真时 `search_clock` 归零（`Watch.cs:109-110`），扇区扫描从头开始。于是一台
每隔两秒挨一发的机组：警报 1.5 s → 8 s 凝视 → 刚要走完第一段 3 s 扇区又被下一发打断。

影响:

* **对任何一台机组，只要它还在挨打，12 s 扇区扫描就停着**：全场任何时刻约四分之一的车组
  （53.1 / 209.2）在这种状态里，其中四分之三手里没有目标。产品文档与 CHANGELOG 把"搜寻"
  描述成常态（[ai.md](../product/systems/ai.md) §1.4、CHANGELOG 1.42），实际只发生在尚未
  挨打的车组上。
* 警报方位是**弹药来向**，可以是侧后。孔径被钉在那里时，±40 ° 窗口随之离开行进方向，
  该机组对正前方的东西看不见——叠加"正后方永不扫"，实际盲区比文档写的更大。
* 与 1.42.0 的 `flinch` 缺陷是同一个形状（时钟不回落 → 状态锁死），1.43.0 只修了输入，
  没有修这处叠加。

这不是调参能收口的，两种改法都要一次取舍：让 `hit` 只买 `MEM` 而不续 `HOLD`
（`r.hold = MathF.Max(r.hold, MEM)`），或者把"警报把头部扳过去"与"注意状态的 8 s 时钟"
拆成两根轴。前者两行，后者更接近设计意图（D3 的"正交"）。

### P2 无目标时的 8 s 是冻结，不是重扫

```csharp
// Systems/AI/Watch.cs:112-130
if (known)          r.gaze_cmd = <目标方位>;      // :115
else if (hit)       r.gaze_cmd = r.flinch_yaw;    // :120
else if (!engage)   r.gaze_cmd = <搜寻扇区>;      // :124
// else: 什么也不写 —— 孔径停在最后那个方位上（:129-130 的注释即此意）
```

丢掉目标后，机组先有 2 s 的枪的记忆（`Crew.MEMORY`），随后 6 s 盯着一个空格子：这段时间
`Sense` 的窗口仍然只有那 ±40 °，扫描钟也不走。最坏情况下，某个方向要等
`Crew.MEMORY` 2 s + `HOLD` 8 s + 一个扫描循环 12 s 才重新被覆盖。文档只写了"持续 8 s 没再真的看见，才回到搜寻"，没有写这 8 s 里
孔径不动、也不扫。至少要在 ai.md §1.4 写明；若要改，可让最后 3 s 归扫描钟接管。

### P3 `Suppress` 每 tick 分配

```csharp
// Systems/AI/Suppress.cs:124-125
var idx = new int[e.alive.Length];        // 每 tick 一个新数组
var cx = new List<float>(); ... cz, cfac  // 四个 List<float> + 一个 List<byte>
```

外加 `e.groups(...)` 两次、`e.query("beam_tgt")` 一次、`e.query("lockable")`（有射线落在
车体上时）。同一份代码里的先例是形状相同的 `ThreatTable`——一张挂在 `World` 上、按 tick
重建的平表（`Systems/AI/Support/ThreatTable.cs` 文件头）。实测 0.146–0.178 ms/tick，占
2.33 ms/tick 的 7 %，其中相当一部分是分配与 GC 压力而非算术。

### P4 1.42.0 的验收读数取自带缺陷的构建

`git show f1bac29:Systems/AI/Suppress.cs` 的写盘段只有 `r.flinch_t = MEM;`，没有递减；
`Watch.cs` 的 `hit` 因此恒为真，`engage` 恒为真。1.42.0 提交信息里的
"`--check battle` 528 杀 / 25386 发对 536 / 28742（交火密度没掉）"是在这个构建上取得的，
它与 1.41.0 的差异里混着"机组的眼睛被警报钉住"这一项。CHANGELOG 1.42 的"发现要靠看"
那一条同样应视为未经有效验证。

### P5 文档与代码不一致（五处）

| 位置 | 现在写的是 | 实际 |
| --- | --- | --- |
| [net.md](../product/systems/net.md) §2.2 的字节表（:107） | 机甲 / 装甲车 = **74 / 46** | 76 / 48（同页 §1.3 已改，表格漏改）|
| [rwr.md](../product/systems/rwr.md) 头（:5） | 核对 **1.39.0 / 2026-11-20** | 1.43.0 改过 §1.7，戳未更新 |
| CHANGELOG 1.42 "炮口朝前不动" | 搜寻时炮口朝前 | 搜寻时没人写 `sight_yaw`，炮口停在**最后一个目标**的方位（`EngageMech.cs:35`、`EngageVehicle.cs:34` 在无信念时直接返回）|
| `Watch.cs:49-54` 的 `heading()` 注释、ai.md §1.4 的第一行表 | 装甲车的 `yaw` 归驾驶员、`Integrate.turn_legs` 跳过轮式，故用 `yaw` | `Movement/Vehicle.cs:117-118` 把 `leg_yaw` 与 `yaw` 写成同一个值，`wheeled` 分支与 `leg_yaw` 等价——错的是注释与那张表 |
| ai.md §1.4 第三条"被打" | 读起来是无条件回头 | 只在手里没有目标时转向（`Watch.cs:115` 的 `if (known) ... else if (hit)`；同节第四条其实写明了）|

### P6 检查覆盖缺口

`--check gaze` 的布景只造机甲机组（`GazeCheck.cs:268-275`）。没有被任何检查覆盖的规则:

* 步兵**永远**不会被激光警报叫醒（`paint` 只挂在非 `person` 的机组上，`Core/World.cs:294`）；
* 激光警报压过近失弹（两者同一个 tick 内后写者胜，`Suppress.cs:291-300`）；
* 手里有目标时孔径不转向警报（P1 的行为前提）；
* `HOLD` → `SEARCH` 的过渡，也就是 P1、P2 本身；
* 装甲车的 `wheeled` 分支（顺带验证 P5 的注释问题）。

### P7 读数只能看量级

同一 `MECH_SEED` 下，两版 12 分钟的射弹数与击杀数都会分叉：`Waves` 的派兵、`Sense` 的
`ACQUIRE` 抖动、目标换手都与遍历顺序耦合。1.42.0 提交信息的方向（改后打得更少）与本报告
测到的方向（改后打得更多）相反，就是这种分叉。判断机制是否出问题要看每 tick 的状态计数
（§2 的下半张表），击杀数只能看量级。

## 4. 1.43.0 确实修好的

* `flinch` 的时钟回到 `Suppress.run` 的第 4 步（`Suppress.cs:211`），`MEM` 1.5 s 之后归零：
  没有这一条，一台机组挨过一发就再也不会搜寻。
* 激光警报的防抖放在几何上而不是计时器堆叠上：`paint` 连续覆盖，断一拍即归零
  （`Suppress.cs:294-296`），通 0.40 s / 断 0.20 s 的横扫不会叫醒机组（`--check gaze` 的 `sweep` 行）。
* 每台设计ator 只问一台车体（`Suppress.cs:242-289`），避免 `painting` 的
  O(机组 × 设计ator × 车体)；同时保留了不能省的顺序——先 `ray_sphere`，过了才做
  `body_hit` 的逐车遮蔽。
* `painting` 与 `Suppress` 共用 `Beams.body_hit` / `hull_sphere`（`Beams.cs:304-369`），
  两条判据不会漂移。

## 5. 建议的处理顺序

1.44.0 按这一步序落地（P1/P2/P3 合并为一次重写，见 [AI_alarm_wheel.md](AI_alarm_wheel.md)）：

1. **P1**：先定取舍（`hit` 只买 1.5 s，还是把警报与注意时钟分成两根轴），再改两行；
   改完给 `--check gaze` 加一条"持续火力下扫描仍在跑"的用例，用 §2 的每 tick 分布当验收。
2. **P2**：ai.md §1.4 写明冻结，或让扫描钟在 `HOLD` 的最后一段接管。
3. **P4**：1.42.0 的验收数字不再被引用；下一次报战斗读数时写明所用的构建。
4. **P5**：五处文档一并改（net.md 的字节表最容易被当成真值）。
5. **P3 / P6**：按 `ThreatTable` 的形状把平表挂到 `World`；`--check gaze` 补步兵与装甲车
   两个布景。
