# 文档重构：AGENTS.md 只留原则，产品文档按读者分家

日期:2026-11-20
状态:已实施（2026-11-20，一次落地，C1–C6 合并为一轮）
范围:只动文档。代码、数值、玩家看到的东西一行都不变。
版本:纯文档改动 → **不动 `MechServer.csproj` 的版本（1.39.0 保持）**，不写 `CHANGELOG` 条目。

## 落地结果

| 项 | 计划 | 结果 |
| --- | --- | --- |
| `AGENTS.md` | ≤ 450 行，只留原则/流程/模块 | **2489 → 334 行**（14 条原则 + 一 tick 的流水线 + 状态所有权表 + 功能索引） |
| 产品文档 | 5 组 27 页 | **27 页 + `README`**，另有 `changelog/2026-10.md` 归档；总行数 7033 → 6500 |
| 旧页 | 8 → 0 | 6 页删除（`screens`/`units`/`weapons`/`rwr`/`range`/`killed_sse`），`flow`/`README`/`CHANGELOG` 原地重写 |
| 工程手册 | `docs/dev/` 5 页 | `ecs`、`pipeline`、`client`、`checks`、`browser` |
| 链接 | 无死链 | **0 条断链**（`docs/plans/frontend.md` 里的一处 `](...)` 是示例文本） |
| 一处事实一个家 | 抽查 12 个常量 | 12 个常量各自命中 **1 页**；跨页出现的是"见某页"的指路，不是数值副本 |
| CHANGELOG | 按年月归档 | 99 条 2026-10 归档，保留 2026-11 的 64 条；新条目格式 ≤ 8 行 |

执行中**按代码纠正**了旧文档的数字（旧文档与 `Core/Defs.cs` 不一致时以代码为准）：

| 位置 | 旧文档 | 代码（已采用） |
| --- | --- | --- |
| 攻顶弹的凝视点 | 车高 92 % | `AIM_ROOF = 0.78`（车高 78 %） |
| 重生倒计时 | 3–40 s / 40 s → 3 s | `RESPAWN_AT_FRONT = 1.5` / `RESPAWN_AT_HOME = 20` |
| AI 装备模板 | 6 套 30/10/10/25/15/10 % | `AI_LOADOUTS` 7 套 20/10/10/20/15/15/10 % |
| 破袭队规模 | 每路一台 | `RAID_SIZE = 2`（一队两台，车道三选二） |
| 小地图量化 | 4 m | `CONTACT_QUANT = 8`（u16，16 km → 2000） |
| APC 命中球 | 1.4 / 1.78 m 两处 | `MathUtil.max_radius` = 1.7 m |
| 客户端火控 | `frontend/src/fcs.js` | `frontend/src/core/fcs.js` |
| 驾束弹熄火后速度 | 560 / 542 m/s | 实测 562 / 546 m/s（`--check beam`） |

一句话目标：

1. `AGENTS.md` 只写**基本原则、开发流程、模块划分**，从 2489 行降到 **450 行以内**；
2. `docs/product/` 从 8 个"什么都装"的大页拆成 **按读者分的 5 组 27 页**，每页一个主题、一类读者；
3. 一条事实只有一页负责（数值、口径、公式、判定），其它页只放链接；
4. **AGENTS.md 里删掉的功能内容，全部落到产品文档**，并补上今天两份文档都没写清的判据（见 §4）。

---

## 0. 现在的读数（2026-11-20）

### 0.1 AGENTS.md

2489 行 / 182 KB。按"这段文字在回答什么问题"分成四类：

| 类 | 行数 | 占比 | 例子 |
| --- | --- | --- | --- |
| A 原则（约束怎么写代码） | 651 | 26 % | 组件集就是类型、伤害不是系统、视线只有一条门 |
| B 流程（怎么建、跑、验） | 460 | 18 % | `dev.sh`、`--check`、日志等级、版本号 |
| C 模块（谁住在哪） | 190 | 8 % | 目录树、ECS 三扇门、Grid、`window.__game` |
| D **功能**（玩家能看见的规则、数值、实测） | 1179 | 47 % | RWR 判据、驾束导弹电机、机甲推力曲线、快照分层 |

四类合计 2480 行（99.6 %）。另有约 270 行是 A–D **内部的重复陈述**，不单独占行：
`MECH_DEBUG` 说了 4 次、`dotnet.sh` 说了 2 次、`--check` 的读数与产品页各写一遍。

问题不在长度本身，而在**它把功能写成了散文**：每描述一个机制就要连带写"为什么这样、实测多少"，
于是机制一改，AGENTS.md 就腐坏。已有证据：1909–1915 记录一个已被删除的 `bassLift`，
1359–1360 是删除留下的断句，625–627 还写着"Players are plain Python objects"，
653 指向不存在的 `systems/mortality.py`。

### 0.2 docs/product

7033 行，其中 `CHANGELOG.md` 3562 行（50.7 %），其余 8 页 3471 行。

| 文件 | 行数 | 实际混着的读者/主题 |
| --- | --- | --- |
| `flow.md` | 384 | 玩家流程 + netcode（分层/时钟/拥塞）+ raider 规划算法 + 调试 |
| `screens.md` | 660 | HUD 每个像素 + 火控解算 + 换装经济 + AI 信念 + 音频 DSP |
| `units.md` | 449 | 数值表 + 推力推导 + 坡度松弛 + navmesh 开销 + AI 策略 + 地图生成 |
| `weapons.md` | 750 | 数值表 + 经济 + 气动/电机 + AI 节奏 + 客户端解算 + 105 mm 录音分析 |
| `rwr.md` | 653 | 玩家规则 + 270 行数学推导 + 实测附录 + JS 函数清单 + 设计取舍 |
| `range.md` | 303 | 启动命令 + 靶位坐标 + 调试 + "怎么测"教学 |
| `killed_sse.md` | 221 | 外部 API 契约 + 口径 + 维护者寻址表 |
| `README.md` | 51 | 索引 + 术语 + 维护规则（含 2 个死链） |
| `CHANGELOG.md` | 3562 | 玩家变更 + 工程实测 + 文档维护说明 |

具体病症（都带行号，执行时按此清单核对）：

1. **一份事实四份副本**：换装定律在 `flow.md:105-121`、`screens.md:325-413`、`weapons.md:45-94`；
   雷达数在 `units.md:84-111`（写 2729 m）、`weapons.md:609-633`、`rwr.md:230-233`（写 2728 m）、
   `screens.md:230-255`——**两份副本已经互相矛盾**。
