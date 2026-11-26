# 数据链：共享观察、目标框与制导锁定

日期:2026-11-25
状态:已实施（2026-11-25，1.45.0）
范围:车辆观察状态、阵营级数据链、快照与小地图过滤、AI 目击轮盘、玩家锁定、SACLOS 轨迹、目标框 UI。
版本:玩家可见的新功能 → **1.45.0**，并添加 `docs/product/CHANGELOG.md` 条目。
修订:**1.45.1**（2026-11-26）将观察改为 4 km、±40° 前锥，并为 AI 轮盘加 4 km 上限；目标框取消视野占比门槛。
前因:[AI_alarm_wheel.md](AI_alarm_wheel.md)（1.44.0 的轮盘）与
[AI_sight_plan.md](AI_sight_plan.md)（机组视线）。

一句话目标:

> 车辆把直接观察到的装甲与制导弹共享给同阵营；数据链接触对 AI、玩家火控和锁定是一种
> 可见性来源，但对激光传播、红外导引头和告警器不是。HUD 用颜色区分敌我，用实线/虚线区分
> 直接观察与数据链观察。

---

## 0. 已核对的现状

当前仓库没有数据链或接触状态。`docs/roadmap.md` 的数据链章节仍是后续功能清单。

| 事实 | 代码 |
| --- | --- |
| `Sight.clear` 只判断烟幕、地形、建筑和树冠，不判断阵营知识 | `Systems/Sight.cs:34-43` |
| 玩家 SACLOS / 攻顶候选在准星圈内获取时才要求物理视线 | `Systems/Locking.cs:173-197` |
| 候选一旦取得，离开准星或视线断开后仍保持；开火不再查视线 | `Systems/Locking.cs:150-165,208-218` |
| AI 当前目标每 tick 查物理视线，新目标扫描最多对 6 个候选查视线 | `Systems/AI/Sense.cs:89-121,179-184,195-243` |
| 目击轮盘只存方位、余温与权重，不存目标 ID；`Sense` 是唯一写者 | `Systems/AI/Support/Wheel.cs`、`Systems/AI/Sense.cs:160-167` |
| AI 火控只读 `ai_tgt`，不再查视线 | `Systems/AI/Support/Engage.cs:73-130` |
| SACLOS 目前把“物理可见”同时用于轨迹刷新和 `beam_lit` | `Systems/Beams.cs:139-180` |
| 红外导引头保持与重捕都要求从弹体出发的物理视线 | `Core/Seeker.cs:57-134,166` |
| 快照按玩家距离分层；小地图 `contacts` 发送全场每一个单位 | `Net/Netcode.cs:441-513,516-556`、`Net/Server.cs:418-450` |
| 客户端预瞄圈与信念点都只认物理视线 | `frontend/src/app/systems/lead.js:99-145`、`belief.js:68-96` |
| 车辆框、预判圈、友军导弹圈均由 Canvas HUD 绘制 | `frontend/src/hud/leads.js` |

### 0.1 本次修正的调查结论

* **可见性分两层。** 知识可见性 = 直接物理视线 OR 新鲜同阵营数据链；物理传播仍只由
  `Sight.clear` 回答。不能把数据链写进 `Sight.clear`。
* **轮盘不保存目标 ID。** 数据链接触作为低权重的目击证据写入现有 `tgt` 方位轮盘。
* **火控定律不改。** 数据链进入 `ai_tgt` / 玩家信念后，现有 `FireControl.solve` 与
  `Engage` 自动按可见目标工作。
* **保持与开火不新增物理门。** 现有锁定保持和发射许可允许视线断开后继续使用信念；本次只
  扩大“获取”和“刷新”的可见性来源。
* **数据链不是全知导航。** `Raid._gradient` 读取真实敌军位置的路线规划不属于“目标轮盘”，
  本次不改；roadmap 中的“raiders 按数据链规划路线”仍是独立后续项。

---

## 1. 规则与范围

### 1.1 观察资格

* 观察者：存活的 MECH 与 APC，包括玩家机体、AI 机体和脚本傀儡；不要求 `ai_crew`。
* 被观察目标：敌对 MECH、APC，以及 SACLOS / 攻顶制导弹。制导弹的观察只用于 HUD 来源标记，
  不进入地面目标选择。
