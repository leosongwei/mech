# 产品改动登记

新条目加在最上面。格式：

```
## YYYY-MM-DD · 一句话标题

- 变更点（玩家可见的差异）
- 涉及文件：...
```

---

## 2026-10-23 · 超出 4 km 不调零，直接按瞄准线打

- 对准的目标超过 **4 km**（射线走满 4000 m 什么都没碰到，或**飞出地图边界**）时，
  火控**不再调零**：弹道就是瞄准线的延长线，炮口不会为了够一个远处的点抬成
  高抛。之前射线出图后在地图边界处「撞到」一个假的地形点（`tile_type` 出界当作
  rock），火控于是按 3–4 km 的最大距离调零，炮口能抬到 0.5 rad 以上，非常怪。
- 准星读数：没有弹着点（超出调零距离 / 射线出图 / 弹丸空中过期）一律红字，
  弹丸过期时带 `+` 后缀显示它飞过的水平距离（如 `L 9935m+`），不再是 `-`。
- 客户端 `map.js` 同步：`sightPoint` 出了地图就返回 null，弹道预测在地图外也
  不再把边缘高度当地面（服务端 `ground_hit` 出界本来就不算命中）。
- 涉及文件：`backend/server/terrain.py`（新 `inside`）、`ballistics.py`、
  `frontend/src/map.js`、`game.js`、`doc/product/weapons.md`

---

## 2026-10-23 · 弹丸寿命按 10 km 估算，射程不再截断飞行

- 所有弹丸的寿命（TTL）改为按 **10 km ÷ 弹丸速度** 手算后写死：30mm 9.3 s、
  12.7mm 11.2 s、5.56mm 11.1 s、火箭巢 16.4 s、SACLOS 36.0 s、红外攻顶 52.6 s、
  RPG 33.3 s。以前寿命按各自射程截断（12.7mm 3.0 s / 1800 m），打 1.9 km 外的
  山脊时弹丸半路自毁，准星只能显示一个 `-`。
- 服务端与客户端的 `min(life, range / 初速)` 截断一并去掉：`射程` 只剩「标称能量
  距离」的含义，不再决定弹丸飞多远；准星距离读数因此在全图范围内都是真实弹着点。
- 涉及文件：`backend/server/defs.py`、`systems/weapons.py`、`ballistics.py`、
  `frontend/src/map.js`、`doc/product/weapons.md`

---

## 2026-10-23 · 准星距离读数：弹丸空中过期时显示飞行距离并加「+」

- 视点在地形上但**弹丸还没飞到就过期**（例如 12.7mm 有效飞行 1800 m，山脊在
  1.9 km 外）时，红字读数不再是一个孤零零的 `-`，而是显示弹丸空中自毁时飞过的
  水平距离并加后缀 **`+`**（如 `L 1805m+`），与真正命中的 `1884m` 一眼可分。
  （寿命改为 10 km、并且超出 4 km 不再调零之后，这个例子不会再出现。）
- 既没有视点也没有解算（对着天空）时仍然显示 `-`。
- 涉及文件：`frontend/src/hud.js`、`doc/product/weapons.md`

---

## 2026-10-23 · 调零距离统一 4000 m，超出时准星距离变红

- 所有弹道武器（枪炮 + 无制导火箭）的调零射线统一走 **4000 m**（`defs.ZERO_RANGE`），
  不再按各自射程 × 1.2 分别计算——手指哪就解到哪，规则只有一条。
- 视点超出弹丸自身能飞到的距离（例如 30mm 只飞到 3.2 km）或没有解算时，仍然
  退回原始瞄准，同时**准星旁边的距离读数变成红字**，提示这一枪不会落在准星上。
- 涉及文件：`backend/server/defs.py`、`ballistics.py`、`netcode.py`（init 里的
  `zero.range`）、`frontend/src/map.js`、`game.js`、`hud.js`、`doc/product/weapons.md`

---

## 2026-10-23 · 靶场山脊新增不可摧毁的碉堡