2. 近炸/拦截写 3 处（`weapons.md:522-608`、`units.md:371-393`、`range.md:104-124,204-227`），
   `units.md:371-393` 与 `weapons.md:649-672` 近乎逐字重复。
3. 锁定/通道/信念写 4 处（`flow.md:71-87`、`screens.md:256-314`、`weapons.md:289-473`、`README.md:37`）。
4. 死亡/重生/城镇写 4 处（`flow.md:163-265`、`units.md:420-448`、`screens.md:482-500`、`README.md:32-35`）。
5. 客户端火控写 2 处（`screens.md:128-229`、`weapons.md:673-717`）；AI 行为写 2 处
   （`units.md:304-403`、`weapons.md:634-672`）。
6. **结构漂移**：`screens.md:395` 的 2.16 排在 `screens.md:414` 的 2.13 之前；
   `range.md:77,104` 的 3.5/3.6 排在 `range.md:125` 的 3.1 之前，且层级一 H3 一 H2。
7. **死引用**：`README.md:4-5` 指 `Systems/Spawner.cs`（不存在）与 `frontend/src/hud.js`（已拆成 `hud/`）；
   `AGENTS.md` 指 `backend/`、`run.py`、`tools/intercept_check.py`、
   `docs/plans/AI_plan.md`、`docs/plans/speed_component.md`（后两个不存在）；
   `CHANGELOG.md` 有 150 行写 `backend/*.py`，32 行写 `frontend/src/hud.js`。
8. `CHANGELOG.md` 里 163 条平均 21 行，写的是根因、实测、shader 细节、被否决的第一版——
   它是**工程日志**，不是玩家能读的变更表。

---

## 1. 拆分判据

### 1.1 一段文字归谁：三个问题，按顺序问

1. **它约束"代码怎么写"吗？**（所有权、唯一写者、顺序、形状、不变量）→ `AGENTS.md` 原则。
2. **它说"玩家看到什么 / 数字是多少 / 怎么判"吗？**→ `docs/product/`。
3. **它是"我怎么跑起来、怎么量出来"吗？**→ `AGENTS.md` 流程，长的进 `docs/dev/`。

一段同时是两者时：**原则留一句（不带数字），数字进产品页，AGENTS 末尾放链接**。
例："观瞄塔与炮口分离，机身追着视线转" 是原则；80/50 deg/s、−26°/+43°、0.62 m 是
`systems/firecontrol.md` 的数值表。

### 1.2 四层文档，各自只有一个职责

| 层 | 位置 | 唯一职责 | 一条判据 |
| --- | --- | --- | --- |
| 工作笔记 | `AGENTS.md` | 原则、流程、模块 | 出现玩家能看见的数字就不合格 |
| 产品文档 | `docs/product/` | 规则、数值、界面、判定 | 一页只服务一类读者 |
| 工程手册 | `docs/dev/`（新建） | 怎么跑、怎么测、怎么量、客户端约定 | 只在开发时读 |
| 计划 | `docs/plans/` | 为什么这样设计、分几步做 | 完成后冻结，不再追改 |

`docs/roadmap.md` 留原位，作为待办清单。

### 1.3 一条事实一个家（执行时按这张表分派）

| 事实类别 | 唯一归属页 |
| --- | --- |
| 单位血量/速度/体型/命中球/雷达 | `rules/units.md` |
| 武器全参数、管阵、弹药 | `rules/weapons.md` |
| 穿透/爆破/溅射/近炸/阵亡爆炸/拦截/导弹作为目标/命中判定顺序 | `rules/damage.md` |
| 价格、回收、换装工时、收入、赏金 | `rules/economy.md` |
| 地形、道路、树、城镇、据点、车道、镜像 | `rules/map.md` |
| 出生、三步选城、重生倒计时、首次部署 | `rules/spawn.md` |
| 波次、增援、raider 的任务规则、占领、胜负、重开 | `modes/battle.md` |
| 靶场的一切 | `modes/range.md` |
| 调零、发射几何、双端同解 | `systems/firecontrol.md` |
| 弹道、气动、制导律、电机、电池 | `systems/flight.md` |
| 锁定、通道、记忆、重捕、信念点 | `systems/seekers.md` |
| 告警器盘面与五层判据、阈值 | `systems/rwr.md` |
| 机甲五定律、APC 自行车模型、爬坡、脱困 | `systems/movement.md` |
| 机组感知/接战/拦截/raider 规划/导航 | `systems/ai.md` |
| 视线、树冠、烟幕、激光判定球 | `systems/sight.md` |
| 衰减、远场、总线、限幅、声部预算 | `systems/audio.md` |
| 帧格式、行宽、分层半径、时钟、拥塞、RTT、小地图 | `systems/net.md` |
| HUD 尺寸、文案、配色、动效 | `hud/*.md` |
| 按键与操作 | `controls.md` |
| 调试消息、控制台自动化 | `interfaces/debug.md` |
| 击杀流格式与口径 | `interfaces/kills_sse.md` |

---

## 2. 新的 AGENTS.md

### 2.1 目录与行数预算

| 节 | 行数 | 内容 |
| --- | --- | --- |
| 0. 这个仓库 | 6 | 一句话：浏览器机甲射击；C# .NET 10 archetype-ECS 服务端、WebGL2 客户端、30 Hz、16 km 图；指向 `docs/product/README.md` |
| 1. 文档地图与写作规则 | 35 | 四层文档谁写什么；一次产品改动的固定顺序（代码 → 产品页 → CHANGELOG → 版本）；语言、单位、最小改动、正述规则 |
| 2. 开发流程 | 110 | 构建与重启、运行、验证（13 个 `--check` 一行一个，读数进 `docs/dev/checks.md`）、版本与登记、日志等级、沙箱注意、浏览器验证入口 |
| 3. 模块划分 | 150 | 目录树（压缩版）+ 一 tick 的流水线代码块 + 客户端四层 + 状态所有权表 |
| 4. 基本原则 | 110 | 14 条，每条 5–8 行，末尾一行"细节：…" |
| 5. 功能索引 | 25 | "想知道 X → 读 Y" 表，25 行 |

合计 **≤ 450 行**。

### 2.2 四条写法硬规则