* 步兵、平民、残骸、指挥部、碉堡：不观察，也不作为数据链接触上报。
* 观察半径统一为 4 km；先做距离与 ±40° 前锥门，再调用 `Sight.clear`。
* 玩家机体看 `sight_yaw`，AI 机组看 `gaze_yaw`，APC 看 `turret_yaw`，脚本傀儡看
  `sight_yaw`。观察独立于屏幕实际 FOV 与倍率；AI 轮盘检查另有 4 km 硬上限。
* 友军车辆的位置直接由同阵营快照 / 小地图提供，不需要先被另一台车观察到。

### 1.2 有效性与失效

* 红、蓝各自维护一份当前观察状态；状态写在目标实体的组件上，不建立裸 `eid` 字典。
* 每个数据链 tick 先清除当前可见位，再由本 tick 的观察者重新建立。任意同阵营车辆仍能
  直接观察，接触就有效；所有观察者都失去视线，接触当 tick 失效。
* `link_seen_tick[faction]` 保留最后一次成功观察的 `world.tick`，用于诊断和验证；可见性只由
  当前 `link_seen` 决定，不额外增加 TTL 或滞后。
* 多名观察者取并集；一名观察者丢视野不会清除另一名观察者刚建立的接触。
* 死亡实体被销毁，状态随实体消失；重生车辆使用组件默认值。ECS ID 复用不继承旧状态。
* 暂停世界不推进 `world.tick`，也不伪造新的观察。

### 1.3 可见但不万能

数据链只扩大“是否知道这个目标”，不改变以下门控：

* 玩家准星圈、20 m 下限、武器锁定距离；
* AI 的 `sight` / `ai_range`、目标类型白名单、观瞄窗口和 `ThreatScore.SWITCH`；
* 激光的 40° 窗口、激光是否真实照到、发射机到 SACLOS 弹体的物理通路；
* 红外导引头从弹体出发的保持与重捕视线；
* RWR / 激光告警的雷达或物理判据。

数据链目标对 AI 是可见目标，但威胁权重低于直接目标。初始取
`Sense.DATALINK_THREAT = 0.5f`，只乘数据链接触，不修改 `ThreatScore.W`。

### 1.4 网络范围

* 敌方 MECH / APC 只有在接收方阵营的数据链有效时才进入单位快照和小地图。
* 自身、友军、两座指挥部保持现有发送规则；现有距离 tier 不扩大。
* 数据链过滤先于 tier 死区，未知名词不进入 `SnapshotView.keep`，不会多留一帧。
* 小地图按阵营最多构建两份 contacts；同阵营玩家共用一份字节。
* 小地图 contacts 不发送步兵、平民或尸体，满足“步兵不上数据链”。普通世界快照中的步兵
  保持现有规则，本次不把整个游戏改成战争迷雾。
* 导弹快照行不按数据链过滤；行内标记接收方是否仅通过数据链知道这枚弹。

### 1.5 不做的事

* 不做无线电距离、中继、带宽或丢包模型；同阵营共享是全局的。
* 不做数据链历史轨迹、目标列表选择或屏幕外锁定 UI；玩家仍须把准星圈压到目标上。
* 不改变弹道、伤害、火控公式、锁定通道数量或 RWR 判据。
* 不把 `meta` 的射击、爆炸、激光精确位置一并改造成战争迷雾；这些是后续审计项。

---

## 2. 服务端结构

### 2.1 组件与实体类型

在 `Ecs/Components.cs` 注册：

| 组件 | 类型 / stride | 默认 | 含义 |
| --- | --- | --- | --- |
| `link_seen` | `U8` × 2 | 0 | 红、蓝当前是否有车辆直接观察到该实体 |
| `link_seen_tick` | `I` × 2 | -1 | 红、蓝最后一次直接观察的 tick |

在 MECH / APC 的类型标签中增加 `datalink`（`Core/Defs.cs`），新增
`Components.DATALINK_COMPS`。`World.spawn_unit` 参照 `BEAM_COMPS` 的挂载方式，仅对带
`datalink` 标签的单位附加这两个组件。步兵与静态设施不拥有该组件。

制导弹使用 `Systems/Datalink.cs` 内的两个每 tick 清空的 `HashSet<int>`，按阵营记录本 tick
被车辆直接观察到的 guided projectile；它们不需要进入持久 ECS 组件池。

