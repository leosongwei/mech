# MECH 产品文档

> **读者**：新玩家、调参的人、机制维护者、外部集成方
> **一句话**：玩家能看到的一切——规则、数值、界面、判定——的中文说明。
> **数值出处**：`Core/Defs.cs`（`WEAPONS` / `UNIT_DEFS` / 各常量）与各模式系统；
> 界面文案以 `frontend/src/` 为准。代码是唯一事实来源，本目录是它的中文镜像。
> **核对**：1.45.1 / 2026-11-26

## 1. 一句话产品

浏览器里的 16 km 见方、红蓝双方三路推进的机甲射击游戏：玩家驾驶一台分模块受损的
机甲，双臂各挂一把真实弹道的武器，沿三条大道推进、占领城镇获得前线出生点，
最终摧毁敌方指挥部。

## 2. 读者动线

| 你是谁 | 按这个顺序读 |
| --- | --- |
| 新玩家 / 外人 | [flow](flow.md) → [controls](controls.md) → [hud/hud](hud/hud.md) |
| 调参与平衡 | [rules/units](rules/units.md) → [rules/weapons](rules/weapons.md) → [rules/damage](rules/damage.md) → [rules/economy](rules/economy.md) → [modes/battle](modes/battle.md) |
| 机制维护者 | 对应的 `systems/*`：先读"规则"，再读"实现" |
| 外部集成 | [interfaces/kills_sse](interfaces/kills_sse.md) |
| 测试与量测 | [../dev/checks.md](../dev/checks.md) → [modes/range](modes/range.md) |

## 3. 页面索引

**游戏与界面**

| 页 | 内容 |
| --- | --- |
| [flow.md](flow.md) | 一局的生命周期：启动 → 大厅 → 对局循环 → 死亡 → 胜负 → 重开 |
| [controls.md](controls.md) | 按键、缩放、调零、瞄准吸附、自由视角 |
| [hud/lobby.md](hud/lobby.md) | 启动页与大厅：呼号、阵营、`DEPLOY` |
| [hud/hud.md](hud/hud.md) | 对局 HUD 各区块、告警盘与箭头的位置、配色速查 |
| [hud/aim.md](hud/aim.md) | 准星、预瞄圈、吸附、锁定标记、导引头圈 |
| [hud/messages.md](hud/messages.md) | 消息栈、击杀行、死亡/胜负屏、受击反馈 |
| [hud/panels.md](hud/panels.md) | 换装面板、整备进度、设置菜单、帮助覆盖层 |

**规则与数值**

| 页 | 内容 |
| --- | --- |
| [rules/units.md](rules/units.md) | 单位总表、模块、命中球、自卫雷达、AI 装备模板、残骸 |
| [rules/weapons.md](rules/weapons.md) | 武器全参数、管阵、弹药与换弹、烟幕投放 |
| [rules/damage.md](rules/damage.md) | 穿透/爆破/溅射/近炸、导弹作为目标、阵亡爆炸、命中判定顺序 |
| [rules/economy.md](rules/economy.md) | 收入、赏金、价格、换装工时 |
| [rules/map.md](rules/map.md) | 地形、道路、树、城镇、据点、车道、镜像 |
| [rules/spawn.md](rules/spawn.md) | 出生、三步选城、重生倒计时、首次部署 |

**模式**

| 页 | 内容 |
| --- | --- |
| [modes/battle.md](modes/battle.md) | 波次、增援、raider 任务规则、占领、胜负、重开 |
| [modes/range.md](modes/range.md) | 靶场模式：地图、靶子、碉堡、防御哨位、与战斗模式的差异 |

**机制**

| 页 | 内容 |
| --- | --- |
| [systems/firecontrol.md](systems/firecontrol.md) | 调零、发射几何、每臂各算、主臂、双端同解 |
| [systems/flight.md](systems/flight.md) | 弹道、气动、制导律、电机、电池 |
| [systems/seekers.md](systems/seekers.md) | 锁定、通道、数据链刷新、记忆、物理重捕、信念点 |
| [systems/rwr.md](systems/rwr.md) | 告警器：盘面、五层判据、推导、实测、实现、取舍 |
| [systems/movement.md](systems/movement.md) | 机甲五定律、APC 自行车模型、爬坡、脱困 |
| [systems/ai.md](systems/ai.md) | 机组、感知、数据链目标、接战、导弹防御、raider 规划、导航 |
| [systems/sight.md](systems/sight.md) | 物理视线、车辆光学观察与数据链：地形、树冠、烟幕、激光判定球 |
| [systems/audio.md](systems/audio.md) | 声音场、衰减、远场、总线、限幅、声部预算 |
| [systems/net.md](systems/net.md) | 帧格式、行宽、数据链过滤、分层、时钟、拥塞、RTT、小地图 |