1. 一段只说一条规则，以及它保护的不变量。**数字、公式、实测、历史沿革一律不留**。
2. 每条原则末尾一行 `细节：docs/product/systems/xxx.md`（或 `docs/dev/`），链接必须可达。
3. 新增一条原则的门槛：**它能判"这段代码写错了"**。描述某个武器/单位/HUD 元素怎么工作，
   一律进产品页；想加入 AGENTS.md 的功能段落在评审时直接打回。
4. AGENTS.md 里 `实测`、`measured`、`--check` 的具体读数出现次数为 **0**；基线数字住
   `docs/dev/checks.md`，并标明测的是哪一版。

### 2.3 保留的 14 条原则

| # | 原则 | 一句话 | 细节 |
| --- | --- | --- | --- |
| P1 | 组件集就是类型 | 实体在 spawn 时定死 archetype，换状态是新实体；`etype` 只是数据 | `docs/dev/ecs.md` |
| P2 | 一个 `pos`、一个 `vel` | 命令住 `vel_cmd`，`movement` 是 `vel`/`pos` 的唯一写者，脚本位移两个都写 | `systems/movement.md` |
| P3 | 身份是组件 | 系统按 tag 收实体（`unit`/`combat`/`lockable`/`moves`/`person`），从不按类型分支 | `docs/dev/ecs.md` |
| P4 | 系统对着 buffer 写 | `groups`/`slot`/`locate` 三扇门；`View` 只用于散读 | `docs/dev/ecs.md` |
| P5 | 访问顺序不属于仿真 | 每线程一条随机数流；验证按量级与不变量，不 diff 两次运行 | `docs/dev/checks.md` |
| P6 | 一 tick 的流水线是语义 | shared 段 + 模式的前/后段 + 弹的七段；伤害链顺序 load bearing | `docs/dev/pipeline.md` |
| P7 | 视线只有一条门 | 地形、树冠、烟幕都走 `Sight`；所有"眼睛"共用它 | `systems/sight.md` |
| P8 | 伤害不是系统 | `combat.apply` 是 `hp` 的唯一写者；`blasts` 判爆、`mortality` 判死 | `rules/damage.md` |
| P9 | 弹的结束是一条记录 + 一个判官 | 每 tick 一条 `first_hit_record`，`Impact` 是唯一读者 | `rules/damage.md` |
| P10 | 弹的种类是 tag | 一种弹 = 一个 tag + 一个飞行系统；判定按形状分工 | `systems/flight.md` |
| P11 | AI 是意图，机器是执行 | 机组写 `nav_*`/`sight_*`，驱动写 `vel_cmd`；数据来源是信念 `ai_tgt` | `systems/ai.md` |
| P12 | 服务端权威，客户端插值 | 同一套火控两端同解；客户端只画它在线上收到的 | `systems/net.md` |
| P13 | 文档跟着改动走 | 一处事实只有一页；玩家可见的才算产品变更 | `docs/product/README.md` |
| P14 | 最小改动，先量后调 | 机制在靶场上量，战场只用来验收 | `docs/dev/checks.md` |

（P6 的流水线代码块留在 AGENTS.md §3，因为它是模块结构而不是功能；每一段为什么排在那里住 `docs/dev/pipeline.md`。）

### 2.4 删出去的（按 AGENTS.md 现行行号）

以下段落整段迁出，落点与补写要求见 §4.1：

```
287–328  观瞄/驾驶/换装工时/AI 装备抽取
329–405  AI 意图与机器、机组组件、开火臂、弹的结束记录、弹的种类、lifetime
406–541  火控、每臂各算、主臂、炮口、扳机门、信念、客户端同解、管阵
542–624  Grid、Raider、Modes 细节、死亡选点、地图镜像、争夺城镇
625–760  近炸、导弹作为目标、球表运动、批量、地面连续、坡度、navmesh、按形状、引信结算
761–928  雷达与 RWR、箭头与闪烁、AI 导弹防御、靶场哨位
929–1107 气动、导引头记忆、驾束导弹（通道/锥/记忆/CLOS/电机/实测）
1108–1248 伤害两半、伤害不是系统、死亡裁判、击杀通告、击杀流、爆炸、模块血、残骸、烟幕、车道
1249–1461 机甲五定律、APC、地形数据、树、一条视线、树丛、城镇生长
1462–1667 线格式、快照分层、时钟与拥塞、小地图、调试门与表
1668–2022 客户端全部（UI 进 hud/，瞄准进 firecontrol.md，声音进 audio.md，渲染约定进 docs/dev/client.md）
2202–2237 大厅/ENTER/靶场服务
```

留下来的：176–286（ECS、身份、顺序、World、空服暂停、观瞄分离的**原则句**）、
1108–1136 与 1201–1222 的**原则句**（数值摘要成一行指向 `rules/damage.md`）、
1631–1636 的**一行**门控说明（表进 `interfaces/debug.md`）、
2024–2100 的构建/验证/日志（读数删掉，命令留一行）、2177–2186 的 `tools/` 一行、
2284–2322 的沙箱段、2324–2409 的浏览器入口（食谱进 `docs/dev/browser.md`，AGENTS 留 15 行）。

---

## 3. 新的产品文档结构

### 3.1 目录树

```
docs/product/
  README.md                索引 + 术语 + 维护规则（含"改哪里"表）
  flow.md                  一局的生命周期：启动 → 大厅 → 循环 → 死亡 → 胜负 → 重开
  controls.md              按键与操作
  hud/
    lobby.md               启动页与大厅
    hud.md                 对局 HUD 各区块 + 配色速查
    aim.md                 准星、预瞄圈、吸附、锁定标记、导引头圈
    messages.md            消息栈、击杀行、死亡/胜负屏、受击反馈
    panels.md              换装面板、整备进度、设置菜单、帮助覆盖层
  rules/
    units.md               单位总表、模块、命中球、雷达、残骸
    weapons.md             武器全参数、管阵、弹药与换弹
    damage.md              穿透/爆破/溅射/近炸/阵亡爆炸/拦截/命中判定
    economy.md             收入、赏金、价格、换装工时
    map.md                 地形、道路、树、城镇、据点、车道、镜像
    spawn.md               出生、三步选城、重生倒计时、首次部署
  modes/
    battle.md              波次、增援、raider 任务、占领、胜负、重开
    range.md               靶场模式
  systems/
    firecontrol.md         调零、发射几何、双端同解
    flight.md              弹道、气动、制导律、电机、电池
    seekers.md             锁定、通道、记忆、重捕、信念点
    rwr.md                 告警器：盘面、判据、推导、实测、实现、取舍
    movement.md            机甲五定律、APC 自行车模型、爬坡、脱困
    ai.md                  机组、感知、接战、导弹防御、raider 规划、导航
    sight.md               一条视线：地形、树冠、烟幕、激光
    audio.md               声音场、衰减、远场、总线、限幅、声部预算
    net.md                 帧格式、行宽、分层、时钟、拥塞、RTT、小地图
  interfaces/
    kills_sse.md           击杀流
    debug.md               调试消息与浏览器控制台
  CHANGELOG.md             近期玩家可见变更（≤ 60 条）
  changelog/YYYY-MM.md     归档
```