### 2.2 新系统与统一查询

新增 `Systems/Datalink.cs`：

* `run(World, dt)`：唯一写者。清空当前状态与导弹集合，遍历车辆观察者和网格候选，建立接触。
* `known(World, faction, target)`：读取车辆当前数据链状态；缺少组件返回 false。
* `missile_known(World, faction, projectile)`：读取本 tick 的导弹观察集合。
* 使用 `Grid.box_into` 复用缓冲；车辆候选先做半径、存活、敌对和 `datalink` 判断，再做 LOS。
* 导弹不在单位网格中，单独遍历 `world.projectiles()`，只保留 `proj_kind` 2 / 3。

新增 `Systems/Visibility.cs`，提供：

```csharp
Visibility.source(World world, int observer, int target, Vec3 point)
// Direct / Datalink / None
```

先查 `Sight.clear(observerEye, point)`，再查 `Datalink.known(world, observerFaction, target)`。
调用者保留自己的距离、类型、窗口与武器门控。`Visibility` 不写状态，也不参与物理碰撞。

### 2.3 tick 位置

```text
PlayerInput
Datalink.run                 // 本 tick 的共享观察；玩家机体也参与
mode.pre_systems
  Sense.link_wheel           // Watch 之前写低权重数据链方位
  Watch.run / Watch.turn
  Sense.run                  // 直接 OR 数据链，消费轮盘转向
Gunnery / Locking / Weapons
Movement / Beams / Flights
...
broadcast                    // 读取本 tick 最终状态
```

`Datalink.run` 使用 tick 起始网格和移动前姿态，与现有 `Sense` 相同。新烟幕在 `Smoke.run`
之后才影响下一 tick 的感知，保持现有流水线语义。

### 2.4 玩家锁定与火控

* `Locking._candidate` 的唯一 `Sight.clear` 改为 `Visibility.source(...) != None`；准星圈、
  距离和 `lockable` 判断不变。
* `Locking._hold` 与 `Locking.pick` 不增加数据链或物理视线门。链路失效后仍按现有保持与
  信念记忆规则工作。
* 客户端 `Lead` 与 `Beliefs` 接受数据链行：直接视线通时刷真身，物理视线断但链路有效时也刷
  真身；两者都失效才走记忆外推。
* 数据链失效导致实体行消失时，`Beliefs` 对服务器仍在跟踪的 ID 保留并继续走信念，不立即
  删除；服务器释放锁定后再删除。这样玩家仍能发送 `lb`，不会悄悄丢失记忆。
* 攻顶弹保持原有“射手提供信念点”的机制。`Weapons.fire` 按直接观察、数据链观察、记忆估计
  的顺序取得当前最佳信念点，三种来源使用同一套 `tgt_mem / tgt_vel` 种子和出膛逻辑。
* 直接观察提供当前真身；数据链提供共享轨迹；两者都没有时使用已有记忆估计。没有可用射手
  信念且目标也不可见时不开火，不能把不可见目标的真实位置偷偷当成估计。`World.spawn_round`
  的真身回退只用于当前直接可见或数据链可见的目标。

### 2.5 AI 目标轮盘与选择

* 在 `Sense` 增加 `link_wheel(World)`，位于 `Watch.run` 之前。它只把**当前有效、仅数据链可见**
  的敌方接触写入 `Wheel.tgt`，权重为
  `Wheel.TGT_W × ThreatScore × DATALINK_THREAT`。
* 轮盘仍只存方位；`Sense` 仍是 `ai_tgt` 的唯一写者。`Watch` 无需保存目标 ID。
* `Sense._sees` 改为统一可见性，保留 `ai_range` 和 60% 车高探测点。
* `Sense._acquire` 保留网格盒、目标类型、`sight` 和观瞄窗口；物理可见候选权重不变，数据链
  候选权重乘 `DATALINK_THREAT`，通过统一可见性判断后进入候选。
* `_worth` 对当前新鲜数据链目标使用同一折扣，避免低优先级数据链目标在保持阶段反而获得
  更强滞回。已经失效、正在走 `Crew.MEMORY` 的目标保持原有比较规则。
