# AGENTS.md

本文件是参与 **MECH** 开发的编码智能体的工作笔记。MECH 是一款浏览器端机甲射击游戏：
C#（.NET 10）原型（archetype）ECS 服务器、WebGL2 客户端、16 km 见方的 tile 地图、
三条兵线、RED 对抗 BLUE、30 Hz 时钟。面向玩家的规则位于 `docs/product/`，
工程手册位于 `docs/dev/`。本文件是开发契约：原则、开发流程、各模块的职责归属。

## 1. 文档分层与一次改动的顺序

四个层次，各司其职：

| 层次 | 位置 | 回答的问题 | 缺陷表现 |
| --- | --- | --- | --- |
| 工作笔记 | `AGENTS.md`（本文件） | 代码应如何编写，如何构建/运行/验证，各模块的职责归属 | 出现玩家可见数值，或以文字叙述具体机制 |
| 产品文档 | `docs/product/` | 规则、数值、界面、判定 | 一页同时服务两类读者 |
| 工程手册 | `docs/dev/` | 如何构建、运行、度量；客户端约定 | 内容超出"仅开发期阅读"的范围 |
| 计划 | `docs/plans/` | 当初为何如此设计 | 工作落地后仍被编辑 |

**改动任何内容之前，先阅读 `docs/product/`**（索引：`docs/product/README.md`），
确认改动符合已成文的规则。

一次面向产品的改动，按以下顺序完成，全部纳入同一个提交：

1. 代码；
2. 该事实所属的产品页面（`rules/`、`systems/`、`hud/`、`modes/`）；
3. 在 `docs/product/CHANGELOG.md` 顶部添加带日期的条目——八行以内，
   写明玩家可见的差异及涉及的页面；
4. `MechServer.csproj` 中的版本号：新功能递增第二位（第三位归 0），
   玩家不可见的改动递增第三位。`frontend/package.json` 的 `version`
   是私有清单字段，任何流程均不读取。

维持四层文档诚实的规则：

- **一个事实，一个归属。** 数值、公式或判定只写在拥有它的页面上，
  其余页面一律放链接。归属表（"改哪里"）位于 `docs/product/README.md`。
- **新机制写入 `docs/product/`。** 在本文件中添加功能性段落即评审失败；
  本文件只容纳下文的原则。
- **内部重构不做任何登记**——不建页面、不写 CHANGELOG 条目；仅改动文档
  或注释亦不递增版本号。
- **代码使用英文，文档使用中文。** 源码、注释、提交信息、日志行以及玩家
  读到的所有字符串均为英文。`docs/` 下的一切均为中文，UI 字符串与标识符
  按原文引用。该规则约束的是文件内容的语言，与文件名无关；本文件作为
  智能体工作笔记，同样使用中文。
- **注释与文档使用肯定式表述。** 描述代码是什么、做什么。必须成立的规则
  以正面陈述给出；不存在的事物不值得占用一句篇幅。
- **单位为米与秒。** 地图为 16 km 见方，tick 为 1/30 s。
- **最小编辑，范围收敛。** 调参数值统一放在 `Core/Defs.cs` 与各模式系统的
  顶部，禁止内联在代码路径中。缺陷修复只包含修复本身，附带对周边代码的
  清理即超出范围。

## 2. 开发流程

### 构建

```bash
cd frontend && npm run build     # -> assets/（依赖检查 + 语法检查 + webpack）
./dotnet.sh build                # 服务器；必须干净构建，零警告
./dev.sh build                   # 使用共享 npm 缓存的客户端构建
```

服务器一律通过 `./dotnet.sh` 构建：该脚本将 CLI home、NuGet 缓存、
`XDG_RUNTIME_DIR` 与 `TMPDIR` 全部指向 `.dotnet_tmp/`（已 gitignore），
因为裸 `dotnet build` 会写入 `$HOME`，在只读沙箱中直接失败。
`Nullable` 已开启：警告即缺陷。

前端改动只需重新构建——服务器按请求读取 `assets/`，因此无需重启。
后端改动需要重启（`./dev.sh stop && ./dev.sh start`）。

### 运行