同时新建 `docs/dev/`（工程手册，中文）：

```
docs/dev/
  ecs.md       存储、三扇门、不变式、validate
  pipeline.md  一 tick 的顺序，以及每一段为什么排在那里
  client.md    渲染 pass、场景局部坐标、at()/headAt()、图标、质量档
  checks.md    13 个 --check 的命令、基线读数（标版本）、读法
  browser.md   控制台、freeCam、debug_tp、无键盘操作
```

### 3.2 每页的固定模板

```markdown
# 标题

> **读者**：玩家 / 调参 / 机制维护者 / 外部集成
> **一句话**：这页回答什么问题
> **数值出处**：`Core/Defs.cs`（`WEAPONS` / `UNIT_DEFS`）
> **核对**：1.39.0 / 2026-11-20

## 1. 规则（玩家能看到/能做的）
## 2. 数值表
## 3. 判定与边界（什么情况算、什么情况不算）
## 4. 设计与取舍（可选：为什么这样）
## 5. 实测（可选：标版本与命令）
## 6. 相关页
```

正文用玩家词汇 + 英文 UI 原文；**代码标识符只出现在"数值出处"行和"实现"小节**，
让页面顶端读起来是产品说明，底部才是维护者的寻址表。

### 3.3 读者动线

| 读者 | 读什么 |
| --- | --- |
| 新玩家 / 外人 | `README` → `flow` → `controls` → `hud/*` |
| 调参与平衡 | `rules/units` → `rules/weapons` → `rules/damage` → `rules/economy` → `modes/battle` |
| 机制维护者 | `systems/*`（先看该页的"规则"，再看"实现"） |
| 外部集成 | `interfaces/kills_sse` |
| 测试 | `docs/dev/checks` + `modes/range` |

### 3.4 旧 → 新映射

**`flow.md`（384）**

| 旧段落 | 行 | 去处 |
| --- | --- | --- |
| 1 进入游戏 | 3–17 | `hud/lobby.md` |
| 2 大厅 | 18–31 | `hud/lobby.md` |
| 3 操作循环（按键/缩放/调零/吸附/ALT） | 32–70 | `controls.md`（流程骨架留 `flow.md`） |
| 3 锁定、烟幕、告警 | 71–104 | `systems/seekers.md`、`systems/sight.md`、`systems/rwr.md` |
| 3 换装与工时 | 105–121 | `rules/economy.md` |
| 3 小地图/快照/时钟/拥塞 | 122–162 | `systems/net.md` |
| 4 受伤、死亡、重生 | 163–215 | `rules/spawn.md` + `hud/messages.md` |
| 5 经济 | 216–227 | `rules/economy.md` |
| 6 城镇占领 | 228–265 | `rules/map.md` + `modes/battle.md` |
| 7 增援 | 266–291 | `modes/battle.md` |
| 7 raider | 292–325 | `systems/ai.md`（算法）+ `modes/battle.md`（规则） |
| 8 胜负与重开 | 326–341 | `modes/battle.md` + `flow.md` |
| 9 设置与调试 | 342–363 | `hud/panels.md` + `interfaces/debug.md` |
| 10 靶场 | 364–384 | `modes/range.md` |

**`screens.md`（660）**

| 旧段落 | 行 | 去处 |
| --- | --- | --- |
| 1 大厅 | 6–28 | `hud/lobby.md` |
| 2.1–2.6 HUD 区块 | 29–101 | `hud/hud.md` |
| 2.7 准星 | 102–127 | `hud/aim.md` |
| 2.8 预瞄/锁定/吸附 | 128–229 | `hud/aim.md`（画面）+ `systems/firecontrol.md`（解算） |
| 2.9 告警圈 | 230–255 | `systems/rwr.md`（盘面）+ `hud/hud.md`（位置） |
| 2.10 导引头圈 / 信念点 | 256–314 | `hud/aim.md` + `systems/seekers.md` |
| 2.11 残骸 | 315–324 | `rules/units.md` |
| 2.12 换装面板 | 325–394 | `hud/panels.md` + `rules/economy.md` |
| 2.16 整备进度 | 395–413 | `hud/panels.md` |
| 2.13 消息 | 414–453 | `hud/messages.md` |
| 2.14 右下角状态 | 454–471 | `hud/hud.md` |
| 2.15 受击与后坐 | 472–481 | `hud/messages.md` + `systems/audio.md` |
| 3 死亡界面 | 482–500 | `hud/messages.md` |
| 4 胜负界面 | 501–509 | `hud/messages.md` |
| 5 帮助 | 510–514 | `hud/panels.md` |
| 6 设置菜单 | 515–577 | `hud/panels.md`（+ `systems/audio.md` 音量、`docs/dev/client.md` 质量档） |
| 7 声音 | 578–648 | `systems/audio.md` |
| 8 配色速查 | 649–660 | `hud/hud.md` 附录 |

**`units.md`（449）**

| 旧段落 | 行 | 去处 |
| --- | --- | --- |
| 单位总表 | 7–83 | `rules/units.md` |
| 自卫雷达 | 84–111 | `rules/units.md`（+ `systems/rwr.md` 公式） |
| 模块与血量 | 115–128 | `rules/units.md` |
| 命中球 | 129–140 | `rules/units.md` |
| 视角/观瞄塔/炮口 | 141–172 | `rules/units.md` + `systems/firecontrol.md` |
| 热烟幕 | 173–198 | `rules/weapons.md`（装备）+ `systems/sight.md`（遮挡） |
| 行驶 | 199–266 | `systems/movement.md` |
| 爬坡 | 267–285 | `systems/movement.md` + `rules/map.md` |
| 寻路 | 286–303 | `systems/ai.md` |
| AI 装备模板 | 304–332 | `rules/units.md` |
| 单位行为 | 333–403 | `systems/ai.md` |
| 残骸 | 404–419 | `rules/units.md` |
| 出生与重生 | 420–432 | `rules/spawn.md` |
| 地图与据点 | 433–448 | `rules/map.md` |