* 直接目标与数据链目标同方向时，`Wheel.mark` 的强证据覆盖弱证据规则继续生效。
* 链路有效期间 `ai_tgt.age == 0` 并持续刷新；链路失效后才走 `Crew.MEMORY`。

### 2.6 SACLOS 与攻顶

SACLOS 只扩展“可见”的来源，不增加数据链制导分支。`Systems/Beams.cs` 中原有的 `visible`
直接改为“在 `BEAM_CONE` 内，并且直接视线或同阵营数据链可见”：

```text
visible = 在 BEAM_CONE 内 && Visibility.source(...) != None
```

* `visible` 不区分直接来源与数据链来源：两者都调用 `_sample`，刷新
  `beam_mem / beam_vel / beam_acc`，行为完全一致。
* 数据链可见时，机体按最佳努力原则把驾束激光指向数据链轨迹；目标是否被烟幕或地形挡住，
  不改变发射机的跟踪与 `beam_ok`。
* `visible` 为假时，完全回到原有不可见逻辑：`beam_seen` 增长，在 `BEAM_MEMORY` 内按最后
  的速度与加速度外推信念点。通道释放和 40° 窗口规则不变。
* `beam_lit` 表示激光是否**物理照射到目标车体**，仍由 `Sight.clear && body_hit(...)` 决定。
  它只服务于照射状态与激光告警，不参与 SACLOS 制导；因此数据链可见不会让告警器穿烟告警。
* SACLOS 弹体仍必须真实收到发射机的激光（`Systems/Flights.cs::Saclos` 的
  `Sight.clear(origin, p0)`）；导弹不查询目标数据链，发射机到导弹的物理通路不变。
* 攻顶弹保持原有出膛逻辑：机体按最佳努力提供射手信念点，直接观察优先，其次数据链轨迹，
  都没有时使用记忆估计。种子仍是同一套 `tgt_mem / tgt_vel`；出膛后
  `Core/Seeker.track / acquire` 继续只查弹体物理视线，数据链不提供穿山重捕。

这里的区分只有“目标跟踪可见”与“激光实际照到目标”：前者直接 / 数据链等价，后者始终是
物理量。SACLOS 与攻顶的制导条件都没有因数据链增加任何新分支。

---

## 3. 网络与客户端

### 3.1 能力声明与行格式

不增加任何行宽。init JSON 增加可选字段：

```json
"datalink": 1
```

* MECH / APC 的 `flags` bit6：`TARGET_LINK`，表示该行可由接收方通过数据链获得。友军车辆
  与已过滤的敌方接触置位；按接收者写入。bit7 继续保留。
* 弹丸行 `+5`  bit0：`TARGET_LINK`；敌方制导弹按本 tick 观察集合写入，友军制导弹始终已知。
  `+6..+7` 保持零。
* 旧客户端忽略未知 JSON 字段、flags 高位和弹丸填充字节；新客户端在缺少能力声明时把
  `TARGET_LINK` 视为 false，保持旧的仅直接目标行为。
* `ROW_MECH=76`、`ROW_VEHICLE=48`、`PROJ_STRIDE=32` 与总帧长计算不变。

### 3.2 快照与小地图

* `SnapshotView` 保存 `faction`，在 `at(World, Player)` 中设置。
* `snapshot_bytes` 第一遍在 `inside` 之前过滤敌方 MECH / APC：`Datalink.known` 为假就不入桶，
  也不进入 `keep`。
* `contacts_bytes(World, faction)` 使用同一过滤，并排除 `person`、平民和尸体。
* `Server.broadcast` 在每个 contacts tick 构建红、蓝两份缓存，按连接阵营选择；不再只构建
  一份全场表。
* Harness 的 `SnapshotView.all` 继续作为全量定价基线，不用于正式连接；测试需分别覆盖
  `all` 与按阵营过滤视图。

### 3.3 目标框数据模型

把 `frontend/src/app/systems/lead.js` 拆成两层：

1. **目标层**：车辆与制导弹的目标记录，不依赖玩家是否装备弹道武器。
2. **预瞄层**：只对敌方目标和可用弹道手臂生成 `marks`、连线与吸附候选。

每条记录增加：

```js
{ kind: 'vehicle' | 'missile', faction, source: 'direct' | 'datalink', marks: [] }
```

