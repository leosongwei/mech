# 前端重构:一条图形管线、单位/地图渲染分文件、网络与音频成系统

日期:2026-11-13
状态:**已实施(九步全部完成),待签收后删除旧目录并改名**
范围:**`frontend/src/` 的模块结构。玩家看到的东西一行都不变。**
版本:纯内部重构,玩家不可见 → 实施时只 bump `MechServer.csproj` 的第三位
(1.25.0 → 1.25.x),`docs/product/CHANGELOG.md` 不写条目。任何一步如果顺带改了手感、
数值或 HUD 文案,它就不再是重构,要按 feature 处理(第二位 + changelog)。

一句话目标:

1. **每帧是一条显式的管线**(pass 列表 + stage 列表),不再是 `game.frame()` 里散着
   十几个 `this._xxx()` 调用;
2. **渲染拆成"资源 / 着色器 / 几何 / pass / 批次"五个可换的文件**,单位模型按类型
   分文件,地图(地形、道路、道具)也各自成文件;
3. **网络与音频各自成系统**:线格式(可 node 测的纯解码)、视图(插值/运动估计)、
   声音场(声速到达 + 声部预算)各有一层,而不是塞在 `Game` 的方法里。

---

## 0. 落地结果(2026-11-13,九步全部完成)

新树在 `frontend_v2/`(分支 `frontend-v2`),旧树 `frontend/` 原样保留;两者都往
`assets/` 出包,`MECH_FRONTEND=v2 ./dev.sh build` 构建新的那个。签收之后:删
`frontend/`,把 `frontend_v2/` 改名为 `frontend/`,再把 `tests/frontend/{height,fcs}`
里的两处 import 路径从 `frontend_v2/` 改回 `frontend/`。

| 项 | 结果 |
| --- | --- |
| 模块数 | 15 个文件 / 8726 行 → **85 个模块 / 9353 行**(最大 358 行: `render/renderer.js`) |
| 依赖图 | `scripts/depgraph.mjs`:85 文件 / 206 边 / **0 环**,四层单向,领域层只剩 `render -> map` 一条边 |
| 语法 | `scripts/syntax.mjs`:85 个模块全部可解析(两者都挂进 `npm run build`) |
| bundle | 145,725 B → **156,642 B**(+7.5%,全部是模块边界与新增的表格/校验;注释在 production 里被剥掉) |
| 着色器 | 五个 program 的装配结果与旧版**逐字节相同**(新增的是覆盖层自己的那一对) |
| 端到端 A/B(战场固定机位) | draws 496 → 495-494、instances 54,615 → 54,026-54,311(差值是同一瞬间战场上的活动体)、`roadCount` **350,340 两边相同**、道具 **231,472 instances / 2,116 chunks 两边相同** |
| 测试 | `./run_tests.sh`:js 44 通过;双端对拍 **42 例 0 差异**,最差 1.48e-2 m(容差 0.25 m) |
| 浏览器实测 | 出生/部署/座舱、预瞄环 + 配对白线、两臂弹着标尺、开炮(mag 389 → 381、击毁与赏金)、SACLOS 威胁在 1161 m 起"看得到"(1368 m 公式)、威胁箭头 + 表盘闪烁环、击杀播报、死亡/重生、换装面板(草稿 → 报价 $980/6 s → 确认后 −$974 并开工 6 s)、设置面板(三条滑条 + 辅助开关 + 三个调试键)、V/Q 键、20× 倍镜、烟雾遮蔽 |

**与原计划的偏差**(都在代码注释里写明了):

1. **`hud._project` 返回数组 `[sx, sy]`,不是对象。** 旧 HUD 的私有投影返的就是数组
   (纯瞄环的标记直接 `scr[0]`/`scr[1]` 取用),而 `render/camera.project(out, …)` 是
   `out.x` + 布尔返回。新 HUD 的第一版把两者混用,预瞄环的坐标全变成 `undefined`——
   浏览器实测抓到并改成"内部用 `out`,对外仍返数组"。顺带说明:一个可复用对象在这里
   本就危险,一帧会同时持有两三个解算结果(环和它的框)。
2. **`core/storage.js` 是计划外新增的**(六行的 `load`/`save`):音量、呼号、辅助开关、
   环宽四处的 try/catch 原本各写一遍。
3. **`scene/arms.js` 里 `SPIN` 由 `now` 参数驱动**,模型内部不再有第二个时钟。
4. **`fx/particles.js` 的池是"空闲栈 + 年龄链表"**,不是普通环形:环会在有墓碑时丢掉
   *活着的*粒子,而链表连"满了先死最老的"这条 `shift()` 语义都保住了(三种子 × 两负载
   A/B,每帧存活数与每个实例逐字段相同)。
5. **输入层顺手修了一处**:面板打开时 `keyup` 不再被吞掉(原来面板开着的瞬间按住的
   ALT/W 会卡住 —— 实测旧版 `{alt: true, keys: [AltLeft, KeyW]}` 卡死,新版正常)。
6. **音频顺手修了两处**:`denied()` 与锁音/告警的临时节点现在在 `onended` 里成对断开
   (旧版从不),`dispose()` 第一次存在。节点图与 800 条操作日志逐行相同。
7. **`render/overlay.js` 直接写实例缓冲**:箭头的帧是"有朝向的非均匀缩放",
   `composeInto`(yaw/pitch/scale)表达不了,所以覆盖层把 20 个浮点数直接写进
   `batches.overlay[slot].data`,零分配,与旧版 `_ovlPush` 一致。
8. **`Game._camera` 保留为自由相机的钩子**:`app/frame.js` 调 `app.camera()`,
   而 `app.camera()` 再调 `this._camera()` —— AGENTS.md 里 `g.__cam` 那段调试用法
   因此原样可用。
9. **`frontend_v2/node_modules` 是指向 `frontend/node_modules` 的符号链接**(`.gitignore`
   里加了一行),过渡期不重复装一遍包。