**`weapons.md`（750）**

| 旧段落 | 行 | 去处 |
| --- | --- | --- |
| 全参数表 | 7–44 | `rules/weapons.md` |
| 价格与换装结算 | 45–94 | `rules/economy.md` |
| 伤害规则 | 95–123 | `rules/damage.md` |
| 单发伤害与发数 | 124–169 | `rules/damage.md` |
| 火控自动调零 | 172–236 | `systems/firecontrol.md` |
| 弹道 / 无制导火箭 | 237–288 | `systems/flight.md` |
| SACLOS | 289–385 | `systems/flight.md` + `systems/seekers.md` + `systems/sight.md` |
| 红外末敏弹 | 386–472 | `systems/seekers.md` + `systems/flight.md` |
| 命中与溅射 / 阵亡爆炸 | 473–521 | `rules/damage.md` |
| 近炸引信 | 522–559 | `rules/damage.md` |
| 导弹作为目标 / 拦截 | 560–608 | `rules/damage.md` |
| 自卫雷达发现距离 | 609–633 | `rules/units.md` |
| AI 用导弹 | 634–648 | `systems/ai.md` + `rules/units.md` |
| AI 机炮拦截 | 649–672 | `systems/ai.md` |
| 地面预测 / 预瞄解算（客户端） | 673–717 | `systems/firecontrol.md` + `hud/aim.md` |
| 弹药与换弹 | 718–730 | `rules/weapons.md` |
| 命中手感 | 731–750 | `hud/messages.md` + `systems/audio.md` |

**`rwr.md`（653）**

| 旧段落 | 行 | 去处 |
| --- | --- | --- |
| 1–6 规则（问题/盘面/声音/四种情况/状态跳变/判据链） | 14–207 | `systems/rwr.md` §1 |
| 2.3 箭头渲染细节 | 58–89 | `hud/hud.md`（盘面与箭头）+ `systems/rwr.md`（判据引用） |
| 3 声音 | 90–118 | `systems/rwr.md`（音型）+ `systems/audio.md`（声部） |
| 7 推导 | 208–477 | `systems/rwr.md` 附录 A |
| 8 通视 | 478–489 | `systems/sight.md`（+ 一句引用） |
| 9 激光告警 | 490–524 | `systems/rwr.md` §激光 + `systems/sight.md`（判定球） |
| 10 实测记录 | 525–576 | `systems/rwr.md` 附录 B（标版本） |
| 11 实现（常量/代码/怎么测） | 577–628 | `systems/rwr.md` 附录 C（常量表）+ `docs/dev/client.md`（函数清单）+ `docs/dev/checks.md`（怎么测） |
| 12 风险与取舍 | 629–651 | `systems/rwr.md` 附录 D |

**`range.md`（303）**

| 旧段落 | 行 | 去处 |
| --- | --- | --- |
| 1 启动 | 6–22 | `modes/range.md`（一句）+ `AGENTS.md` §2（命令） |
| 2 地图 | 23–37 | `modes/range.md` |
| 3 靶子 | 38–124 | `modes/range.md` |
| 3.1 碉堡 | 125–146 | `modes/range.md` |
| 3.2 不会还手 | 147–170 | `modes/range.md` |
| 3.3 现场种树/放靶 | 171–203 | `interfaces/debug.md` + `modes/range.md`（一句） |
| 3.4 防御哨位 | 204–227 | `modes/range.md` |
| 4 玩家 | 228–243 | `modes/range.md` |
| 5 建议用法 | 244–288 | `docs/dev/checks.md` §靶场用法 |
| 6 与战斗模式差异 | 289–303 | `modes/range.md` 末 |

**`killed_sse.md`（221）**

| 旧段落 | 行 | 去处 |
| --- | --- | --- |
| 1–3 端点/帧/记录 | 11–105 | `interfaces/kills_sse.md` |
| 4 口径 | 106–144 | `interfaces/kills_sse.md`（+ `hud/messages.md` 引用） |
| 5 心跳与空服暂停 | 145–157 | `interfaces/kills_sse.md` + `systems/net.md` |
| 6 可靠性 | 158–164 | `interfaces/kills_sse.md` |
| 7 怎么读 | 165–201 | `interfaces/kills_sse.md` |
| 8 不提供的 | 202–210 | `interfaces/kills_sse.md` |
| 9 实现位置 | 211–220 | `interfaces/kills_sse.md` 末（维护者表） |

### 3.5 页面预算（行数上限）

| 页 | 上限 | 页 | 上限 | 页 | 上限 |
| --- | --- | --- | --- | --- | --- |
| `README` | 90 | `rules/units` | 320 | `systems/firecontrol` | 300 |
| `flow` | 250 | `rules/weapons` | 340 | `systems/flight` | 300 |
| `controls` | 100 | `rules/damage` | 300 | `systems/seekers` | 320 |
| `hud/lobby` | 90 | `rules/economy` | 150 | `systems/rwr` | 600 |
| `hud/hud` | 250 | `rules/map` | 300 | `systems/movement` | 300 |
| `hud/aim` | 250 | `rules/spawn` | 130 | `systems/ai` | 340 |
| `hud/messages` | 220 | `modes/battle` | 200 | `systems/sight` | 150 |
| `hud/panels` | 260 | `modes/range` | 240 | `systems/audio` | 260 |
| `interfaces/kills_sse` | 220 | `interfaces/debug` | 150 | `systems/net` | 340 |
| `CHANGELOG` | 1700（64 条，新条目 ≤ 8 行后自然变短） | `changelog/*` | 归档，不设限 | | |

预算是**上限**：超过就说明这页又混了读者，回去拆。

---

## 4. 产品文档要补的细节

### 4.1 从 AGENTS.md 迁入（约 1179 行）

不是抄一遍：每行"补什么"是**迁入时要在产品页新写/写全的内容**。

