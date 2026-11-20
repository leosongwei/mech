# 一局的流程

> **读者**：玩家
> **一句话**：从打开页面到下一局开始，一局战斗经过哪几步，每一步玩家看到什么。
> **数值出处**：`Core/World.cs`（`reset_match`、每 tick 一帧）、`Modes/Mode.cs`
> 的 `restart_respawn`、`Systems/Mortality.cs`（终局倒计时）、`Systems/Economy.cs`、
> `frontend/src/app/boot.js`（启动页与部署）
> **核对**：1.39.0 / 2026-11-20

## 1. 一条线看完

```
打开页面 → 启动页（CONNECTING → LOADING → READY）→ 大厅（呼号 / 阵营）
   → DEPLOY → 座舱（指针锁定）→ 对局循环
        ├─ 中弹 → 手臂报废 → 机体归零 → MECH DESTROYED → 倒计时 → 重生
        ├─ 城镇争夺 → 前线推进 → 增援波次（见 modes/battle.md）
        └─ 敌方指挥部被打爆 → VICTORY / DEFEAT → 倒计时 → 同一张地图重开
```

靶场（`--range`）走同一条前四步，之后换成一整套练习规则，见
[modes/range.md](modes/range.md)。

## 2. 启动：从打开页面到 READY

1. 浏览器打开 `<host>:8010/`（靶场是 `:8011/`），打开即玩：身份只是进程内的一个 pid。
2. 客户端立刻连服务器，启动页状态行读 `CONNECTING...`。
3. WebSocket 建立后，服务器一次性发来初始化包：整张 16 km 地图加上全部数值定义
   （武器、单位、机甲几何、雷达常数、可买清单、调零档位）。这些数字之后不再变。
4. 状态行改成 `LOADING 16 KM MAP...`（靶场是 `LOADING RANGE...`），客户端载入
   地形与小地图底图。
5. 载入完毕读 `READY`，同一页上的大厅亮起。
6. 任何一步失败，状态行读 `CONNECTION FAILED - IS THE SERVER RUNNING?`，
   左下角红字给出异常原文；`DEPLOY` 在失败状态下不生效。

启动页与大厅的样子（标题、副标题、版本号、键位表、三行输入）见
[hud/lobby.md](hud/lobby.md)。

## 3. 大厅

1. 填呼号（`PILOT ID`，默认 `PILOT-<pid>`）与阵营（`RED` / `BLUE` / `AUTO`）。
2. `AUTO` 由服务器分配：落在人少的一方，两边一样多时去红方。
3. 机甲一律以默认装备出生——大厅只决定呼号与阵营。换装是进对局之后在基地买卖，
   见 [hud/panels.md](hud/panels.md) 与 [rules/economy.md](rules/economy.md)。

## 4. 部署

1. 点 `DEPLOY`。客户端发一条 `join`，把呼号与阵营
   交给服务器，记住呼号（浏览器本地），请求指针锁定，启动帧循环。
2. 服务器按阵营给一台机甲，落在出生点上（规则见 [rules/spawn.md](rules/spawn.md)），
   回一条 `joined`，消息栈出现 `DEPLOYED AS <呼号>`。
3. `ENTER` 在对局里不会再 joins 一次：部署是一次性的，按钮在部署时交还焦点。

## 5. 对局循环

1. 座舱里每秒 30 次把人机输入发出去：观瞄方向、推杆、两个扳机、烟幕、调零与
   倍率档位。键位见 [controls.md](controls.md)。
2. 服务端同一个 30 Hz 跑一 tick：AI、射击、飞行、命中、伤害、死亡，一笔账一
   tick 结清一次。
3. 每个 tick 给每个连接发恰好一帧（快照或时钟块），画面上的一切都来自这些帧，
   客户端只做插值。帧、拥塞与丢帧的判据见 [systems/net.md](systems/net.md)。
4. 服务器只把视距内的东西发给你：半径按单位种类、进出各一个边界，自己的机甲与
   双方指挥部永远下发。半径表见 [systems/net.md](systems/net.md)。