10. **`map/index.js` 保留 `MapData` 这个名字**,`tests/frontend/height` 只改路径不改代码。
11. **声音场的时钟单位踩了一次坑(已修)。** `SoundField` 的 `push` / `update` 走的是
    `performance.now() / 1000`(秒),而 `app/frame.js` 的 `ctx.now` 是毫秒——模型那边的
    机炮转速按毫秒读,所以同一个数被两个单位用了。结果每个事件一进空气就 `age > SOUND_TTL`
    被丢掉:**整局完全没有枪炮声**,而告警(颤音、锁音)照响,因为它们绕开声场直接调。
    现在固定成 `field.update(own, viewYaw, ctx.now / 1000)`,注释写清哪个是毫秒哪个是秒。
    这是"重构里唯一一次真·回归",也说明 §11 那条"用耳朵验一遍"的检查不能省:
    `renderer.stats`、截图和测试都不会报它。

**实测时踩到的两个环境坑**(不是代码问题,记在这里免得下次再查):后台标签页里 rAF 被
限流,`screenshot` 可能抓到**上一帧的半成品**,看起来像整屏被一块巨大的三角面糊住;
判定办法是改一个 uniform(比如 `renderer.fogColor`)再等 2-3 s 重拍。以及
`Emulation.setFocusEmulationEnabled` 只让后台标签页继续出帧(约 14 fps),画面本身
仍要留时间给合成器。

---

## 1. 现状:8.7k 行、15 个文件、一个上帝对象

| 文件 | 行数 | 主要问题 |
| --- | --- | --- |
| `game.js` | 1836 | 47 个方法,一个 `constructor` 45 个字段;每帧管线、火控几何、预瞄环、威胁接收机、声音场、面板、HUD 状态全在里面 |
| `hud.js` | 1658 | 一个 `HUD` 类画 20 个区块,50 处 `ctx.font`,5 份"轨道+填充+边框",3 份分段标签行 |
| `renderer.js` | 1370 | 9 段着色器字符串、地形、道路、道具、批次、覆盖层、pass 全在一个类里 |
| `audio.js` | 946 | 距离律、节点图、合成器、5 个 cue、告警音全在一个类;**没有 `dispose()`** |
| `map.js` | 617 | 地形数据 + 两条碰撞规则(弹道/视线) + 预测 + **再导出 `fcs.js`** |
| `scene.js` | 560 | 所有单位模型 + 机炮/炮管/挂架 + 弹丸 |
| `net.js` | 398 | 线格式常量、5 类行布局、插值、运动估计、时钟/RTT 全在一个类 |
| `meshes.js` | 309 | 几何;索引顺序和 `renderer.js` 的 `DYN_CAP` 平行数组耦合 |
| `particles.js` | 237 | `list.shift()` 淘汰;`lastTrail` 被 `game.js` 直接写 |
| `input.js` | 191 | `_key()` 的 if 链就是键位表;`viewYaw` 只有 getter |
| `shop.js` / `fcs.js` / `math.js` / `icons.js` / `main.js` | 151/215/126/55/15 | 基本健康 |

打出来的 bundle **145,725 B**(`assets/bundle.js`,minified)——源文件数不是问题,
下面这些结构性毛病才是:

### 1.1 每帧的调用顺序是"代码",不是"数据"

```js
// game.js:588-594,今天的渲染管线
drawUnits(this.renderer, ...);      // 单位 + 弹丸(顺带 beginDynamic)
this.particles.render(this.renderer); // 粒子(顺带 beginParticles)
this._drawSmoke();                  // 烟雾:直接调 renderer.smoke()/sphere()
this.renderer.draw(cam);            // renderer 内部的 6 个 pass
this.hud.draw(this._hudState(own), cam.viewProj);
```

`renderer.draw()` 里又是硬编码的 `_drawSky / _drawTerrain / _setupMeshUniforms /
_drawProps / _drawDynamic / _drawParticles / _drawArrows`。于是**一帧的顺序分散在
两个文件、三处"发射"里**,而 `beginDynamic()` 属于哪个 pass 只有注释说。

### 1.2 图形管线缺四个基本概念

* **没有资源所有权**:5 个 program、11+11 个实例 VBO、2 个覆盖层 VBO、`heightTex` /
  `tileTex`、`roadVBO`、`propBatches[].vbo` —— 只有 `terrainVBO/IBO` 和 prop VBO 会被
  `deleteBuffer`。`setMap()` 调第二次(`repaintTiles` 走的是另一条路)就泄漏纹理和路网。
* **没有 VAO**:`getAttribLocation` 每帧每次 draw 查 7 次(1140/1192/1207/1225/1255/
  1265/1311),`_bindMesh()` 之后 fallback 分支又把它覆盖一遍。
* **没有材质/网格属性表**:per-mesh 行为写成 renderer 里的 `if` ——
  `uFacade = meshIndex === MESH_FACADE`(1276)、`unlit = m === MESH_PYRAMID`(1359)、
  `uOutline = m === MESH_CONE ? 2 : 1`(1081);而 `DYN_CAP[11]`(380)是与
  `meshes.js` 导出顺序平行的第二份表,新加一个 mesh 忘了改它 → `cap: undefined` →
  `push()` 静默丢弃。
* **没有状态边界**:`_drawArrows()` 改掉 `pMesh` 的 `uFogDensity=0` / `uFar=2`
  (1066-1067)后**不还原**,只靠"它最后跑"和下一帧 `_setupMeshUniforms` 撞回来;
  还在帧中间 `gl.clear(DEPTH_BUFFER_BIT)`。

### 1.3 热路径每帧分配

| 位置 | 分配 |
| --- | --- |
| `renderer.push/pushA`(944/953) | **每个实例一个** `compose(d.subarray(...))` —— 上千个 `Float32Array` 视图/帧 |
| `_submit`(1285) | 每次调用 4 个 `Int32Array` |
| `_drawProps`(1322) | 每个 mesh 一个 `cmds` 数组 |
| `_drawArrows` / `overlayOrigin` / `_ovlMat` | 每箭头 2 个字面量对象;`overlayOrigin` 每次新数组;`_ovlMat` 每次 `new Float32Array(16)` |
| `net._snapshot` | 每帧 2 个 `DataView`、1 个 `Set`、每行 2 个闭包;弹丸/烟雾每次快照重建整数组;`new Map(smoke.map(...))` |
| `hud` | 每帧 `this.hot = []` + 每按钮一个对象、`dialAt()` 3-4 个对象、`rgb()` 字符串 |
| `particles.update` | 每帧一个新 `out` 数组;满 900 后每次 `_add` 一次 `shift()`(O(n) memmove) |