* 自身 / 友军车辆：进入目标层，蓝色；友军 `marks` 始终为空。
* 敌方车辆：进入目标层，绿色；直接视线通为 `direct`，否则行内有 `TARGET_LINK` 时为
  `datalink`。无链路标记且声明支持数据链时不绘制。
* 友军制导弹：蓝色圆圈，保持现有“所有己方 guided projectile”范围。
* 敌军制导弹：从快照弹丸行取，不继续只依赖 `rwr.threats`；有直接视线为绿色实线圈，只有
  `TARGET_LINK` 为绿色虚线圈。RWR 告警盘仍保持原规则。
* 数据链目标也参与敌方弹道预瞄；友军目标不生成预判点、不进入 `Lead.layout`、不产生
  `aimSnap`。不改变实际伤害友军的规则。
* 目标框不设数量上限；预瞄圈仍只为最近 4 个敌方目标生成。距离保持 25 m–4 km。

### 3.4 绘制

* “预瞄框”统一改名为“目标框”，包括注释、产品文档和界面描述。
* `frontend/src/hud/layout.js` 增加 `FRIENDLY_TARGET='rgb(30,30,255)'` 与
  `ENEMY_TARGET='rgb(30,255,30)'`；不要改模型 / 小地图的阵营色板。
* 车辆画矩形；导弹画圆。直接目标 `setLineDash([])`，数据链目标 `setLineDash([3,3])`。
* 友军只画目标框 / 导弹圈，不画预判圈、连接线或分割线。
* 每次绘制在 `save/restore` 中设置颜色与虚线，避免虚线泄漏到准星、RWR 或其他 HUD。
* 预判圈仍保留左青右橙的臂色；目标框颜色不替代臂色。

---

## 4. 实施顺序

| 步 | 工作 | 完成条件 |
| --- | --- | --- |
| 1 | 组件、车辆标签、`Datalink.run`、`Visibility` 与流水线 | 红蓝状态独立；丢视野当 tick 失效；`ecs.validate()` 通过 |
| 2 | 快照 / contacts 过滤与能力位 | 未知敌方装甲不发行；友军 / 自身正常；行宽不变 |
| 3 | AI 轮盘、感知与折扣 | 数据链目标使孔径转向；直接目标优先；无目标时仍可攻击链路目标 |
| 4 | 玩家锁定、SACLOS 可见性接入、攻顶种子与客户端信念 | 隔烟可获取；直接 / 链路跟踪等价；激光 / 导引头物理边界不穿 |
| 5 | 目标层 / 预瞄层拆分与 HUD 绘制 | 敌我颜色、实虚线、友军无预瞄全部正确 |
| 6 | 文档、CHANGELOG、版本与实测 | 1.45.0；靶场和战斗回归通过 |

每一步单独可构建。第 1–2 步落地后先跑无头检查，再接火控和 UI，避免把状态错误与显示错误
混在一起。

---

## 5. 测试计划

### 5.1 新增 `--check datalink`

所有场景以 `ecs.validate()` 收尾，并使用显式失败计数，不能只打印读数：

1. 玩家 MECH、AI MECH、APC 能上报；步兵不能上报也不能作为接触。
2. 红蓝字段独立；只写观察者阵营对应的槽位。
3. 地形、建筑、树冠、烟幕阻断观察；任意同阵营观察者仍可见时接触保持。
4. 最后一个观察者丢视野后 `link_seen=0`；`link_seen_tick` 保留最后成功 tick。
5. 重新观察刷新 tick；死亡、重生和 ECS ID 复用不继承状态。
6. 友军位置直接发送；自身和两座指挥部保持豁免。
7. 未知敌方装甲不出现在单位快照、contacts 或小地图；失效下一帧消失，不被死区保留。
8. 步兵仍在普通世界快照中，contacts 不含步兵 / 平民 / 尸体。
9. 新旧能力位组合、行宽和帧长度正确；`all` 基线仍可构建。

### 5.2 扩展现有检查

* `Harness/BeamCheck`：隔烟数据链获取 SACLOS / 攻顶；直接观察与数据链观察对 SACLOS 轨迹
  刷新完全等价；链路失效后的保持与记忆；物理照射不成立时 `beam_lit=0`、RWR 不告警；发射机
  到弹体的激光被挡时仍失控。攻顶依次覆盖直接信念、数据链信念、记忆估计三种种子来源。