- 靶场地图上新增 **5 座混凝土碉堡**，全部落在局部最高点（山顶与棱线上），
  从出生点的射击线看过去轮廓顶着天际线：弹着点打在碉堡上是、落在山脊前是、
  越过山脊也是，一眼可分——专门用来验证火控调零。
- **不可摧毁**：命中照常触发爆炸、火花与音效，但不掉血、不产生残骸、不结算赏金；
  自然阵营（`FACTION_NATURE`），不参与索敌、不可被红外弹锁定，AI 与移动系统
  直接跳过（当作静态景物）。
- 位置（搜索半径 160 m 内的最高瓦片）：主峰 `(870, 1100)` 201 m、
  东南棱线 `(1340, 1220)` 131 m、西棱线 `(730, 1590)` 108 m、
  南支脉 `(620, 1840)` 61 m、北肩 `(1270, 560)` 79 m。
- 外形：直径 11 m、高 8 m 的圆柱基座 + 直径 8 m 的观察层，顶上红色警示块，
  四面各一条射击孔；命中球两条（`y+4.0 r5.0`、`y+8.8 r3.6`），与外形一致。
- 涉及文件：`backend/server/defs.py`（`ETYPE_BUNKER`）、`mathutil.py`、
  `world.py`（`damage` 免疫、`combat_units` 排除）、`modes/range/bunkers.py`（新）、
  `modes/range/__init__.py`、`systems/ai.py`、`systems/movement.py`、
  `frontend/src/scene.js`、`frontend/src/map.js`、`doc/product/range.md`

---

## 2026-10-23 · 火控自动调零：枪炮与无制导火箭对准视点

- 30mm 机炮、12.7mm 机枪、5.56 步枪与**无制导火箭巢**改为**自动调零**：
  服务端沿**炮口射线**找到第一个视点（先查单位命中球，再查地形与建筑），解出
  落在这个点上的仰角，然后按解出的仰角开火。准星压在哪，弹就落在哪，远距离
  不再需要手动抬枪补下坠。
- 射线长度上限 = **武器射程 × 1.2**（`ZERO_MARGIN`）——`range` 只是能量参考，
  不是硬边界；视点比 25 m 还近、弹丸寿命/射程够不到、或者需要的仰角超过
  **约 60°**（`ZERO_MAX_ELEV`）时，**退回原始瞄准**（保留发射器机械偏置与散布）。
- SACLOS 与红外攻顶**不调零**：制导本来就把弹压在视线上。
- AI 同样享受调零，提前量解算不再额外补重力，避免与调零叠加成双倍补偿。
- 客户端 `map.js` 用同一套算法、同一 1/30 s 步进预测落点，所以准星距离就是真实
  弹着距离；客户端的单位命中球与服务端逐一对齐（机甲按部位、APC 车体+炮塔、
  基地、碉堡、步兵），不再用单球近似。
- 涉及文件：`backend/server/ballistics.py`（新）、`defs.py`（`ZERO_MARGIN`、
  `ZERO_MAX_ELEV`）、`netcode.py`（init 下发 `zero`）、`systems/weapons.py`、
  `systems/ai.py`、`frontend/src/map.js`、`frontend/src/game.js`、
  `doc/product/weapons.md`

---

## 2026-10-23 · 曳光弹改成纯色四棱锥

- 子弹（30mm 机炮、12.7mm 机枪、5.56 步枪）的曳光弹原来是一段 12 边圆柱，
  现在是一根**四棱锥**：平底朝飞行方向、尖头拖在后面（视觉暂留的拖尾感），
  锥体中心落在弹丸上，颜色取武器定义里的 `color`，**不受光照影响**（纯色），
  背光时也是同一种亮黄。
- 口径决定粗细：`cal`（mm）越大越粗，30mm 最粗，**12.7mm 只有约四成**，
  5.56mm 最细；长度仍按弹速（`min(16 m, 弹速 × 0.014)`）。
- 每发子弹的三角形数从约 40 个降到 **6 个**；导弹、火箭、攻顶弹的外形不变。
- 涉及文件：`backend/server/defs.py`（武器新增 `cal` 口径字段）、
  `frontend/src/meshes.js`（新增四棱锥网格）、
  `frontend/src/renderer.js`（`uUnlit` 通道 + 该网格的实例上限）、
  `frontend/src/scene.js`