### 1.4 同一个量有几份实现

* 投影:client 的 `hud._project`(hud.js:159)**和** `game._ringScreen`(game.js:1379,
  手写 4×4 乘 `hud.vp`)。
* `this.init.zero || { range: 4000, max_elev: 1.05 }` 写了 4 次(1111/1155/1180/1227)。
* "哪条手臂持有导引头":`hud._crosshairs`(340-349)与 `hud._lock`(455-460)各一份。
* 雾 + log 深度的 GLSL 块抄了 3 份(211-215 / 249-253 / 353-356);天空/太阳/雾色在
  GLSL(33)、`this.fogColor/sun`(413/416)、`this.ovlSun`(498)三处。
* 面板开关的 6 行(`uiOpen/uiKind/keys.clear/fireL/fireR/qPressed/exitPointerLock`)
  在 `_shopToggle`(1519-1527)与 `_settingsToggle`(1559-1565)各一份;关闭序列同样两份。
* 滑块三段(音量/告警/环宽)在 `_settingsClick` 里手写 3 遍(1604-1634)。

### 1.5 常量造成的假依赖

```
hud.js → audio.js   (只为 RWR_TONE_S)
hud.js → input.js   (只为 ZOOM_LEVELS)
renderer.js → map.js → fcs.js   (只要 ROAD_* 和 treeTrunks,却把火控律拽进渲染)
```
这三条边让"谁是叶子"变得不可判定,也让 `map.js` 无法独立被 node 测(今天
`tests/frontend/height` 就靠它可导入)。

### 1.6 生命周期

全工程**只有一个 `boot()`**,没有一个 `dispose()`:`audio.ensure()` 里的
`setInterval(_pump, 100)` 永不清理,`denied()` / `setLock()` 的节点从不
`disconnect`。对单人页面无所谓,但它正是"音频不是一个系统"的证据——audio 内部
没有"图的拥有者"这回事。

---

## 2. 边界

**做**:分层、拆文件、把每帧顺序变成数据、给渲染引入资源/状态/属性表的抽象、
把 `Game` 里的五块逻辑搬出去、把网络与音频各自分层、给依赖图加一条可执行检查。

**不做**(见 §13):换框架/TS/构建链、换渲染架构(仍是 instancing + `multi_draw`)、
改线格式、改任何数值/手感/HUD 视觉、worker 化、第三方 GL 库。

判据永远是同一句:**重构前后,同一机位的截图、`renderer.stats` 的
draws/instances、`--check net` 的 kB/s、`run_tests.sh js` 的用例表,全部一致。**

---

## 3. 分层与依赖规则

四层,只允许指向下一层:

```
core/    纯:常量、数学、火控律            (无 import 或只 import core)
  ↓
domain/  领域:net/ map/ audio/ render/ input/     (只 import core 与同层叶子)
  ↓
view/    组装:scene/ hud/ fx/             (import domain,不 import app)
  ↓
app/     编排:game/ frame/ systems/ panels/     (谁都 import,谁都不 import 它)
```

三条硬规则:

1. `app/` 之外**没有模块 import `app/`**;
2. 领域模块之间不互指(`render/` 不认识 `net/`、`hud/`、`scene/`;它只吃数字和数组);
3. 跨模块共享的常量住在 `core/config.js`,**不靠"从需要它的那个模块顺手 import"**。

规则 3 顺带解掉 §1.5 的三条假依赖。

**规则要能被机器查**,不靠自觉:`frontend/scripts/depgraph.mjs`(约 60 行,正则抓
`import ... from '...'`,不引第三方)——解析 `src/` 的 import 图,输出每个文件所属
层、打印环,遇到"环"或"层违规"退出码 1。挂进 `npm run check:deps`,并加进
`./dev.sh build` 之前(构建失败比截图便宜)。

---

## 4. 目标文件地图

行数是**估**;括号里是今天的来源区间。

