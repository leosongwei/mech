# 浏览器里验证

> **读者**：维护者（写客户端 / 在浏览器里验证的人）
> **一句话**：这个项目没有前端测试套件，改动都在浏览器里看——这页是控制台的门、自由相机、
> 无键盘移动，以及求值与截图的手法。
> **代码**：`frontend/src/app/game.js`（暴露面）、`app/systems/camera.js`（`freeCam`）、
> `render/renderer.js`、`main.js`
> **核对**：1.39.0 / 2026-11-20

## 1. 入口

```bash
./dev.sh start        # 战斗：http://127.0.0.1:8010/
./dev.sh range        # 靶场：http://127.0.0.1:8011/
```

`main.js` 在 `DOMContentLoaded` 时造 `Game` 并挂成 `window.__game`（下面一律写 `g`），
所以控制台第一句是 `const g = window.__game`。机制在靶场上量、战场只用来验收，
靶场一节见 [../product/modes/range.md](../product/modes/range.md)。

`dev.sh` 一律带 `MECH_DEBUG=1`，调试消息与设置面板的 DEBUG 区才在（消息表与 payload 见
[../product/interfaces/debug.md](../product/interfaces/debug.md)）。

## 2. 常用门

| 门 | 是什么 |
| --- | --- |
| `g.net.meta` | 最近一条 meta：金钱、血量、波次、重生、`god`、`winner`、`lock_target` |
| `g.net.entities` / `.projectiles` / `.smoke` / `.contacts` | 插值后的图（`Map`）、导弹、烟、小地图点 |
| `g.net.tick` / `.held` / `.loss` / `.rttMs` / `.tps` | 时钟与网络三数字（同一份数据 HUD 在用） |
| `g.net.send({t:'…'})` | 一条调试或输入消息；`g.net.send({t:'respawn'})` 永远允许 |
| `g.input` | 摇杆与视线：`yaw` / `pitch` / `freeYaw` / `zoom` / `zeroRange` / `fireL` / `fireR` |
| `g.map` | `MapData`：`heightAt` / `walls` / `roads` / `tiles` / `w` / `h` / `tile` |
| `g.renderer` | `draw` / `stats` / `propStats` / `quality` / `far` / `roads` / `beginDynamic()` |
| `g.audio` | `setVolume(0..1)` / `setAlarmVolume` / `field` / 各个 cue |
| `g.particles` | 粒子池（`slots`、`update`、`emit`） |
| `g.hud._project(x,y,z)` | 世界点投到屏幕坐标，拍照取景时对位用 |
| `g.freeCam` | 自由相机（§3） |
| `g.aim` / `g.lead` / `g.rwr` / `g.beliefs` | 视图系统各自的状态：吸附、预瞄圈、告警器、信念点 |

## 3. 自由相机 `g.freeCam`

`systems/camera.js::camera` 先问它，命中了就用它当这一帧的眼——所以座舱、缩放、死亡环绕都
让位，HUD 照画，而准星与开火仍然来自玩家自己的视线（相机只换画面）。`g._camera` 依旧可替换，
需要 spec 表达不了的机位时用它。

```js
g.freeCam()                                  // 当前 spec（含解算后的 eye / look），没有则 null
g.freeCam(null)                              // 还给座舱
g.freeCam({ x, y, z, yaw, pitch, zoom })     // 固定眼
g.freeCam({ id, dist, yaw, height, zoom })   // 跟一台车
g.freeCam(id)                                // 同上，标准机位
```

* **固定眼**：世界坐标一个点，`yaw` / `pitch` 是视线（`dirFrom` 的约定，`-1.45` 近乎俯视），
  `pitch` 缺省 `-0.1`。`zoom` 是光学档：`fov = BASE_FOV / zoom`，所以 4 就是视野收窄四倍，
  一米外的一根炮管、十二米外的一排管子都这么看。
* **跟随实体**：每帧按那台车**画出来的位姿**重解（`x` / `z` / `yaw`），所以走着的车、摆动
  的手臂都被跟上。缺省值：`dist` 12 m、`height` 2.6 m，`yaw` 是**从车指向眼**的方向，
  留空取车身 yaw + 0.9 rad（左前方四分之三视角）——它在车开动时保持这一侧。眼始终看向车的
  中心（单位表里的 `height * 0.5`）。
* 每个世界里的车 id 都在 `g.net.entities` 里；快照是围着飞行员的球，所以要先
  `debug_spawn` 放一台想近距离看的。`[...g.net.entities].filter(([, e]) => e.type === 4)`
  是线上所有机甲。