* `Harness/GazeCheck`：链路目标在烟后或当前窗口外时写入低权重轮盘；`Watch` 转向；`Sense`
  取得目标；同方向直接目标优先；无更高威胁目标时 AI 仍转向并攻击。
* `Harness/FcsCheck`：直接 / 数据链 / 失效记忆三种输入对照，确认 AI 火控持续使用正确轨迹。
* `Harness/NetCheck` / `Tiers`：按阵营 contacts 缓存、过滤后的字节量级、15 Hz 解码与行宽。

### 5.3 前端测试

在 `tests/frontend/` 增加纯函数与假 Canvas 测试：

* 友军蓝 `rgb(30,30,255)`、敌军绿 `rgb(30,255,30)`。
* direct 实线、datalink 虚线；直接来源优先；虚线不泄漏。
* 友军 `marks=[]`，不进入吸附；敌方单圈 / 双圈与原有最近目标规则不变。
* 目标框生成不依赖弹道手臂；无 `TARGET_LINK` 的旧行不产生数据链标记。
* 实体行消失后已锁定信念继续外推；直接或链路恢复时跳回真身。
* 弹丸 `+5` 解码不破坏位置 / 速度；敌军与友军导弹圈分类正确。

### 5.4 回归命令

```bash
./run_tests.sh
./npm.sh run check:deps
./dotnet.sh build
./npm.sh run build
./dotnet.sh run -- --check datalink
./dotnet.sh run -- --check beam
./dotnet.sh run -- --check gaze
./dotnet.sh run -- --check fcs
./dotnet.sh run -- --check intercept
./dotnet.sh run -- --check net --minutes 30 --from 8
./dotnet.sh run -- --check tiers --minutes 14 --from 8
./dotnet.sh run -- --check battle --minutes 12
./dotnet.sh run -- --check stuck --minutes 12
```

性能读数只用于判断量级，不作为逐 tick 差分基线。靶场用 `debug_spawn`、烟幕和山脊验证：
实线变虚线、虚线消失、友军无预瞄、隔烟锁定、激光仍被遮挡。

---

## 6. 文档与验收

### 6.1 产品文档

| 页面 | 修改 |
| --- | --- |
| `systems/sight.md` | 直接视线与数据链分层、车辆观察资格、当前有效与最后观察 tick |
| `systems/ai.md` | 数据链作为低威胁可见证据、轮盘写入位置、窗口与记忆边界 |
| `systems/seekers.md` | 数据链可获取 / 刷新，弹体重捕仍要物理视线 |
| `systems/firecontrol.md` | 数据链进入信念，解算定律不变 |
| `systems/net.md` | 按阵营过滤、contacts 两份缓存、能力位、步兵边界、行宽不变 |
| `hud/aim.md` | 目标框命名、敌我颜色、实虚线、友军无预瞄、导弹圈 |
| `hud/hud.md` | 更新颜色速查与“预瞄白框”旧称 |
| `rules/units.md` | MECH / APC 的数据链资格，步兵没有 |
| `modes/range.md` | 数据链靶场验证场景 |

`docs/dev/pipeline.md`、`ecs.md`、`checks.md` 补充系统位置、组件归属和
`--check datalink`。`docs/plans/datalink.md` 在实施后记录落地结果，不回写成设计推导。

### 6.2 验收标准

* 所有车辆接触满足“红蓝独立、当前可见、丢视野即失效”，且无跨实体 ID 污染。
* 未知敌方装甲不出现在任何按接收者构建的快照或小地图数据中。
* 数据链目标能进入 AI 轮盘并被攻击，但同场景下直接目标保持更高优先级。
* 玩家可对烟幕 / 山脊后的数据链目标获取 SACLOS 与攻顶锁定。SACLOS 对直接 / 数据链目标
  的轨迹刷新完全等价，链路失效后按原记忆逻辑外推；`beam_lit` 与 RWR 仍只认物理照射，
  发射机到 SACLOS 弹体的物理通路不变。
* 攻顶弹按直接观察、数据链轨迹、记忆估计的顺序获得最佳射手信念点，使用原有出膛与
  导引逻辑；出膛后不会因数据链穿山重捕。