```
frontend/src/
  main.js                       15
  core/
    config.js            ~90   (BASE_FOV/SNAP_HZ/TICK_HZ/RWR_RANGE/LEAD_*/ALARM_*/
                                WARN_*/BEAR_*/SOUND_TTL/SHAKE_*/ZOOM_LEVELS/…)
    math.js                    126   (原样)
    fcs.js                     215   (原样;仍是 FireControl.cs 的孪生)
  net/
    wire.js              ~120   (net.js 3-20 / 92-116 / 139-145 / 147-163 的纯解码)
    rows.js              ~170   (net.js 249-364:5 类行布局 + 复位 + 运动估计)
    view.js              ~110   (net.js 24-55 / 366-383:entities/projectiles/smoke/
                                contacts 的拥有者 + interpolate)
    client.js            ~120   (net.js 57-90 / 385-392:socket、调度、ack/clock)
    index.js                     20   (门面 `Net` = client + view)
  map/
    tiles.js             ~90   (map.js 9-72:tile/ROAD_* 枚举、treeHash、treeTrunks、canopyOf)
    field.js             ~110   (map.js 151-228:load/inflate/heights/tiles/roads/walls/
                                heightAt/tileAt/blocked/inside —— `height.json` 的契约)
    solid.js             ~110   (map.js 231-294:弹道规则 wallAt/trunksAt/trunkHit/
                                obstacleAt/groundHit)
    sight.js             ~150   (map.js 301-425:视线规则 sightWall/canopiesAt/canopyHit/
                                canopyBlocked/sightAt/puffBlocks/sightBlocked)
    predict.js           ~140   (map.js 427-617:predictImpact/sightPoint/unitSpheres)
    paint.js              ~30   (game.js 505-521:tiles 重绘 + trunkCache/canopyCache 失效)
    index.js                     30   (barrel;保留 `MapData` 这个名字给 tests/)
  render/
    gpu.js                ~80   (compile/program + 资源表 + `withState()` + dispose)
    meshes.js                  309   (原样搬)
    mesh-table.js         ~80   (MESH_* 索引 + 每个 mesh 的属性:cap/unlit/facade/
                                ovlKind;吃掉 renderer 的 DYN_CAP 与三个 `if`)
    shaders/
      chunks.js           ~40   (#version/precision/fog/logdepth/diffuse 片段)
      sky.js              ~25
      terrain.js          ~90
      road.js             ~25
      mesh.js            ~110
      overlay.js          ~40   (覆盖层独立 program)
    batch.js              ~90   (实例批次:直接写 Float32Array,无 subarray)
    textures.js           ~60   (heightTex/tileTex 的创建与重传)
    terrain.js           ~180   (近景 patch、远景 LOD 网格、地形 pass)
    roads.js             ~150   (路面几何 + 路面 pass)
    props.js             ~220   (道具几何、640 m 分块、道具 pass)
    overlay.js           ~140   (威胁箭头:自己的 program、自己的深度重置)
    camera.js             ~40   (makeCamera + 公开的 `project()`)
    passes.js             ~70   (PASS 列表与 drawFrame)
    renderer.js           ~180   (门面:构造、resize、setMap、stats、兼容别名)
  scene/
    palette.js            ~30
    helpers.js            ~70   (at/headAt/FORWARD/put 语义)
    units/mech.js        ~130   (scene.js 226-342)
    units/vehicle.js      ~90   (apc/wreck/base)
    units/infantry.js     ~50   (infantry/civilian/corpse/bunker)
    arms.js              ~190   (gatling/105 常量、rack、tubeOf、armModel、SPIN)
    projectile.js         ~60
    smoke.js              ~40   (原 game._drawSmoke:罐体 + 烟团)
    index.js              ~90   (drawUnits:按 etype 派发 + 每帧 emit)
  hud/
    layout.js             ~80   (常量 + dialAt/arrowRingAt + 每帧锚点对象)
    canvas.js            ~200   (字体缓存、text/bar/panel/tabRow/cursor/hover/Btn/HitList)
    reticle.js           ~180
    leads.js              ~110
    dial.js              ~190   (RWR 表盘 + 激光三角 + 烟标)
    minimap.js            ~110
    messages.js          ~110
    status.js            ~200   (顶栏/速度/模块/进度/状态行)
    overlays.js          ~120   (武器面板/死亡/结束;`_help` 已不可达,删除)
    panels/shop.js       ~230
    panels/settings.js   ~170
    gear.js               ~40
    index.js             ~150   (HUD 门面:resize/draw/hitUi/gearRect/…)
  audio/
    levels.js            ~70   (attenuation/airCutoff/reverberation/reverbLift/
                                SOUND_*/FAR_*/RWR_TONE_S —— 纯,无 WebAudio)
    graph.js             ~180   (节点图、母线、limiter/clip、噪声床、IR、dispose)
    voices.js             ~60   (声部预算与抢占)
    synth.js             ~120   (_noiseBurst/_thump/包络 + 统一的 teardown)
    cues.js              ~150   (shot/boom/explosion/denied/HEAVY_REPORT 表)
    alarms.js            ~140   (RWR 颤音、激光 6 拍、setLock)
    field.js             ~120   (声音场:声速到达 + pan/behind + TTL + 死耳朵)
    index.js             ~120   (GameAudio 门面:tick(dt)/ensure/dispose/cue)
  fx/
    dispatch.js          ~180   (原 game._fx:11 种事件 → 一个 handler 表)
    emitters.js           ~80   (弹迹、残骸烟:跟着实体的粒子发射器)
    particles.js               237   (改 pool:环形写指针 + 复用 out + lastTrail 内化)
  input/
    bindings.js           ~60   (键位表数据,取代 if 链)
    pointer.js           ~110   (监听、指针锁、滚轮/倍镜/装定)
    sight.js              ~80   (yaw/pitch/freeYaw/viewYaw/update/moveVector/fire 位)
    index.js              ~40   (门面)
  app/
    game.js              ~260   (字段、boot、start —— 只剩编排)
    frame.js             ~140   (STAGE 表 + 每帧驱动)
    boot.js              ~120   (启动页/lobby/指针锁)
    panels.js            ~150   (openPanel/closePanel 状态机 + storage 帮助函数)
    hudstate.js          ~180   (原 _hudState 拆成 5 个 builder)
    systems/
      aim.js             ~220   (原 _muzzle/_firePose/_sightEye/_sightAim/_ringSolve/_aim)
      lead.js            ~230   (原 _leadMarkers/_leadScreen/_ringScreen/_leadLayout)
      rwr.js             ~230   (原 _threats/_bearTrack/_bearRate/_alarms + rayMiss)
      camera.js           ~60   (原 _camera)
      feedback.js         ~50   (原 _checkDamage/_noLockFeedback)
      shop.js            ~110
      settings.js         ~90
```

合计约 45 个文件、8.8k 行;最大的文件 `app/systems/lead.js` 约 230 行。

---

## 5. 图形管线

### 5.1 一帧 = 一个 pass 列表

```js
// render/passes.js
// 顺序与今天的 renderer.draw() 逐条相同:sky → terrain(远景 + 近景 + 路面)
// → props → world → particles → overlay
export const PASSES = [sky, terrain, props, world, particles, overlay];

export function drawFrame(R, cam, world) {
  R.beginFrame(cam);                        // resize + clear + stats 归零
  for (const p of PASSES) p.draw(R, cam, world);
}
```

每个 pass 是一个模块,导出 `{ name, draw(R, cam, world) }`,**自己负责自己的 GL 状态和
发射**:

| pass | 内容 | 状态 |
| --- | --- | --- |
| `sky` | 全屏三角 | `withState({depth:false, depthWrite:false})` |
| `terrain` | 远景 LOD 网格 → 近景 tile patch(`uNear` 遮挡切口)→ 路面 | depth on |
| `props` | 按 640 m 分块剔除,multi_draw | depth on |
| `world` | **先 emit(单位/弹丸/烟雾/烟团)再 submit 不透明批次** | depth on |
| `particles` | 混合批次 | `withState({blend:true, depthWrite:false})` |
| `overlay` | 威胁箭头 | 自己的 program + 自己的深度重置 |