---

## 2026-10-23 · 火箭巢射速提到 0.1 s 一发

- 火箭巢击发间隔 **`0.3 s` → `0.1 s`**（rpm 200 → **600**）：按住扳机就是一串
  火箭雨，40 发 **4 s** 打光（原来要 12 s）。弹速、散布、下坠等其他参数不变。
- 内部：冷却判定的浮点残渣（`0.1 − 3 × 1/30 s ≈ 1e-9`）会让每发多等一整 tick，
  0.1 s 的射速实际变成 0.133 s；判定加 `1e-4` 容差后 600 rpm 正好 3 tick 一发。
  其他自动武器的节奏不受影响（30mm 仍是 10 tick、机枪仍是 3 tick）。
- 涉及文件：`backend/server/defs.py`、`backend/server/systems/weapons.py`、
  `doc/product/weapons.md`

---

## 2026-10-23 · 三把爆炸类武器按真弹标定

- 火箭巢按 **S-8**（80 mm，B-8 巢）重做弹道：出管 **55 m/s**（原来是 80），
  推力 `560 m/s²`，约 1 s 后到 **610 m/s** 巡航（原 520），射程 **4 km → 3.2 km**，
  寿命 8 s。飞行时间 1 km 2.1 s、2 km 3.7 s（下坠 28 m）、3.2 km 5.7 s（下坠 96 m）。
  散布从 `0.00175` 放回 `0.006 rad`（1 km 上 σ≈6 m），机械上仰随之重算为
  `0.0429 rad`（1 km 与机炮弹道交会，`map.js` 的预测同步）。
- SACLOS 按 **TOW** 标定：出管 **65 m/s**、以 `110 m/s²` 加速、约 2 s 后
  **278 m/s** 巡航（原 330），射程 3.75 km 要飞约 13 s，寿命 18 s → 20 s。
- 红外攻顶导弹按 **标枪** 标定：软发射 **20 m/s**、约 1 s 后 **190 m/s** 巡航
  （原 300），射程 **4.75 km → 2.5 km**，寿命 22 s → 20 s。攻顶弹道改成"先陡爬升
  到目标上方 **150 m**、贴着该高度巡航、距目标 **220 m** 转俯冲（约 34°）"
  （原固定 260 m / 520 m），爬升高度与俯冲距离现在是 `defs.py` 里的
  `climb` / `cruise` / `dive` 三个参数，2 km 目标约 12 s 命中。
- 涉及文件：`backend/server/defs.py`、`backend/server/systems/projectiles.py`、
  `doc/product/weapons.md`

---

## 2026-10-23 · SACLOS 弹道不再蛇形发散

- SACLOS 的制导瞄准点从"视线上的垂足"改为"视线更前方的引导点"：旧做法下导弹
  一旦有横向误差就被要求近 90° 转向，弹道在视线两侧来回抽并越抽越大（3 km 上
  摆到 ±90 m，看起来就是发散的 S 形，命中率极低）；现在直接向视线平滑收敛，
  2 m 的初始横向误差 3 s 内衰减到 0.2 m 以内，3 km 上稳定压线。
- 视线扫动（追移动靶）时导弹平滑跟随，不会震荡：0.1 rad/s 扫动时最大滞后约
  55 m，之后指数收敛。
- 制导点退化（导弹几乎已经在视线方向上）时保持原航向直飞，不再被强行掰向
  世界 +X 方向。
- 保留一点真实感：每发 SACLOS 都带一个**随机相位与频率**的侧向推力偏差
  （`wob = 30 m/s²`、`wob_f = 1.5`），制导律每 tick 反向修正。手感按二代 SACLOS
  （TOW）来——弹道基本笔直，只有 **±0.3–0.4 m、周期 1–2 s** 的细小起伏，
  弹体不滚转，每发形状略有不同；偏差始终被视线压住，不会像旧制导那样发散。
  随机数存进新的 `proj_seed` 组件，由 `weapons.fire` 从世界随机源取。