* spec 会活到释放或刷新页面为止：**页面跳转（`navigate`）回到大厅，要重新点 `DEPLOY`**。

## 4. 无键盘移动

`debug_tp` 沿**服务器**手里的输入朝向（`pl.in_yaw`）移动，`pos.y` 取该点地面高度，
并把速度清零（一次跳跃不是运动）。所以迭代着走：先设 `g.input.yaw`，等几个 tick 让服务器
收到，再发一条，然后从**下一个快照**读回位置。

```js
const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
const me = () => g.net.entities.get(g.net.meta.mech);
g.input.yaw = Math.atan2(TZ - me().pz, TX - me().px);
await sleep(300);                            // 让 in_yaw 到位
g.net.send({ t: 'debug_tp', dist: 1200 });
await sleep(1100);                           // 等快照对回来
```

* 读 `px` / `pz`：那是权威位置；`x` / `z` 是插值出来的画的位置，一次大跳之后会冻结，
  读起来像"传送没生效"。
* 一次一秒左右；`dist` 默认 300 m，`angle` 相对朝向。
* 完整的分步循环与其余几手常用指令在
  [../product/interfaces/debug.md](../product/interfaces/debug.md) §4。

## 5. 输入是字段，视线不是

* `g.input.pitch`、`g.input.yaw`、`g.input.freeYaw` 都是普通字段，直接写就行——无头自动化
  里合成鼠标移动不必，指针锁被浏览器拒绝也不会影响瞄准（没有用户手势，这属于预期）。
* **`g.input.viewYaw` 是只读 getter**（`yaw + freeYaw`），写它什么也不会发生，而座舱眼一直
  看着 `input.yaw`——那是一台停着的车最后发给服务器的朝向，久不动就一直是旧的。要转头：
  写 `g.input.yaw`（车会以 80 deg/s 转向它）或 `g.input.freeYaw`（ALT 的自由视角，只动眼）。
* 打死自己验证死亡流程：`debug_god` 关掉后 `g.net.send({t:'debug_hurt', module: 0, amount: 99999})`，
  相机落到基地环绕，`{t:'respawn'}` 立刻回来。

## 6. 求值与截图走浏览器桥

控制台不是 shell，所以从外面驱动页面用浏览器桥：
`POST http://127.0.0.1:10086/command`，`action` 为 `evaluate` 或 `screenshot`。

* 代码里带引号就先把请求体写成 `/tmp` 下的 JSON 文件，再 `curl -d @文件`——省掉一整层
  引号转义。
* `evaluate` 里可以 `await`（`await sleep(...)`、`await fetch(...)`），所以一段多步操作
  一次请求就能跑完；每次调用控制在 shell 超时以内，别用 `-m` 去顶。
* **截图单独发一次请求**：求值返回之后画面才是要拍的那一帧。

## 7. 拍照与取景技巧

```js
const old = g.renderer.roads.count; g.renderer.roads.count = 0;   // 收起沥青
g.renderer.beginDynamic();                                       // 下一帧不画动态体
g.renderer.propStats; g.renderer.stats;                          // 道具/绘制计数，核对画面
g.map.heightAt(x, z); g.map.walls; g.map.roads;                  // 画面背后的数据
```

* 道路 pass 读的是 `g.renderer.roads.count` 这个字段（`renderer.roadCount` 是只读 getter，
  读得出来、写不进去）；存下旧值，拍完再写回去。
* `beginDynamic()` 丢掉**下一帧**的一次 `emit` 并清空不透明/混合实例集，所以它拍的是车、
  弹、粒子都不在的那一帧——连着几个 `requestAnimationFrame` 之后再截图，动态体就回来了。
* `g.renderer.far` 是 32000（远平面），`g.map.heightAt` 与服务器 `Terrain.height_at` 同解，
  所以地面、路面、屋顶都能和画面上的位置逐个对上。
* 换掉整块画面用 `g._camera`；只想换视角用 `g.freeCam`（§3）。

## 8. 相关页

* 客户端的层、pass 顺序、场景局部坐标、质量档：同目录 [client.md](client.md)
* 调试消息表与控制台示例：[../product/interfaces/debug.md](../product/interfaces/debug.md)
* 玩家看到的记号与 HUD：[../product/hud/aim.md](../product/hud/aim.md)、
  [../product/hud/hud.md](../product/hud/hud.md)
* 按键与操作：[../product/controls.md](../product/controls.md)
* 靶场怎么用它当试验台：[../product/modes/range.md](../product/modes/range.md)