```bash
./dev.sh dev      # 构建客户端后启动服务器（首次运行）
./dev.sh start    # 在会话 "mech" 中启动服务器（byobu 或纯 tmux）
./dev.sh attach   # 接入会话（脱离：F6 / Ctrl-a d）
./dev.sh stop     # 终止 battle 与 range 两个会话
./dev.sh status
./dev.sh range    # 在会话 "mech-range" 中启动武器靶场服务器
tail -f /tmp/mech-server.log
```

<http://127.0.0.1:8010/> 为战斗服务器，<http://127.0.0.1:8011/> 为靶场。
玩家在大厅中选定呼号与阵营后点击 `DEPLOY`。

**禁止以 `nohup ... &` 或任何其他脱离式后台任务启动服务器**：工具调用会
阻塞在未关闭的管道上，且进程随派生它的 shell 一同退出。服务器必须在
tmux 会话中启动，通过 `./dev.sh status`、`curl` 与日志观察世界状态。

沙箱化的智能体 shell 中，`/tmp` 是每会话独立的全新目录，tmux 的 socket
目录存在之前无法创建 socket：

```bash
mkdir -p /tmp/tmux-$(id -u) && chmod 700 /tmp/tmux-$(id -u)
```

### 验证

无头验证框架位于 `Harness/`，通过 `./dotnet.sh run -- --check ...` 驱动。
它在进程内运行真实的 `World`——无 socket、无墙钟——因此一项检查产出的
是数值，而是一次会话。`MECH_SEED` 使单线程运行可复现；读数只用于判断
量级（本次改动是否使情况恶化），禁止将其作为基线做差分。每项检查以
`ecs.validate()` 收尾。

| 命令 | 检验内容 |
| --- | --- |
| `--check battle --minutes 12` | 一整场脚本化战斗：实体普查、每 tick 开销 |
| `--check range --minutes 2` | 靶场世界，含一名开火中的玩家 |
| `--check hit` | 全部武器对一辆停放的 APC |
| `--check intercept` | 全部导弹对防御哨站 |
| `--check beam` | 驾束弹：命中、掩体、被切断的激光 |
| `--check fcs` | AI 乘员与玩家在相同场景下的对照，另含 chassis/pull/smoke/arsenal 场景 |
| `--check drive` | 机甲推力模型与其自身代数解的对照 |
| `--check terrain` | 面片坡度、兵线可通行性、采样开销 |
| `--check nav` | 导航网格：区域、剖面、长距离规划、兵线 |
| `--check stuck` | 战斗是否持续推进、是否有实体受困 |
| `--check diag --minutes 2` | 若干 AI 机体的行为诊断 |
| `--check net --minutes 30 --from 8` | 一名玩家的线路流量，按分钟统计 |
| `--check tiers --minutes 14 --from 8` | 备选快照档位的开销 |
| `MECH_PERF=1 --check battle` | 同一场战斗，去除事件扫描，按分钟统计开销 |

命令、验收形态与当前读数：`docs/dev/checks.md`。
单元测试为 `./run_tests.sh [cs|js|compare] [--filter NAME]`——无服务器、
无 socket；`tests/cases/fcs.json` 与 `height.json` 是双端共同应答的契约。

**机制的度量在靶场进行，而非战斗。** 一场战斗是二十余个 AI 乘员在同一
世界中开火、阵亡、行驶，因此在战斗中读到的某个水平值、节点数或命中数，
主要度量的是玩家恰好所处的位置。靶场的固定靶标从不开火；`./dev.sh range`
加控制台消息即是试验台，数值正确之后，战斗是改动被*看见*的地方。

### 日志

`MECH_LOG_LEVEL` 设定日志详细程度（默认 `INFO`，按需 `DEBUG`）。
`MECH_DEBUG` 是另一回事：它是开发消息及设置菜单 DEBUG 区块的 websocket
门控（`dev.sh` 始终设置它）。按每发弹体、每次命中、每次锁定或每 tick
触发的日志行属于 DEBUG；INFO 留给每会话一次的事件。

### 浏览器内

`window.__game` 是控制台入口（`g.net.*`、`g.input`、`g.renderer`、
`g.audio`、`g.map.*`、`g.freeCam`）。无头自动化中 pointer lock 会被拒绝——
直接设置 `g.input.*`，取代合成鼠标移动。操作配方：`docs/dev/browser.md`。

## 3. 模块地图