| AGENTS 行 | 内容 | 去处 | 迁入时补什么 |
| --- | --- | --- | --- |
| 287–306 | 观瞄塔与炮口分离 | `systems/firecontrol.md` | 三段角度表（`sight_yaw/pitch`、机身 `yaw`、手臂 `pitch`）、80/50 deg/s、−26°/+43°、0.62 m、AI 的 `err<0.14` 且 `abs(Δpitch)<0.02` 门 |
| 307–321 | 换装耗时 | `rules/economy.md` | 换臂 2 s/臂、弹药 4 s/臂、修理 2 s/100 hp、烟幕装填、第二条订单被拒、工时随车体死亡 |
| 322–328 | AI 装备抽取 | `rules/units.md` | 六个模板比例 30/10/10/25/15/10 %、左臂固定 30 mm、玩家与靶场哨位不走抽签 |
| 329–353 | AI 意图 / 机器 | `systems/ai.md`、`systems/movement.md` | 意图只写 `nav_*`；驱动拥有 `vel_cmd/leg_yaw/...`；腿部对齐导致 81 km/h 的方向性；扇形避障用最小转角 |
| 354–376 | 机组是组件 / 机组开火 | `systems/ai.md` | `ai_crew` 与平台 tag；`min(ai_range, wd.range)`；`lock_range` 与 `range` 的分工；`AI_LAUNCH_GAP` 15 s |
| 377–405 | 弹的结束记录 / 种类 / 寿命 | `rules/damage.md`、`systems/flight.md` | `first_hit_*` 字段表、六种 `HIT_*`、t=0 的到期接触、四种弹 tag |
| 406–450 | 一门火控 / 每臂各算 / 主臂 | `systems/firecontrol.md`、`systems/ai.md` | 两次调零的区别；`Punch` 排序；`MASTER_HANDOFF` 0.5 s；2.86 m 炮口间距的几何结论 |
| 451–476 | 每底盘炮口 / 扳机门 | `systems/firecontrol.md`、`systems/ai.md` | 三种底盘的发射几何；`atan((r+hit_r)/d)` 公式与 `PITCH_TOL`；主臂为何单开火 |
| 477–508 | 信念 / 客户端同解 | `systems/ai.md`、`systems/firecontrol.md` | 0.45 s 扫描与 0.34–0.61 s 抖动、2 s 记忆、视线门；`tests/cases/fcs.json` 的契约 |
| 509–541 | 管阵 | `rules/weapons.md` | `rack()` 参数表（5×8 / 2×4 / 4×4）、装填顺序、`elev`/`bolted` 的语义、一发一管、无再装填 |
| 542–553 | Grid | `docs/dev/ecs.md` | 128 m、每 tick 重建、`moves ∪ combat` 两个 tag 的理由 |
| 554–580 | Raider | `systems/ai.md`、`modes/battle.md` | 力梯度、128 m A\* 与 16 m 街道网两层、`STUCK_SKIP/REPLAN`、MARCH/ASSAULT/HOLD |
| 581–588 | 模式拥有什么 | `AGENTS.md` §3（一行）、`docs/dev/ecs.md` | 模式只决定地图、初始、系统块与规则查询；共享系统里没有模式分支 |
| 589–602 | 死亡选点 | `rules/spawn.md` | A/B/C 三步、40 s→3 s 曲线的形状、首次部署的"开局死亡点" |
| 603–624 | 地图镜像 / 中立镇 / 争夺 | `rules/map.md` | 11+11 镇、4/4/3、两处 9984 m 的中立镇、170 m 争夺圈、被争夺即断补给 |
| 628–642 | 近炸引信 | `rules/damage.md` | 只对 `person`、2.5 m、解"这一 tick 飞过的那条腿" |
| 643–676 | 导弹作为目标 / 球表 | `rules/damage.md`、`docs/dev/ecs.md` | 10 hp / 1 甲、无残骸无战功、相对腿解算、宽相盒按最快位移放大 |
| 677–686 | 批量与逐个 | `docs/dev/checks.md` | 几何可批、积分不批；两次实测的结论与读法 |
| 687–738 | 地面/坡度/navmesh | `rules/map.md`、`systems/movement.md`、`systems/ai.md` | 三角面对角线规则、`height.json` 契约、0.85 上限与各底盘限制、A\* 切片与判据 |
| 739–760 | 按形状查询 / 引信结算 | `rules/damage.md`、`systems/ai.md` | 地面/建筑/树干/单位四种查询、引信就地结算（2.5 m 触发对 10 hp） |
| 761–928 | 雷达与 RWR / 箭头 / AI 拦截 / 靶场哨位 | `systems/rwr.md`、`rules/units.md`、`hud/hud.md`、`systems/ai.md`、`modes/range.md` | 雷达方程与发现距离表、盘面 3 km、箭头三规则与闪烁节拍、CPA 两条判据、哨位重生 5 s |
| 929–952 | 气动 | `systems/flight.md` | 点质量 + 攻角、`CL/CD` 极曲线、42° 抛射、三段制导、PN 门、电池 30 s |
| 953–1001 | 导引头记忆 | `systems/seekers.md` | 3° 锥、92 % 甲板、信念种子（0.5 s / `lock_range`×1.5）、重捕按信念点距离排序、无客户端提示 |
| 1002–1107 | 驾束导弹 | `systems/seekers.md`、`systems/flight.md`、`systems/sight.md` | 4 通道、40° 锥、8 s 记忆、CLOS 律、电机 40 m/s→100 m/s²×7.3 s→600 m/s 与速度表、30 g/3 g、激光判定球 10 m |
| 1108–1136 | 伤害两半 / 死亡裁判 | `rules/damage.md` | `pen_damage`/`blast_damage` 公式、命中与溅射的区别、`apply` 的边界、死亡唯一裁判 |
| 1137–1200 | 击杀通告 / 击杀流 | `hud/messages.md`、`interfaces/kills_sse.md` | 20 s / 6 s、`[bot]`、玩家在一端才播、钱是弹窗不是消息；SSE 记录形状与口径 |
| 1201–1222 | 爆炸实体 / 模块血 | `rules/damage.md`、`rules/units.md` | 爆炸字段（friendly/skip/pid/链式延迟一 tick）、机甲 120/50/50 与 `MOD_TORSO` |
| 1223–1248 | 残骸 / 烟幕 / 车道 | `rules/units.md`、`rules/weapons.md`、`systems/sight.md`、`rules/map.md` | 5 s 尸体 / 10 s 残骸与 flags 位；14 罐 50 m、45 m 泡、5 s；`lanes`/`lanes_rev` 的口径 |
| 1249–1360 | 机甲五定律 / APC | `systems/movement.md` | 功率曲线与 1.5 g 上限、阻力由功率平衡解出、巡航与刹车、坡度表、停放不溜车；APC 自行车模型、侧向加速度上限、倒车、弯道 8 m 切削预算与到点半径 |
| 1361–1461 | 地形数据 / 树 / 视线 / 树丛 / 城镇 | `rules/map.md`、`systems/sight.md` | 1000²、16 m、4 m 高度步长、`roads`/`walls` 位图、DDA；树干柱体/树冠体两套解；920 处树丛；城镇生长与广场 80 m |
| 1462–1630 | 线格式 / 分层 / 时钟 / 拥塞 / 小地图 | `systems/net.md` | 行宽表 74/46/30/25/20 B、分层半径表与进出带、16 B 时钟块字段、`floor/slack/resume` 伪代码、半 tick 修正、小地图 6 B×3.75 Hz |
| 1631–1667 | 调试门与表 | `interfaces/debug.md` | 14 条消息的完整表 + 示例 + `respawn` 例外的理由 |
| 1670–2022 | 客户端 | `hud/*`、`systems/firecontrol.md`、`systems/audio.md`、`systems/net.md`、`docs/dev/client.md` | 空格驱动在机身系、两个十字、眼睛与炮口、吸附与 `V`、圈宽 2–8x、fx 行、音频四效、齿轮与 `ESC`、图标、粒子质量档 |
| 2202–2237 | 大厅 / `ENTER` / 靶场服务 | `hud/lobby.md`、`modes/range.md`、`AGENTS.md` §2 | `DEPLOY` 只触发一次、`ENTER` 不再 join；靶场的夹具、重生 3 s/5 s、$99999 |