`game._drawSmoke()` 消失:`scene/smoke.js` 的发射器由 `world` pass 调用。`beginDynamic()`
不再是外部 API —— 它是 `world` pass 的第一行。

`gl` 状态不再手写 enable/restore 对,统一走 `gpu.withState({...}, fn)`;
`useProgram` / `bindTexture` / `enable` 走差分缓存。**收益不在帧时间上,在于"这个 pass
碰了什么"变成一句话。**

### 5.2 资源所有权

```js
// render/gpu.js
class Gpu {
  constructor(gl) { this.gl = gl; this._res = new Set(); }
  buffer(target, data, usage) { ... this._track(b) }
  texture(...) { ... }
  program(vs, fs) { ... this._track(p) }
  vao(...)        { ... }
  dispose() { for (const r of this._res) this.gl.deleteXXX(r) }
}
```

`Renderer.dispose()` 调 `gpu.dispose()`;`setMap()` 第二次调用时先释放上一份纹理与路网
(今天 `heightTex`/`tileTex`/`roadVBO` 会泄漏)。program 全部走 `gpu.program()`,
因此第一次有了统一的编译报错(带文件名,而不是 `src.slice(0,400)`)。

### 5.3 网格属性表取代 `if`

```js
// render/mesh-table.js —— 索引顺序仍然 load-bearing,但属性只有一份
export const MESH = { BOX:0, SPHERE:1, CONE:2, CYL:3, ROCK:4, PYRAMID:5,
                      SMOKE:6, FACADE:7, WEDGE:8, NOSE:9, HEX:10 };
export const MESH_INFO = [
  { cap: 40000 },               // BOX
  { cap: 24000 },               // SPHERE
  { cap: 24000, ovlPiece: 2 },  // CONE —— 覆盖层里白色底盖那一件
  ...
  { cap: 64, facade: true },    // FACADE
  { cap: 32000, unlit: true },  // PYRAMID:曳光弹
  ...
];
```

`uFacade` / `unlit` / 覆盖层白色底盖全从表里读,`push()` 不再有静默丢弃(`cap` 缺省即
抛错,构建期就能发现)。

### 5.4 批次:零分配写入

```js
// render/batch.js —— 直接写列,不构造临时视图
class Batch {
  constructor(gpu, mesh, cap, blended) { this.data = new Float32Array(cap*20); this.count=0; }
  put(px,py,pz,yaw,pitch,sx,sy,sz,r,g,b,a) { composeInto(this.data, this.count++*20, ...); }
  flush(gl) { gl.bufferSubData(..., this.data.subarray(0, this.count*20)); }
}
```

`math.js` 增加 `composeInto(out, off, …)`(与今天的 `compose` 共用同一个内核),
`compose()` 保留签名给测试/一次性用途。这是 §1.3 里最大的一笔分配。

### 5.5 覆盖层独立成 pass

威胁箭头的三件东西——把 `pMesh` 的 uniform 改掉、帧中间清深度、`_ovlMat` 每帧新矩阵——
合成一件事:`render/overlay.js` 拥有自己的 program(uniform 只有 `uViewProj / uSun /
uCamPos / uOutline / uPiece`),自己的实例缓冲,自己的 `Float32Array` 矩阵(常量大小的
成员,不新建)。深度重置只在这个 pass 里发生,并在文件头写清为什么。

### 5.6 投影只有一个

`render/camera.js` 导出 `project(cam, x, y, z, out)`;`hud._project` 变成调它,
`game._ringScreen` 的手写矩阵乘法删掉 —— 消灭 §1.4 的第一条重复。`cam` 仍然是
`{eye, viewProj, invViewProj, far, fov, aspect}` 这个现有形状(AGENTS.md 里
`g.__cam` 的调试用法依赖它)。

---

## 6. 单位渲染与地图渲染分文件

### 6.1 单位:发射器,不是绘制器

今天 `scene.drawUnits(R, …)` 拿到 renderer 对象,用**字符串**派发形状
(`R[shape](...)`),所以 `scene.js` 必须认识 renderer,而 renderer 的 11 个形状方法必须
和 `meshes.js` 的导出顺序对齐。改成:

```js
// scene/index.js —— etype 是 Defs.cs 的那一串,派发表就是今天 switch 的十行
const EMIT = { 1: infantry, 2: infantry, 3: apc, 4: mech, 5: base,
               6: civilian, 7: wreck, 8: corpse, 9: bunker };
export function emitUnits(batch, entities, ctx) {
  for (const e of entities.values()) { const f = EMIT[e.type]; if (f) f(batch, e, ctx); }
}
// ctx = { defs, flashes, ownId, now } —— 今天每个 emitter 各自多出来的实参
```

* 每个模型文件拿到的是 `batch`(有 `put(mesh, …)`)与快照实体,**不认识 renderer**;
* 形状是 `MESH.BOX` 常量,不是字符串(R[shape] 的 11 个薄别名保留在门面上,兼容
  `g.renderer.box(...)` 之类的控制台用法);
* `scene/arms.js` 持有 `SPIN` 与 `performance.now()`;改为由 `now` 参数传入 —— 模型代码
  里不该有第二个时钟。
* 每个模型文件 < 130 行,新增一种单位 = 新增一个文件 + `EMIT` 一行。

### 6.2 地图:三条规则,三个文件

`map.js` 今天同时是"数据""弹道规则""视线规则""预测器"。拆法按**规则**分,不按函数分:

| 文件 | 谁用 |
| --- | --- |
| `map/field.js` | 所有人:`heightAt`(三角面规则,`height.json` 的契约)、`tiles/roads/walls` |
| `map/solid.js` | 弹道:建筑的柱、树干圆柱 —— `predict.js` |
| `map/sight.js` | 视线:树冠球/锥、烟团 —— `app/systems/rwr.js`、`lead.js` |
| `map/predict.js` | 客户端弹道预测与准星落点 |