* 目标框颜色与线型符合规格；友军无预瞄、无吸附；导弹圈覆盖敌我且区分数据链来源。
* 服务端零警告，前端依赖 / 语法 / 构建通过，新增检查与既有回归全部通过。

---

## 7. 落地结果（2026-11-25，1.45.0）

六步一次落地，版本为 **1.45.0**。

| 步 | 结果 |
| --- | --- |
| 1 | `datalink` 标签、`link_seen` / `link_seen_tick`、`Datalink.run`、`Visibility` 与 shared 流水线完成 |
| 2 | 快照与 contacts 按阵营过滤；init `datalink`、unit bit6、projectile `+5` bit0；行宽不变 |
| 3 | `Sense.link_wheel` 在 `Watch` 前写低权重方位；扫描复用网格盒与扫描钟；`--check gaze` 三行链路场景通过 |
| 4 | 玩家候选接受数据链；SACLOS 直接 / 链路轨迹等价且物理照射分离；攻顶按最佳信念点播种 |
| 5 | 客户端目标层 / 预瞄层拆分；友蓝敌绿、直接实线 / 链路虚线、友军无预瞄、敌我导弹圈完成 |
| 6 | 产品页、工程手册、CHANGELOG、版本与专门检查完成 |

### 7.1 与计划的偏差

* **SACLOS 不保留独立 `known` 分支。** 按最终口径只扩展 `visible`；直接 / 数据链完全等价。
  `beam_lit` 单独保留为物理照射量，不参与制导。
* **AI 轮盘按扫描钟更新。** `Sense.link_wheel` 只在机组 `ACQUIRE` 扫描到点时遍历附近网格盒，
  避免每 tick 对全场接触做一次机组笛卡尔积；轮盘 8 s 余温覆盖这个更新间隔。
* **观察从全向改为 80° 前锥。** 最终实现统一使用 4 km 距离与 ±40° 水平锥：玩家看
  `sight_yaw`，AI 看 `gaze_yaw`，APC 看 `turret_yaw`，脚本傀儡看 `sight_yaw`；不受屏幕 FOV
  与倍率影响。AI 轮盘检查另受 4 km 硬上限约束。
* **目标框不设数量上限。** 低倍率不再因屏幕候选多而丢框；最近 4 台的限制只保留给敌方预瞄圈。
* **友军目标也区分直接 / 数据链线型。** 友军位置始终共享，因此直接可见为蓝色实线，仅共享可见
  为蓝色虚线；两者都没有预瞄。
* **普通世界快照保留步兵。** “步兵不上数据链”落实为不观察、不上报、contacts 不发送；没有把
  整个普通快照改成战争迷雾。

### 7.2 验证读数

| 检查 | 结果 |
| --- | --- |
| `./dotnet.sh build --no-restore` | 0 警告、0 错误 |
| `./run_tests.sh` | 前端 58 项通过；双端 42 cases，0 differ |
| `--check datalink` | `observe` 至 `link-top` 10 行全部 `ok`，`ecs.validate()` 通过 |
| `--check gaze` | 17 行全部 `ok`，含 `link-wheel` / `link-direct` / `link-memory` |
| `--check beam` / `fcs` / `intercept` / `hit` | 全部通过 |
| `--check battle --minutes 12`（seed 11） | `ecs.validate() ok`；7.17 ms/tick incl. wire，653 实体 |
| `--check net --minutes 10 --from 4` | `ecs.validate() ok`；10 min simulated in 110.1 s |
| `--check tiers --minutes 6 --from 2` | `ecs.validate() ok`；snapshot 119.0 → 26.1 kB/s |
| `--check stuck --minutes 12` / `range --minutes 2` | 全部通过；stuck 判词无受困机组 |
| WebBridge 靶场 | v1.45.1；友军蓝框无预瞄、敌军绿框有预瞄；烟幕后双方变虚线；20× 下 17° 偏轴目标仍上报，57° 锥外目标不出现；低倍率 9 个目标框全显示，同一中心目标在高倍率仍显示；contacts 步兵为 0 |

性能按量级读，不把不同实体规模的战斗读数当逐 tick 基线。截图保存在
`/tmp/mech-datalink-range.png` 与 `/tmp/mech-datalink-smoke.png`。