### 4.2 今天两份文档都没写清的（新写）

1. **伤害链的时刻语义**：`projectiles`/`proximity` 写 hp、`blasts` 判爆、`mortality` 判死；
   一个系统读到的 hp 是它自己那一刻的；0 血车体在本 tick 仍在册。
2. **命中判定的粒度**：地面按 9 m 采样、建筑按整格柱体、树干按圆柱、单位按球；
   相对腿解算（目标已动完、导弹未动）与宽相盒的 `reach`。
3. **弹的结束记录**：每 tick 一条 `first_hit_record`、六种 `HIT_*`、t=0 的两类（到期/消失）。
4. **快照分层完整表**：半径、进出带 1.15、每类行宽、频率、实测 kB/s，以及"为什么这些半径是客户端常数"。
5. **拥塞判据**：`behind/floor/slack/held/resumed` 公式、`FLOOR_RISE` 与 2 s 封顶、
   RTT 半 tick 修正与 `PING/TPS/LOSS` 的读法。
6. **发射几何总表**：三类底盘的炮口、管阵 `elev`/`bolted`、弹种与落点。
7. **扳机门与主臂交接**：`atan((r+hit_r)/d)`、`PITCH_TOL`、`MASTER_HANDOFF`、为什么第二臂不一起开火。
8. **机组的信念**：`ai_tgt` 的写入条件、2 s 行走、扫描时钟只决定换目标。
9. **导弹防御**：`CPA_HIT` 40 m / `CPA_MEM` 100 m、只 fuse 臂开火、`ai_cover` 一个扳机写者。
10. **锁定与通道**：4 通道、40° 锥、8 s 记忆、重捕按信念点距离排序、候选黏性、信念点画法与变暗。
11. **视线一条门**：地形/树冠/烟幕/激光的判定球与遮挡，树干与树冠两种形状。
12. **运动学**：功率曲线表、g 上限、巡航带、刹车距离、坡度表、停放不溜车、脱困、APC 弯道预算。
13. **音频**：四重距离效应、远场声部与压缩、限幅链、声部预算与 `SOUND_FLOOR` 服务端过滤。
14. **操控总表**：所有按键（含 `ALT`/`CTRL+滚轮`/`V`/`B`/`H`/`ESC`/`ENTER`）、缩放档位、吸附开关。
15. **地图生成**：镜像与 `_orbit_key`、城镇生长（rungs/close/houses/plaza）、树丛 920、车道投影。
16. **调试消息表**：14 条 + 示例 + `MECH_DEBUG` 与 `MECH_LOG_LEVEL` 的区别。
17. **每页"核对"行**：feature bump 时同步更新，标明这页对过哪一版。

### 4.3 去重与纠错清单（执行时逐条勾）

1. 换装定律收敛到 `rules/economy.md` 一处，`flow`/`hud/panels`/`rules/weapons` 改成一句链接。
2. 雷达数字统一：以 `Core/Defs.cs` 为准（2729 与 2728 只能留一个），只住 `rules/units.md`，
   `systems/rwr.md` 引用。
3. 近炸/拦截/导弹作为目标收敛到 `rules/damage.md`；`systems/ai.md` 只写"机组怎么打"。
4. 锁定/通道/信念收敛到 `systems/seekers.md`；`hud/aim.md` 只讲画的是什么。
5. 死亡/重生/城镇收敛到 `rules/spawn.md` 与 `rules/map.md`。
6. 客户端火控收敛到 `systems/firecontrol.md`，`hud/aim.md` 只留画面与交互。
7. AI 行为收敛到 `systems/ai.md`，`rules/units.md` 只留数值与模板。
8. 调试表收敛到 `interfaces/debug.md`（今天有 3 份：AGENTS 1637–1667、`flow.md:342-363`、`range.md:171-203`）。
9. 烟幕收敛到 `systems/sight.md`（遮挡）+ `rules/weapons.md`（装备与参数）。
10. `README.md:4-5` 改成 `Core/Defs.cs`、`frontend/src/hud/`，并补全 8 页→27 页的索引。
11. `CHANGELOG.md` 的 `backend/*.py`、`frontend/src/hud.js` 在归档时加一行头注"路径为当时的路径"，
    新条目用新路径。
12. `screens.md:395` 的 2.16 排到 2.13 之后（拆分时自然消失，但要确认新页顺序）。
13. `range.md:77,104` 的 3.5/3.6 与 3.1–3.4 统一层级与顺序。
14. 删掉 `AGENTS.md:625-627`（Python 对象）、`1359-1360`（断句）、`1909-1915`（已删的 `bassLift`）、
    `653/758/2223`（不存在的 Python 工具）。
15. `AGENTS.md` 里 `docs/plans/AI_plan.md`、`docs/plans/speed_component.md` 两个死链：
    要么补文（从 AGENTS 的 AI/速度段落整理成计划），要么改成指向 `systems/ai.md`、`systems/movement.md`。
    建议后者。