渲染侧对应三个文件:`render/terrain.js`(高度纹理 + 近景 patch + 远景 LOD)、
`render/roads.js`(从 `roads` 位图铺路面)、`render/props.js`(树/房子/石头 + 640 m
分块)。它们只 import `map/tiles.js` 与 `map/field.js`,**不再经过 `map.js` 的再导出**,
于是 `render/` 不会再拖进 `fcs.js`。

---

## 7. 网络

### 7.1 四层

```
wire.js    纯函数:有 magic 校验的帧解析,写进调用方给的 sink。可在 node 里喂字节。
rows.js    每类行的字段表 + 复位 + 运动估计(vel EMA / accel)。
view.js    entities/projectiles/smoke/contacts 的拥有者,interpolate(alpha)。
client.js  socket、JSON 调度、ack/clock/RTT/held。
```

`index.js` 把 client + view 合成今天的 `Net` 门面,`game` 侧代码基本不用改
(`net.entities` / `net.meta` / `net.interpolate` / `net.ack()` 全部保留)。

### 7.2 行布局变成一张表

今天 5 类行是 5 段手抄的 `dv.getFloat32(off + 8*k)`(net.js 269-313),每类还要手工
"先把不带字段清零"。改成:

```js
// net/rows.js
export const CLASSES = [
  { name:'mech',  bytes:74, fields:[['x','f32'],['y','f32'],…,['syaw','f32'],
                                    ['ammo','f32',2],['mag','f32',2],['weapon','f32',2],
                                    ['hpmax','f32',2],['hp','f32',2],['arm','f32',2]] },
  { name:'apc',   bytes:46, fields:[…] },
  …
];
export const ROWS = CLASSES.map(c => c.bytes);   // 与 Netcode.ROWS 同一份数字
```

一次 `decodeRow(e, dv, off, cls)` 通用循环取代 5 段。附带一个**自检**:解完所有块
后 `off` 必须等于 `HEADER + Σ`,否则抛错 —— 今天两侧行宽不一致只会表现为字段乱跳。

`*_COMPS` 那种"每类带不带某字段"的选择性清零也由字段表驱动(先按表清零,再按表填),
id 回收复用对象的坑不再靠人工维护。

### 7.3 去掉每帧垃圾

* 弹丸/烟雾/接触点改成**双槽环形缓冲**:`snapA/snapB` 两个固定数组 + 长度,解码进
  "后槽",`view` 交换指针。`net.js` 今天每次快照 `new Array(n)` + 每行一个字面量对象
  (176-193,236-249)。
* `smokeBy` 的 `new Map(smoke.map(...))` 改为复用同一个 `Map` + 按 `id` 增删。
* `wire.js` 用一个成员 `DataView`(`buf`/`byteOffset` 重挂)而不是每帧 `new`。
* `Set` 换成"代次标记":行对象上记 `gen`,快照开头 `gen++`,收尾删掉
  `e.gen !== gen` 的条目。

插值目标(`tx/tx/…`)与派生值(`mv*`/`ax/az`)今天和渲染字段挤在同一个对象上,分层后
仍是同一个对象(为了性能),但**写入者是明确的两个模块**(`rows.js` 权威、
`view.js` 派生)。

---

## 8. 音频与声音系统

### 8.1 拆法

`levels.js` 是纯的(距离律、电平表、`RWR_TONE_S`),`hud` 与 `rwr.js` 从此只依赖它 ——
`hud → audio` 那条假依赖消失(§1.5)。

`graph.js` 拥有节点图并**第一次拥有 `dispose()`**:清掉 `_pump` 的 interval、停掉
warble/beep/lock 振荡器、`ctx.close()`。

`voices.js` 是声部预算:`admit(vol, dur)` / `MAX_VOICES` / `STEAL_VOL`,单独一个文件才
可测(今天它和 `ensure()` 挤在一起)。

`synth.js` 是**唯一**创建/拆除临时节点链的地方:`_noiseBurst` / `_thump` / 包络表,
每个 voice 拿到的节点在 `onended` 里一次性断开(今天 `denied()` 与 `setLock()` 的节点
从不 disconnect,因为拆链逻辑散在三个方法里)。

`cues.js` = `shot/boom/explosion/denied` + `HEAVY_REPORT` 表;`alarms.js` = 颤音、激光
6 拍、锁音。两者都只是"配方",不碰母线。

### 8.2 声音场成为一等对象

今天"声速到达"这套逻辑在 `game.js`:`_sound`(371)/`_bearing`(389)/`_pumpSounds`(402),
它其实是**网络音频系统的下半个**——服务器按耳朵过滤 `fx`(`Netcode.hearable`),客户端
把事件放进声场,按波前到达播放、按到达时刻的朝向定 pan/behind。搬进 `audio/field.js`:

```js
class SoundField {
  push(x,y,z, level, play)                   // 事件入列(带 tMin)
  update(own, viewYaw, dt, now)              // 波前到达 → play(vol, dist, pan, behind)
  clear()                                    // 死亡/重开
}
```

`game.js` 只剩 `this.field.push(...)` 和每帧一次 `update`。`SOUND_TTL`/`EAR_NEAR`/
`_bearing` 一起搬,`game` 不再 import `attenuation/SOUND_SPEED`(§1.5 的第二条假依赖)。

### 8.3 时钟

`setInterval(_pump, 100)` 保留(**音频时钟,不是渲染时钟**;后台标签页 rAF 停了它还得
走),但由 `graph.js` 持有并在 `dispose()` 里清掉。`GameAudio.tick(dt)` 提供给
`app/frame.js`,用于声场与颤音状态的每帧推进。

---

## 9. HUD 与应用外壳

### 9.1 HUD

`hud/canvas.js` 收掉 §1.4/§1.3 里那批重复:`font(size, bold)` 缓存字符串、
`text/bar/panel/tabRow/cursor/hover`、`TAU`(取代 16 处 `6.284`)、`HitList`
(取代 `this.hot = []` + 每按钮一个对象)。5 份"轨道+填充"变 1 份 `bar()`,3 份分段行变
`tabRow()`,游标 13 行重复变 `cursor()`。

