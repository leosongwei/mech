# 产品改动登记

新条目加在最上面。格式：

```
## YYYY-MM-DD · 一句话标题

- 变更点（玩家可见的差异）
- 涉及文件：...
```

---

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