- 涉及文件：`backend/server/systems/projectiles.py`、`systems/weapons.py`、
  `defs.py`、`components.py`、`doc/product/weapons.md`

## 2026-10-23 · 武器手感、红外锁定圈与战场残骸

- 无制导火箭巢：散布缩小为原来的 70%（`0.0025 → 0.00175`），弹匣 **20 → 40 发**，
  改为全自动——按住扳机每 `0.3 s` 一发（原为半自动，每次扣扳机一发）。
- 攻顶导弹：准星改为**圆圈**，直径 = 视口宽度 5%（比普通十字准星大一圈）；
  圆内即锁定范围，目标离开圆圈**立即脱锁**（取消原来的 0.10 rad 保持带），
  锁定进度归零；**步兵与平民不可锁定**，只有 APC / 机甲能被导引头捕获。
  圆圈在屏幕上的像素大小固定，倍率越高覆盖的角宽越小（1x ≈ 0.051 rad，
  10x ≈ 0.005 rad）；客户端把倍率与视口宽高比随输入发给服务器。双手都装导弹时
  两个圈等大、各向本手一侧错开约 40% 半径（左圈偏左、右圈偏右），颜色分明。
- 被击毁的步兵倒地成尸体，**5 s** 后消失；被击毁的 APC / 机甲变成压暗的残骸并
  **持续冒烟**，**10 s** 后消失。残骸不可锁定、不能命中、不挡弹道、不算战斗单位。
- 靶场：被击毁的靶子改为 **3 s 后在地图随机位置重生**（离玩家出生点 ≥ 350 m、
  其他靶子与刚被击毁的残骸 ≥ 140 m——新靶不会和旧车壳重合、坡度受限；移动靶随机
  选一条 400–840 m 的巡逻段），场上恒定 10 辆静止靶 + 2 辆移动靶，不再固定回
  原靶位。
- 内部：把 `battle` / `range` 的差异从各系统里的 `if` 收进 `backend/server/modes/`
  下的独立系统（battle：`waves.py` 波次与占领；range：`targets.py` 靶子）；
  两个模式共享的收入/重生归 `systems/economy.py`，残骸倒计时归
  `systems/debris.py`。新增模式只需加一个模式文件夹。
- 修掉地图外围的怪异尖峰：地形着色器对地图边界外的采样点直接外推边缘高度，
  最外圈 LOD 网格因此被拉成千米级尖刺（靶场东侧尤其明显）；现在采样点先夹到
  地图内，边界外一律取边缘高度，靶场与战斗地图的山体轮廓都恢复正常。
- 涉及文件：`backend/server/defs.py`、`components.py`、`world.py`、`netcode.py`、
  `main.py`、`systems/locking.py`、`ai.py`、`economy.py`（新）、`debris.py`（新）、
  `modes/*`（新）、`frontend/src/game.js`、`hud.js`、`scene.js`、`particles.js`、
  `renderer.js`、`doc/product/*`、`AGENTS.md`

## 2026-10-23 · 新增靶场模式（服务端 `--range`）

- 新增 `./dev.sh range`（会话 `mech-range`，端口 8011，日志 `/tmp/mech-range.log`）
  与 `python run.py --range`：同一客户端接入另一张 **4 km × 4 km** 靶场地图。
- 地图：西北一座 200 m 高山（带摆动山脚与两条支脉），其余为 9 m 起伏的平原，
  全草地无掩体；玩家固定在南部中间蓝叉出生，面朝山顶，山距约 2.2 km。
- 12 个蓝方 APC 靶位（山顶/山坡/山脚/平原散布），其中 2 条是 8 m/s 往返巡逻的
  移动靶；靶子**不索敌不开火**，被击毁 3 s 后原位重生，击毁仍得 $180 赏金。
- 靶场里阵营固定 RED、初始资金 $99999、**随处可换装**（`AT BASE - REARM:` 清单
  常驻，不受 220 m 补给范围限制）；无波次、无城镇占领、无胜负；死亡 8 s 后回到
  同一出生点，`RESTART` 重建靶子并 1 s 后重生。