```
dev.sh  dotnet.sh  run_tests.sh  MechServer.csproj（<Version>）  Program.cs（--check）
Ecs/        Ecs.cs（原型存储）      Components.cs（全部组件）
Core/       Defs.cs（类型、武器、常量）   World.cs（玩家、tick、生成）
            Terrain.cs（高度场、道路、墙体、城镇）   Nav.cs  Grid.cs  Rng.cs
            FireControl.cs  Flight.cs  Seeker.cs  Ballistics.cs   Combat.cs  Shop.cs
            SphereTable.cs  Profiler.cs  MathUtil.cs
Net/        Netcode.cs（帧、快照、meta）   Server.cs（websocket + 主循环）
            KillStream.cs（独立线路上的击杀播报）
Systems/    每系统一个文件，`run(world, dt)`
  Rearm PlayerInput Gunnery Locking Smoke Weapons Unstick
  Movement/{Foot,Vehicle,Mech/{Steer,Thrust,Slope,Drag,Ground},Support/{Drive,Integrate}}
  Sweep Beams Flights FirstHit Impact Blasts Mortality Economy Debris Sight Smoke
  AI/{Sense,Threats,Intercept,Engage*,Wander}  AI/Strategies/{Front,Raid}
  AI/Support/{Tags,Crew,Policy,Engage,ThreatTable,Pipeline}  AI/Nav/{NavMesh,NavWalk,NavRegion,NavQuery,Paths}
  Support/KillFeed.cs          # 玩家读到的一行——谁以何种武器从多远击杀了谁
Modes/      Mode.cs  Modes.cs  Battle/  Range/
Harness/    Check BeamCheck InterceptCheck FcsCheck NetCheck Tiers StuckCheck DriveCheck
            TerrainCheck NavCheck
tests/      cases/*.json  frontend/（node:test）  backend/（控制台运行器）  compare.mjs
frontend/   src/{core,net,map,audio,render,scene,hud,fx,input,app}（四层，单向依赖）
assets/     编译后的客户端（已 gitignore）
tools/      gun_report.py  saclos_motor.py  smoke_lock_test.js
```

服务器的 tick，位于 `Core/World.cs`：

```
rearm, player_input,            # 购买的作业倒计时；输入转化为意图
mode pre-systems                # battle：AI 乘员
gunnery, locking, smoke, weapons,   # 机器随意图转动，随后开火
unstick, movement,                  # 受困机体先被移离，随后各驾驶员驱动
sweep.spheres, beams, expire,       # 本 tick 的命中球、激光、电池到期
flight_ballistic/rocket/saclos/top, # 每种弹体一个飞行系统
first_hit_ground/structures/units/fuse,   # 各弹道段遇到的对象（仅记录）
impact,                             # 该记录的唯一读者；在此终结弹体
blasts, mortality, economy, debris, # 伤害链，按此顺序
mode post-systems               # battle：波次
```

客户端为四层单向依赖，由 `frontend/scripts/depgraph.mjs` 检查：
`core -> net/map/audio -> render/scene/hud/fx/input -> app`。
`render/passes.js` 定义绘制顺序，`app/frame.js` 的 `STAGES` 定义帧内顺序。
约定及其背后的陷阱：`docs/dev/client.md`。

### 状态归属

| 状态 | 写入者 | 读取语义 |
| --- | --- | --- |
| `pos`、`vel` | 底盘的驾驶员（`Movement/`）、弹体的飞行系统、烟雾罐 | 世界的唯一真值；所有碰撞、导引头与快照均读取它 |
| `vel_cmd` | `player_input`（玩家的操纵杆）或驾驶员的 `nav_* -> vel_cmd` 步骤 | 机器的意图；它与 `vel` 的差值*即*机器本身的响应 |
| `nav_*` | AI 乘员（路线、路点、速度） | 驾驶员的输入；乘员对运动唯一可写的东西 |
| `sight_yaw`/`sight_pitch` | `player_input`、乘员的 `Engage`、模式 | 眼睛看向何处；即时响应 |
| `yaw`、`pitch` | `Gunnery` | 已瞄准的火炮；车体与炮臂随视线转动 |
| `hp` | `Combat.apply`（及 `Shop` 的维修） | 任何系统均可读，但以其所在 tick 阶段为准 |
| `first_hit_record` | 各飞行系统（重新置位）与各碰撞系统（`if t < first_hit_t`） | 仅 `Impact` 读取 |
| `ai_tgt` | `AI/Sense` | 乘员的信念目标；乘员火炮一律瞄准它，而非 `pos` |
| snapshot、meta | `Netcode` | 客户端；客户端只做插值，从不做模拟 |