5. 小地图单独一条通道，画全场 16 km，不受视距限制，见 [hud/hud.md](hud/hud.md)。

## 6. 战场推进

1. 三条兵线从各自指挥部通到敌方指挥部，兵线上的城镇归占领它的一方。
2. 站进城镇占领圈、守够时间的一方能把它占下来（半径、秒数、争夺与回退见
   [rules/map.md](rules/map.md) 与 [modes/battle.md](modes/battle.md)）。
3. 前线的城镇成为己方出生点、补给点与出兵点；指挥部按时向下发机甲，城镇按时
   下发步兵与 APC，波次时间表见 [modes/battle.md](modes/battle.md)。
4. 每方还会派出敌后破袭机甲，绕开主战线去切断补给链，任务规划见
   [systems/ai.md](systems/ai.md)。

## 7. 补给与换装

1. 只有站在己方指挥部或己方城镇旁边才能补货，此时左下出现黄字
   `[B] REARM / REPAIR`。
2. `B` 打开面板，报价、成交、整备工时与"开工期间不能动、不能开火"的规则见
   [hud/panels.md](hud/panels.md) 与 [rules/economy.md](rules/economy.md)。
3. 靶场随处可换装。

## 8. 受伤、死亡与重生

1. 中弹先落在被击中的部位：手臂有自己的血量，打光了那条臂的武器报废（弹药一起
   没了），机体池归零才整台报销。伤害判据见 [rules/damage.md](rules/damage.md)。
2. 机体归零：机甲炸成一团爆炸，屏幕换成死亡卡，相机绕阵亡地点慢慢转，战况还能
   继续看（样子见 [hud/messages.md](hud/messages.md)）。
3. 屏幕上的 `RE-DEPLOY IN n s` 倒数完，就在选定的城镇或指挥部环内重生。等待时间
   与出生点怎么选见 [rules/spawn.md](rules/spawn.md)。
4. 装备随车一起没了：重生出来的是标准机体，买的武器要重新买，见
   [rules/economy.md](rules/economy.md)。
5. 死亡不扣钱，战绩里的 DEATHS 加一。

## 9. 胜负

1. 一方的指挥部血量见底，这一局立刻分出胜负：全屏 `VICTORY` 或 `DEFEAT`，第二行
   说明是哪座指挥部没了。
2. 双方进入倒计时 `NEW MATCH IN n s`，战斗在倒计时里继续进行。
3. 胜负只有一种结法：先打爆对方指挥部的一方获胜；指挥部的血量与胜负判据见
   [modes/battle.md](modes/battle.md)。

## 10. 重开

1. 倒计时归零，服务器清空战场、按同一张地图重建基地、城镇与平民，双方从小地图
   开始重新推一遍。
2. 每位玩家回到开局资金，战绩（击杀、死亡、得分）留着——那是这一夜的账，不是这
   一局的账。
3. 数秒后双方在新一轮的出生点上重生，从头再来一遍。

## 11. 另一条线：靶场

靶场是同一套客户端连的另一张 4 km 地图：`$99999` 起步、随处换装、被动靶子与
步兵班、一座导弹防御炮位，没有波次、没有城镇、没有胜负。流程与规则见
[modes/range.md](modes/range.md)。

## 12. 没有人在看的时候

服务器上没有连接时，世界停在原地：模拟与发帧一起暂停，刷新回来的第一个玩家看到
的就是离开时那一局。这条规则的实现与例外见 [../dev/pipeline.md](../dev/pipeline.md)。

## 13. 相关页

* 键位：[controls.md](controls.md)
* 大厅：[hud/lobby.md](hud/lobby.md)
* 对局 HUD：[hud/hud.md](hud/hud.md)
* 消息与击杀行：[hud/messages.md](hud/messages.md)
* 换装面板与整备：[hud/panels.md](hud/panels.md)
* 战场规则：[modes/battle.md](modes/battle.md)
* 出生与重生：[rules/spawn.md](rules/spawn.md)
* 网络与视距：[systems/net.md](systems/net.md)
* 调试消息：[interfaces/debug.md](interfaces/debug.md)