面板外壳共用 `panel()`;`overlays.js` 里删掉不可达的 `_help`(game 侧 `help: false` 常量)。

`hud/index.js` 保留 `resize/draw/hitUi/gearRect/…` 与 `vp/w/h` 字段,`hud._project`
作为兼容别名继续存在一个版本周期(AGENTS.md 记录过它)。

### 9.2 `app/frame.js`:阶段表

```js
export const STAGES = [
  ['time',     tick],            // dt/fps, 衰减计时器(消息、钱、闪烁、抖动)
  ['input',    readInput],       // 倍镜 / 装定 / 移动向量
  ['net',      netIn],           // interpolate(alpha)
  ['sound',    audioIn],         // soundField.update + trills
  ['send',     sendInput],       // 30 Hz in 消息
  ['own',      ownState],        // ownEntity / deathSpot / 面板自动关闭 / 受击与空扳机
  ['fx',       fxStep],          // particles.update / 弹迹 / 残骸烟
  ['camera',   camera],          // 舱内或死亡轨道 + 抖动;hud.resize/vp
  ['aim',      aim],             // 准星 / 弹着点 / 环解算
  ['sense',    sense],           // rwr(威胁) → lead(预瞄) → alarms → lockTone → 箭头
  ['render',   render],          // drawFrame(R, cam, world) + hud.draw
];
```

顺序与今天的 `frame()` 逐条相同(`_aim` 在 `_threats` 之前,`_leadMarkers` 因为按
快照节流所以在其后仍是同一次调用)。顺序就是文档:`AGENTS.md` 里那段"每帧"的话从此
对着这个数组读。`game.frame()` 缩成 20 行。

### 9.3 面板状态机

`app/panels.js` 提供 `openPanel(kind)` / `closePanel()`:指针锁、`input.uiOpen/uiKind`、
`keys.clear()`、两个扳机位、`qPressed`、面板互斥、`localStorage` 读写。
`_shopToggle` / `_settingsToggle` / 两个 `Close` / 三段滑块全部落到这里
(§1.4 的最后两条)。`storage.get/set` 一个帮助函数,取代 5 处 try/catch。

### 9.4 HUD 状态契约

`app/hudstate.js` 把今天 84 行、50 个键的字面量拆成 5 个 builder:
`metaState / ownState / aimState / rwrState / textState`,`hudstate()` 只做对象合并。
连带修掉每帧 3 次 `quote()`(`shopQuote` 只在草稿变化时算一次,脏标记即可)。

---

## 10. 分阶段

每步都可独立提交、独立回滚;每步结束都要过 §11 的全部检查。

| Step | 内容 | diff 规模 |
| --- | --- | --- |
| **0** | 基线:记录 bundle 大小、`renderer.stats`(draws/instances,一个固定机位)、`--check net` 的 kB/s、`--check battle --minutes 2` 的 ms/tick、3 张参考截图(战场 / 射程 / 停靠基地);数字写进本文件 §14 | 0(只记录) |
| **1** | 叶子与常量:`core/config.js` 出生,hud→input/audio、game→audio(距离律)三条假依赖断掉;删掉 `map.js` 对 `fcs.js` 的再导出;`depgraph.mjs` + `npm run check:deps` | 小,~200 行移动 |
| **2** | `render/`:着色器进文件 + `chunks.js`;`gpu.js`(资源表/withState/VAO/dispose);`mesh-table.js`(吃掉 `DYN_CAP` 与三个 `if`);`batch.js`(零分配)。**这一层改行为不变、只改结构**,renderer.js 先瘦到 ~600 行 | 中,~900 行移动+重写 |
| **3** | `render/` 拆 pass:`terrain.js` / `roads.js` / `props.js` / `overlay.js`(独立 program)/ `passes.js`;`renderer.js` 成门面;`game._drawSmoke` → `scene/smoke.js` | 中 |
| **4** | `scene/` 拆:模型按类型分文件、`arms.js`、`MESH.*` 常量取代字符串派发、`now` 传参 | 中,纯搬移为主 |
| **5** | `net/` 四层 + 行字段表 + 双槽缓冲 + 代次标记;`wire.js` 加长度自检;可选:`--check net` 落一份字节样本给 node 侧解码对拍 | 中 |
| **6** | `audio/` 七层 + `SoundField` 搬出 game + `dispose()`;`particles` 改环池 | 中 |
| **7** | `hud/` 拆 + `hud/canvas.js` 原语 + `Camera.project` 统一 | 大搬移,逻辑少改 |
| **8** | `app/`:frame 阶段表、`systems/*` 搬出 Game、`panels.js`、`hudstate.js`、`boot.js`;`game.js` 收到 ~260 行 | 大 |
| **9** | 文档:本文件补"落地结果"小节;`AGENTS.md` 的 `frontend/src/` 目录树与 Client 段;版本 patch bump | 小 |

Step 2/3 与 Step 7 是"大改但低风险"(纯渲染结构、纯 HUD 搬移);Step 5/6/8 触碰状态,
放在管线稳定之后做,因为它们才是"行为可能漏改"的地方。

---

## 11. 验证与度量

每次提交前:

```bash
cd frontend && npm run build && npm run check:deps
./run_tests.sh js                     # height.json / fcs.json 双端契约不变
cd .. && ./dotnet.sh build            # 服务端零警告(--check 用的还是同一份代码)
```

行为不变的证据(四项都必须与 Step 0 的基线一致):

| 量 | 怎么读 | 判据 |
| --- | --- | --- |
| draws / instances | HUD 底行(或 `g.renderer.stats`)固定机位 | 与基线**逐项相等** |
| 线格式 | `--check net --minutes 30 --from 8` | kB/s 与行普查完全一致(wire 一行没动) |
| 帧时间 | 浏览器 Performance 面板,固定机位 10 s 平均 | 不劣化;预期略好(§1.3) |
| 每帧分配 | DevTools Allocation sampling,战斗场景 10 s | 实例数下降一个数量级(可观测,不作硬门槛) |
| 画面 | 3 张参考截图逐像素对比(同一机位、同一 tick 附近) | 无可辨差异 |
| 玩法 | `--check battle --minutes 12` / `--check range` | 服务端没动,只作回归确认 |