## 4. 原则

每条原则只保留断言本身；具体机制、数值与实现细节由"详见"指向的页面
及第 5 节功能索引承载。

**P1. 组件集合即实体的类型，且终生不变。** 详见 `docs/dev/ecs.md`。

**P2. 每个运动体只有一份 `pos`、一份 `vel`。** 详见 `docs/product/systems/movement.md`。

**P3. 身份是组件，而非类型分支。** 详见 `docs/dev/ecs.md`。

**P4. 系统面向 buffer 编写，而非经由 `View`。** 详见 `docs/dev/ecs.md`。

**P5. 系统遍历实体的顺序不属于模拟的一部分。** 详见 `docs/dev/checks.md`。

**P6. 流水线即语义，其余只是开销。** 详见 `docs/dev/pipeline.md`。

**P7. 视线只有一条。** 详见 `docs/product/systems/sight.md`。

**P8. 伤害分为两段，且本身并非系统。** 详见 `docs/product/rules/damage.md`。

**P9. 弹体的终结只有一份记录、一个裁决者。** 详见 `docs/product/rules/damage.md`。

**P10. 弹体的种类是一个 tag。** 详见 `docs/product/systems/flight.md`。

**P11. AI 表达意图，运动属于机器。** 详见 `docs/product/systems/ai.md`。

**P12. 火控定律唯一，两种装定，双端一致。** 详见 `docs/product/systems/firecontrol.md`。

**P13. 文档随改动更新。** 详见 `docs/product/README.md`。

**P14. 小编辑，先靶场后战斗。** 详见 `docs/dev/checks.md`。

## 5. 功能索引

| 主题 | 阅读 |
| --- | --- |
| 一局比赛如何开始、进行、结束 | `docs/product/flow.md` |
| 按键、缩放、归零、瞄准辅助 | `docs/product/controls.md` |
| HUD 元素、颜色、消息行 | `docs/product/hud/` |
| 单位属性、模块、命中球、雷达 | `docs/product/rules/units.md` |
| 武器数值、弹架、弹药 | `docs/product/rules/weapons.md` |
| 穿透、爆炸、引信、死亡、拦截 | `docs/product/rules/damage.md` |
| 价格、收入、补给时间 | `docs/product/rules/economy.md` |
| 地形、道路、树木、城镇、兵线、地图镜像 | `docs/product/rules/map.md` |
| 出生、三步选镇、重生等待 | `docs/product/rules/spawn.md` |
| 波次、增援、突袭队、占领、胜利 | `docs/product/modes/battle.md` |
| 靶场及其固定靶标 | `docs/product/modes/range.md` |
| 瞄准、发射器、总保险、扳机门控 | `docs/product/systems/firecontrol.md` |
| 弹道、气动、制导、发动机、电池 | `docs/product/systems/flight.md` |
| 锁定、通道、记忆、重新捕获、信念点 | `docs/product/systems/seekers.md` |
| 告警接收机 | `docs/product/systems/rwr.md` |
| 机甲五定律、自行车模型、坡度、脱困 | `docs/product/systems/movement.md` |
| 乘员：感知、交战、导弹防御、突袭规划、导航 | `docs/product/systems/ai.md` |
| 遮蔽关系：地形、树冠、烟雾、激光球 | `docs/product/systems/sight.md` |
| 声音：距离、远场底噪、限制器、声道预算 | `docs/product/systems/audio.md` |
| 帧、行宽、档位、时钟、拥塞、RTT、小地图 | `docs/product/systems/net.md` |
| 控制台消息与自动化 | `docs/product/interfaces/debug.md` |
| 击杀播报的独立线路 | `docs/product/interfaces/kills_sse.md` |
| 存储、三个入口、不变量 | `docs/dev/ecs.md` |
| 何时运行什么，为何在该处 | `docs/dev/pipeline.md` |
| 渲染 pass、局部坐标系、图标、画质 | `docs/dev/client.md` |
| 如何构建、运行与度量 | `docs/dev/checks.md` |
| 从浏览器驱动游戏 | `docs/dev/browser.md` |