- 界面：启动页副标题改为 `WEAPON RANGE · 4 KM MAP · APC TARGETS · REARM ANYWHERE`，
  状态行 `LOADING RANGE...`；顶部战况条 `WAVE n` 改为 `RANGE`，其下新增
  `TARGETS n`（存活蓝方单位数）。
- 涉及文件：`backend/server/terrain.py`、`backend/server/world.py`、
  `backend/server/systems/range_targets.py`（新增）、`backend/server/systems/ai.py`、
  `backend/server/systems/spawner.py`、`backend/server/main.py`、
  `backend/server/netcode.py`、`backend/run.py`、`dev.sh`、
  `frontend/src/game.js`、`frontend/src/hud.js`、`doc/product/*`、`AGENTS.md`

## 2026-10-23 · 建立产品文档

- 新增本目录：交互流程、界面说明、单位参数、武器参数、改动登记。
- 文档数值与 `backend/server/defs.py`、`systems/spawner.py` 对齐；以后改数值必须同步修改。
- 涉及文件：`doc/product/*`、`AGENTS.md`

## 2026-10-23 · 蓝方增援不再空投到红方基地

- 修复：蓝方波次曾按红方路径坐标计算，导致蓝军直接出现在红方指挥部旁（约 600 m）。
- 现在双方各自沿自己那份路线推进，蓝方空投点在自己前线后方 10 个路点，距本方基地 0.1–2.7 km。
- 顺带修复：路径终点此前用瓦片坐标追加（每条路终点落在 (1300,1300) 荒野），现已改为蓝方基地。
- 影响界面：小地图上双方兵力首次呈对称分布。
- 涉及文件：`backend/server/systems/spawner.py`、`backend/server/world.py`、`backend/server/terrain.py`

## 2026-10-22 · 右上角调试面板（RESPAWN / RESTART / GOD）

- 新增：仅在服务端 `MECH_DEBUG=1` 时出现的调试面板，三个按钮分别立刻重生、
  清场重开一局、切换免伤；GOD 按钮状态取自服务端，刷新后不会显示错误状态。
- 新增调试消息 `debug_restart`。
- 涉及文件：`frontend/src/index.html`、`frontend/src/game.js`、
  `backend/server/main.py`、`backend/server/netcode.py`

## 2026-10-22 · 反坦克兵的火箭朝向修正

- 修复：火箭筒兵的战斗部锥体原本竖在筒口朝上，现在与筒身同轴朝向正前方。
- 涉及文件：`frontend/src/scene.js`

## 2026-10-22 · 巨石改为 low-poly 岩石群

- 替换：原本 900 颗独立灰球，改为 80 处岩石群、每群 2–8 块不规则多面体角石
  （平直刻画、半埋、大小与倾角随机），每处岩石群另带 3 块散石。
- `TILE_ROCK` 仍阻挡通行，与小地图配色一致。
- 涉及文件：`frontend/src/meshes.js`、`frontend/src/renderer.js`、`backend/server/terrain.py`

## 2026-10-22 · 火箭巢改为精准单发，出生点退到基地区域外

- 火箭巢：由连发弹幕改为半自动单发（20 发、无备弹、无换弹），散布 0.0025，
  机械上仰 0.0431 rad，1 km 内与机炮弹道基本重合，更远明显下坠。
- 出生点不再落在指挥部建筑里：改为指挥部外 70–130 m 环，城镇外 40–85 m 环。
- 涉及文件：`backend/server/defs.py`、`backend/server/world.py`、`frontend/src/map.js`

## 2026-10-22 · 第一人称座舱与 1x/4x/10x/20x 瞄具

- 视角改为机甲座舱第一人称（眼位 5.05 m），滚轮切换四档倍率，倍率过渡平滑。
- 新增左右臂独立准星、弹着点距离、锁定框与锁定音。
- 涉及文件：`frontend/src/game.js`、`frontend/src/hud.js`、`frontend/src/input.js`、
  `frontend/src/scene.js`、`backend/server/defs.py`
