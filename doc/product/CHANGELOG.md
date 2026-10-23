# 产品改动登记

新条目加在最上面。格式：

```
## YYYY-MM-DD · 一句话标题

- 变更点（玩家可见的差异）
- 涉及文件：...
```

---

## 2026-10-23 · 曳光弹改为面向镜头的单面长方形

- 子弹（30mm 机炮、12.7mm 机枪、5.56 步枪）的曳光弹原来是一段 12 边圆柱，
  现在是一块**面向镜头的长方形片**：长边沿弹道、矩形中心落在弹丸上，颜色仍用
  武器定义里的亮黄（`color`），亮度乘 2.6 压过光照项，背光/远处也能看清。
- 每发子弹的三角形数从约 40 个降到 **2 个**；导弹、火箭、攻顶弹的外形不变。
- 涉及文件：`frontend/src/meshes.js`（新增单面四边形网格）、
  `frontend/src/renderer.js`（`plate()` 绘制朝向镜头的矩形及其实例上限）、
  `frontend/src/scene.js`、`frontend/src/game.js`（把相机位置传给 `drawUnits`）

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