16. `flow.md:122-162`（netcode）与 `screens.md:578-648`（音频）整段搬走，原页只留一句引用。

### 4.4 CHANGELOG 的形态

- 工作文件 `CHANGELOG.md` 只保留**当年当月**的条目，每条 ≤ 8 行，写玩家可见的差异 + 涉及页面；
  已落地的 2026-11 有 64 条、约 1700 行，随新格式的短条目逐步变短；
- 更早的按年月归档到 `docs/product/changelog/YYYY-MM.md`（已归档 `2026-10.md`，99 条），索引写在 `CHANGELOG.md` 末尾；
- 工程实测、被否决的第一版、shader/DSP 细节搬到对应 `systems/*` 页的"实测"或"取舍"节；
- 归档文件头写一行：`路径与数字是当时的，今日以正文页为准`。

---

## 5. 执行步骤

每步一个提交，可独立回滚；**搬完即删源**，不留两份活副本。

| # | 做什么 | 验收 |
| --- | --- | --- |
| C1 | 建目录骨架（`docs/product/{hud,rules,modes,systems,interfaces,changelog}`、`docs/dev/`），写新 `README.md` 索引与"改哪里"表；不动任何内容 | 树存在、索引覆盖全部目标页；旧页仍在 |
| C2 | 拆 `flow.md` + `screens.md` → `flow/controls/hud/*` | 旧两页删除；`grep -rl -e flow.md -e screens.md docs AGENTS.md` 只剩合理引用；每页头部有"读者/出处/核对" |
| C3 | 拆 `units.md` + `weapons.md` → `rules/{units,weapons,damage,economy}` | 旧两页删除；换装定律只剩一处；雷达数只剩一处 |
| C4 | 拆 `rwr.md` + `range.md` + `killed_sse.md` → `systems/{rwr,sight}`、`modes/range`、`interfaces/{kills_sse,debug}` | 旧三页删除；§4.3 的第 1–9、12–13 条勾完 |
| C5 | 重写 `AGENTS.md`：删功能、留原则、加功能索引；新建 `systems/{firecontrol,flight,seekers,movement,ai,audio,net}` 与 `docs/dev/{ecs,pipeline,client,checks,browser}`，把 §4.1 的表逐行落位 | `wc -l AGENTS.md ≤ 450`；`grep -c -e 实测 -e measured AGENTS.md` = 0；每个"细节"链接可达 |
| C6 | 去重收尾：§4.3 剩余条目、死链、`CHANGELOG` 归档 | §6 的机检全绿 |

C5 体量最大，允许按主题拆成 3 个提交（火控与弹 / AI 与机器 / 网络与声音），
但**每个提交结束时 AGENTS.md 与产品页都是一致的**（删掉的段落已在新页落位）。

约定：

- 每个提交只动文档，**不 bump 版本、不写 CHANGELOG 条目**；
- 发现文档与代码不符时，先改文档对齐代码；若代码确实有 bug，单开一条改动（bump + 条目），
  不在文档重构里顺手改；
- 旧页删除后靠 git 历史找回，不做备份副本。

---

## 6. 验收（可机检）

```bash
# 1. AGENTS.md 的体量与纯度
wc -l AGENTS.md                                      # ≤ 450
grep -nE '实测|measured' AGENTS.md | wc -l            # 0

# 2. 死引用（归档除外）
grep -rnE 'backend/|Systems/Spawner\.cs|frontend/src/hud\.js|run\.py|intercept_check\.py|AI_plan\.md|speed_component\.md' \
  AGENTS.md docs --include='*.md' | grep -v 'docs/product/changelog/'   # 空

# 3. 每页预算
find docs/product -name '*.md' -not -path '*/changelog/*' -exec wc -l {} + | sort -n

# 4. 相对链接可达（新写一个只读脚本 tools/check_links.mjs 或临时 node -e）
#    规则：Markdown 里出现的 *.(md) 相对路径必须存在

# 5. 一处事实一个家（抽查 12 个常量，命中页数必须为 1；归档与 CHANGELOG 不计）
for k in REARM_SWAP 2728 TIER_ROUND MASTER_HANDOFF BEAM_CHANNELS CPA_HIT \
         MECH_G_LIMIT RWR_RANGE LASER_SPHERE FAR_CEIL SNAP_EVERY RESPAWN_AT_FRONT; do
  printf '%-22s %s\n' "$k" \
    "$(grep -rl --exclude=CHANGELOG.md --exclude-dir=changelog -- "$k" docs/product | wc -l)"
done

# 6. 编号连续（每页的 ## 从 1 开始依次递增，无 2.16 排 2.13 前的情况）
grep -rn '^##' docs/product | head -50
```

人工走查（各 5 分钟）：

- 新玩家动线：`README → flow → controls → hud/hud` 能读懂一局怎么打；
- 调参动线：`rules/units → rules/weapons → rules/damage` 每个数值只有一处；
- 维护者动线：任选 `systems/*` 一页，只读这一页能判断"这段代码能不能改"；
- 反查：从代码里挑 10 个常量，每个都能在 3 跳内找到归属页。

---

## 7. 之后怎么保持

1. `docs/product/README.md` 的维护规则改成一张 **"改哪里"** 表：改武器/单位/伤害/经济/地图/
   出生/波次/HUD/操控/调试/击杀流 → 各去哪一页，一次改动可能命中多页时以 §1.3 的归属表为准。
2. `AGENTS.md` §1 写明唯一入口：**新功能只进产品页**；想往 AGENTS 加功能段落，评审时打回。
3. 每页头部 `核对` 行随 feature bump 更新；季度或大版本前跑一次 §6 的机检。
4. `CHANGELOG` 按 §4.4 的形态维护；工程实测沉淀到 `systems/*` 的"实测"节，标版本与命令。
5. `docs/plans/` 保持"完成即冻结"；活的设计讨论留在 `docs/roadmap.md`。

---

## 8. 不做的事

- 不改任何代码、数值、文案；不重写行文风格（保留现有的"为什么/实测"口吻，只是搬对地方）。
- 不把 `docs/plans/` 合并进产品文档：计划回答"当时为什么这样选"，产品页回答"现在是什么"。
- 不做表格自动生成：`Defs.cs` 仍是唯一事实来源，产品页是它的中文镜像，靠人核对 + 版本行。
- 不按版本给 `CHANGELOG` 分文件：只按年月归档，避免 163 个小文件。