**接口**

| 页 | 内容 |
| --- | --- |
| [interfaces/debug.md](interfaces/debug.md) | 调试消息表、浏览器控制台自动化 |
| [interfaces/kills_sse.md](interfaces/kills_sse.md) | 击杀流 `GET /events/kills` |

**变更**

| 页 | 内容 |
| --- | --- |
| [CHANGELOG.md](CHANGELOG.md) | 近期玩家可见变更（当年当月） |
| [changelog/](changelog/) | 按年月归档的旧条目 |

## 4. 术语

| 术语 | 含义 |
| --- | --- |
| 机甲 / MECH | 玩家与部分 AI 使用的双足机器，分 CHASSIS / TORSO / L.ARM / R.ARM 四个模块 |
| 模块 | 独立血条的机体部位，手臂被打掉即失去该臂武器 |
| 路 / LANE | 三条固定大道（北、西、中），AI 与波次沿路推进 |
| 前线 | 己方在该路上最靠前的连通城镇 |
| 城镇 | 可占领的据点，占领后成为前线出生点；正在争夺的那座不刷兵 |
| 波次 / WAVE | 指挥部定期刷出的增援计数（HUD 的 `WAVE n`） |
| 瞄具 / OPTICS | 多档倍率，改变视场角与地形网格精度 |
| 锁定 / LOCK | 导弹武器准星圈里有敌车即算锁定，见 [systems/seekers](systems/seekers.md) |

## 5. 维护规则（对人和 agent 都适用）

1. **改任何东西之前先读本目录**，确认改动落在既有产品规则内。
2. **任何玩家可见的改动都必须落页并登记**：新增/删除界面元素、文案、键位、
   单位或武器数值、出生与规则时间、胜负条件、调试按钮。纯内部重构不必登记。
3. 顺序：先改对应页 → 再在 [CHANGELOG.md](CHANGELOG.md) 顶部加一条（≤ 8 行，
   写玩家看得见的差异 + 涉及页面）→ 最后 bump `MechServer.csproj` 的 `<Version>`。
4. **一处事实只有一个家。** 数值、口径、公式、判定写在它归属的那一页，
   其它页只放一句"见某页"的指路，不复制数字。

**改哪里**

| 你要改 | 去哪一页 |
| --- | --- |
| 武器参数、弹药、管阵 | [rules/weapons](rules/weapons.md) |
| 单位血量/速度/体型/命中球/雷达 | [rules/units](rules/units.md) |
| 穿透、爆破、溅射、近炸、拦截、命中判定 | [rules/damage](rules/damage.md) |
| 价格、回收、换装工时、收入、赏金 | [rules/economy](rules/economy.md) |
| 地形、道路、树、城镇、车道、镜像 | [rules/map](rules/map.md) |
| 出生、选城、重生倒计时 | [rules/spawn](rules/spawn.md) |
| 波次、增援、占领、胜负、raider 任务 | [modes/battle](modes/battle.md) |
| 靶场的一切 | [modes/range](modes/range.md) |
| 调零、发射几何、火控解算 | [systems/firecontrol](systems/firecontrol.md) |
| 弹道、气动、制导、电机 | [systems/flight](systems/flight.md) |
| 锁定、通道、记忆、信念点 | [systems/seekers](systems/seekers.md) |
| 告警器判据与阈值 | [systems/rwr](systems/rwr.md) |
| 机甲/APC 的运动与爬坡 | [systems/movement](systems/movement.md) |
| 机组感知、接战、导航 | [systems/ai](systems/ai.md) |
| 视线、树冠、烟幕遮挡 | [systems/sight](systems/sight.md) |
| 声音 | [systems/audio](systems/audio.md) |
| 帧格式、分层、时钟、拥塞 | [systems/net](systems/net.md) |
| HUD 尺寸、文案、配色、动效 | [hud/](hud/) |
| 按键与操作 | [controls](controls.md) |
| 调试消息 | [interfaces/debug](interfaces/debug.md) |
| 击杀流格式 | [interfaces/kills_sse](interfaces/kills_sse.md) |

文档用中文；界面上的英文原文（如 `MECH DESTROYED`、`REARM / REPAIR`）照抄不译。
工程侧的手册（怎么跑、怎么量）在 [../dev/](../dev/)。