另外每步都要用 `g.__cam` 那几个机位看一眼:远景地平线(雾/LOD)、城镇屋顶(facade
着色)、威胁箭头(覆盖层)、烟雾(不透明 pass)——这四样正是最容易在搬移中悄悄坏掉、
又最不会被数字抓到的。

---

## 12. 风险

| 风险 | 说明 | 对策 |
| --- | --- | --- |
| `window.__game` 调试契约 | AGENTS.md 明确记录了 `g.renderer.draw` 可被包装、`roadCount`、`propStats`、`g.__cam` 依赖 camera 形状 | 门面上这些名字**一个都不许改名**;`drawFrame` 由 `renderer.draw` 内部调用,包装仍然有效 |
| 面板/指针锁不变量 | "面板打开时鼠标是光标"今天在 4 个地方各自 enforcing | Step 8 才收拢;**在那之前**任何一步都不许动这几行 |
| 音频节拍 | 表盘闪烁(rim)与颤音(`RWR_TONE_S`)必须同拍;`_pump` 的 interval 不能改成 rAF 驱动 | 保留 interval,只换持有者;Step 6 后用 `g.__cam` 之外的听觉检查(射程打一发,看红圈闪烁与颤音是否仍同拍) |
| `map.js` 必须 node 可导入 | `tests/frontend/height` 直接 import 它;`atob`/`DecompressionStream` 只能在函数里用 | 拆成 `map/` 后保留 `map/index.js` 导出 `MapData`;不引入任何顶层 DOM 访问 |
| 模块环 | `render→map→fcs`、`hud→audio/input` 这类边拆完还可能长回来 | `depgraph.mjs` 进构建 |
| webpack 只看 `main.js` 入口 | 新增目录不影响构建;但 `clean: true` 会清 `assets/`,一次全量重建 | Step 0 记下 bundle 大小,每步对比;若 minified 增长 >5% 说明有模块边界写出了无法摇树的大对象 |
| 大搬移里"顺手改一点" | 重构最容易死在顺手优化上 | 每个 Step 单独提交,提交信息写"纯搬移 / 行为不变";任何一步出现数值差异,先回滚再定位 |

---

## 13. 明确不做

* **不换框架、不上 TypeScript、不换 webpack 配置**(`entry` 仍是 `src/main.js`,
  只是 import 路径变深)。
* **不换渲染架构**:仍是单一 `meshVBO` + 每 mesh 一个实例缓冲 + `WEBGL_multi_draw`
  (含逐命令 fallback)。不做 shadow map、不做 FBO 后处理、不做编辑器。
* **不改线格式一个字节**,不改 `Netcode.cs`,不改任何 `Defs.cs` 数值。
* **不改 HUD 视觉**(布局常量原样搬进 `hud/layout.js`),唯一例外是删掉不可达的
  `_help` 面板。
* **不做 worker / WASM / 分包懒加载**:bundle 已经 145 KB,收益不在这里。
* **不引入第三方库**(含 GL 封装、状态机、事件总线):`depgraph.mjs` 是正则 + 图遍历,
  够用。
* **不顺手修玩法问题**:拆的过程中发现的可疑逻辑(例如 `_threats` 里那段激光扫描)
  只**记录**到本文件 §15 的附录,不在同一提交里改。

---

## 14. 基线(Step 0 填,之后每一步对照)

| 量 | 旧树(基线) | 新树 | 怎么读 |
| --- | --- | --- | --- |
| `assets/bundle.js` | 145,725 B | 156,642 B | `ls -l assets/bundle.js` |
| draws / instances | 496 / 54,615 | 495 / 54,026 | 战场固定机位(eye 2979,171,8279,yaw 0.785,pitch -0.45)`g.renderer.stats`;差值是同一瞬间场上的活动体 |
| `roadCount` / `propStats` | 350,340 / 231,472 inst, 2,116 chunks | 同左,逐项相同 | `g.renderer.roadCount` / `g.renderer.propStats` |
| 装配后的着色器 | 9 段 | 9 段逐字节相同(+覆盖层自己的一对) | 打印两边的 program 源对比 |
| `--check net` / `--check battle` | — | — | 服务端未动,未测(重构范围只有客户端) |
| 参考截图 | `/tmp/old-town.png` | `/tmp/new-town.png` | 同一机位,肉眼无差异(城镇、路面、道具、雾、facade)|

---

## 15. 附录:拆的过程中发现、但不在本次改的东西

只记录,不改;每一条都是独立的小改动,值得单独一次提交。

1. `game._threats`(723-841)后半段 815-839 是一个**与威胁无关**的激光指示器扫描,
   和雷达方程挤在一个方法里。拆进 `app/systems/rwr.js` 时顺手分成两个函数,但
   判定逻辑一行不动。
2. 死代码:`game.ownHpFrac`(165)、`game.predictions`(166)、`game._aim` 里算出来又
   没用的 `fov`/`tanHalf`(1225-1226)、`constants.fov` 的第二次计算、
   `hud._help`(不可达)、`icons.iconsReady`(无使用者)、`hud.ARROW_R`(导出未用)、
   `map.groundHit`(客户端已无调用者)。删除属于清理,不属于重构,单独一步。
3. `renderer._buildProps`(740-919)对每一块瓦片走**两遍**并各算一次 `treeTrunks`;
   一遍带 tally 的写法省一半加载时间,但会动到道具布局的浮点结果,需要截图确认。
4. `Core/Shop.cs` ↔ `shop.js` 的 `quote()` 是同样的"两主机镜像",却**没有**像
   `tests/cases/fcs.json` 那样一张共享用例表。加一张 `tests/cases/shop.json` 是
   独立的一件事,收益与 fcs 那张表同级。
5. `hud._radar` 一帧内两次 `performance.now()`(797、869),所以表盘与激光三角可能
   差一帧的节拍。改成一次取样即可,属行为微调,单列。
6. `particles._add` 满池后 `list.shift()` 是 O(n) memmove;环池会顺手修掉,但若想
   保持"旧粒子先死"的观感,需要一个新的淘汰判据(按 `life` 最小),单独确认。
