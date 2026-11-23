# 客户端工程

> **读者**：维护者（写客户端 / 在浏览器里验证的人）
> **一句话**：`frontend/src` 怎么分层、一帧和一次绘制按什么顺序跑、three 的场景图与各层怎么
> 接起来、场景的局部坐标与放置约定，以及构建、图标、质量档这类"只影响画面"的常驻约定。
> **代码**：`frontend/src/`（`core/`、`render/`、`app/`、`scene/`）、`frontend/scripts/`、
> `frontend/webpack.config.js`
> **核对**：1.40.0 / 2026-11-23

## 1. 构建与 `assets/`

```bash
./npm.sh install                # 依赖 -> frontend/node_modules
./npm.sh run build              # -> assets/（三步，见下）
./dev.sh build                  # 两者连跑
```

* npm 一律经 `./npm.sh`：缓存、日志、`TMPDIR` 与 HOME 都指向 `frontend/.npm/`
  （已 gitignore），参数原样交给 npm，所以 `./npm.sh run check:deps` 就是
  `npm run check:deps`。一次构建在仓库外不留任何东西。
* 同一个脚本把构建放进 bwrap：整个文件系统只读，只有本仓库可写。调用方已经提供了
  这层保护时（外层 bwrap，或 `$HOME` 只读的沙箱）不套第二层；`MECH_NO_BWRAP=1` 跳过。

* `npm run build` 是三步：`node scripts/depgraph.mjs`（层规则，见 §2）→
  `node --experimental-vm-modules scripts/syntax.mjs`（把 `src` 里每个文件当模块解析一遍，
  语法错误在这里就炸，不必等 webpack 跑到一半）→ `webpack --mode production`。
  前两步各有一条 `npm run check:deps` / `check:syntax`，单独跑一条不用等打包。
* 产物落在 `../assets`（`output.path` 指过去，`clean: true` 每次先清）：
  `bundle.js`、`index.html`（`html-webpack-plugin` 从 `src/index.html` 生成）、
  `vendor/material-symbols-outlined.woff2`（§10）。`publicPath` 是 `/static/`，
  服务端在 `/` 与 `/static/*` 上发同一批文件。
* 改前端**只需要重新打包**：静态文件按请求读盘，服务器不用重启；只有改后端才要
  `./dev.sh stop && ./dev.sh start`。
* **版本号从 `MechServer.csproj` 读**：`webpack.config.js` 打开那份工程文件，取它自己的
  `<Version>`（读不到就直接抛错，构建失败），作为 `templateParameters.version` 交给
  模板，`src/index.html` 打印成 `<p class="ver">v<%= version %></p>`。所以启动页上那个
  数字是这次构建真的读到的值，`frontend/package.json` 的 `version` 与产品无关。

## 2. 四层，一个方向

`frontend/src` 是四层，边只能指向自己这层或更低的层，`scripts/depgraph.mjs` 在构建里检查，
违规就 `exit 1`：

| 层 | 目录 / 文件 | 是什么 |
| --- | --- | --- |
| 0 | `core/` | 纯函数：常量、矩阵与角度、火控定律、localStorage 包装 |
| 1 | `net/` `map/` `audio/` `render/` `input/` | 领域层：线上格式、地图与碰撞、声音图、three 与各层、输入 |
| 2 | `scene/` `hud/` `fx/` | 视图层：单位模型、HUD、粒子与事件分发 |
| 3 | `app/` | 应用层：`game.js`、`frame.js`、`systems/`、`panels.js` |
| 4 | `main.js` | 引导：造 `Game`、挂 `window.__game`、`loadIcons()`、`boot()` |

* `icons.js` 与 `shop.js` 是两个没有目录的叶子，被当作第 0 层。
* 领域层内部只允许一条跨模块边：**`render -> map`**（地形与道路 pass 读高度场和地块位图）。
  其余领域模块的依赖留在自己目录里。
* 任何闭合的环都是错误，与层号无关。脚本用一条 `import … from '…'` 的正文正则找边，
  只看相对的 `.js` 说明符（`icons.json`、`.woff2` 这类资源不算边），所以它零依赖。
* 跑 `node scripts/depgraph.mjs` 会顺手打一张"每个模块几个文件几行"的表，改完结构先看它。

## 3. `src/` 每个目录管什么

| 目录 | 职责 | 关键入口 |
| --- | --- | --- |
| `core/` | 共享数字与纯函数 | `config.js`（客户端常量）、`math.js`（`compose` / `composeInto` / `dirFrom`）、`fcs.js`（§11） |
| `net/` | 二进制与 JSON 的解析、插值后的图 | `client.js`（`send` / `meta` / `entities` / `contacts` / `ack`）、`wire.js`、`rows.js`、`view.js` |
| `map/` | 地图数据与两条碰撞规则 | `field.js`（`MapData` / `heightAt`）、`solid.js`（弹）、`sight.js`（眼）、`predict.js`（客户端弹道预测）、`tiles.js`（地块词汇、树）、`paint.js` |
| `audio/` | 声音图与声部 | `index.js`（`GameAudio`）、`graph.js`、`field.js`（声音场）、`cues.js`、`alarms.js` |
| `render/` | 设备、场景图、各层 | `index.js`（`Renderer`、`ORDER`）、`instances.js`、`primitives.js`、`materials.js`、`ground.js`、`roads.js`、`props.js`、`sky.js`、`overlay.js`、`textures.js`、`camera.js`（§5、§7） |
| `input/` | 指针、键位、机身系摇杆 | `index.js`、`sight.js`（`moveVector`、`viewYaw`）、`bindings.js`、`pointer.js` |
| `scene/` | 单位模型，从基本体搭 | `index.js`（`emitUnits` / `emitProjectiles` / `emitSmoke`）、`helpers.js`（§6）、`arms.js`（管阵） |
| `hud/` | 全部 2D 绘制 | `index.js`（`draw` / `_project`）、`layout.js`、`canvas.js` |
| `fx/` | 事件流与粒子池 | `dispatch.js`、`particles.js` |
| `app/` | 帧、系统、面板 | `game.js`（暴露面）、`frame.js`（§4）、`systems/`（`aim` / `lead` / `rwr` / `camera` / `belief` / `feedback` / `settings` / `shop`）、`panels.js`、`hudstate.js` |

## 4. 一帧是 11 个阶段

`app/frame.js` 的 `STAGES` 就是顺序，`frame()` 从 `requestAnimationFrame` 进来逐个跑：

| 阶段 | 做什么 |
| --- | --- |
| `time` | 帧时钟、消息与赏金弹字的寿命、受击闪与抖动衰减 |
| `input` | 读取本帧输入、光学档与平滑后的倍率、调零值 |
| `net` | 把最新快照插值进画的这一帧（EMA，时间常数 14） |
| `sound` | 声音场：波前到了谁那里就播谁（用实时位置与 `viewYaw`） |
| `send` | 30 Hz 输入消息（含 `st` 这个 ack，RTT 与拥塞都靠它） |
| `own` | 自己那台车：位姿、死亡点、整备面板与受伤判定 |
| `fx` | 粒子、枪口闪、导弹拖尾、残骸冒烟 |
| `camera` | 座舱眼或死亡环绕；HUD 的视口跟着它 |
| `aim` | 视线、中心准星、每臂的落点记号 |
| `sense` | 告警器 → 预瞄圈 → 两支铃音；视线信念点 |
| `render` | 发实例、画世界、再画 HUD |

* `send` 排在 `own` 前面，所以它问 `ownEntity()`（直接查实体表）；`sense` 排在 `aim` 后面，
  因为预瞄框画在告警器已经定位的目标上，箭头也挂在这台车自己的眼上。
* 一个阶段只读写 `app` 和共享的 `ctx`（`own` / `cam` / `dt` / `now`），把一段抽掉或插进来
  都不碰邻居。新增"每帧要做的一件事"就加一行。

## 5. 绘制顺序是 `render/index.js` 的 `ORDER`

场景图是 one scene、六个层，层的先后写在那张 `ORDER` 表里，落成每个 mesh 的
`renderOrder`——"哪一层先画"是拥有那一层的模块的属性，不是绘制函数里的一行顺序：

| 层 | 画什么 | 深度 |
| --- | --- | --- |
| `sky` | 一个全屏三角形，`uInvVP` 反向投影（`sky.js`） | 不测不写 |
| `ground` | 远 LOD 网格 + 近地块补丁（`ground.js`，§8） | 测、写 |
| `roads` | 贴地的沥青（`roads.js`，§9） | 测、写 |
| `props` | 树、房子、石头；按 640 m 分块，静态（`props.js`） | 测、写 |
| `bodies` | 会动的一切：车、零件、弹体、烟罐 | 测、写 |
| `particles` | 冲击粒子，质量档决定混合还是抖动（§10） | 测、不写 |

* `Renderer.draw(cam, emit)` 的顺序是：写本帧的 uniform（§7）→ 把 `emit` 交给模型
  （`emitUnits` / `emitProjectiles` / `emitSmoke` / `particles.emit`）→ 发布实例 →
  两个 cull（§7）→ `device.render(scene, camera)`。
* 告警箭头是**第二个 scene**：它画在 HUD 自己的像素里，在世界之后、清掉深度缓冲再画一次
  （§7）。所以它是整帧的第七个动作，而不是第六层的兄弟层。
* 车在树前面还是后面属于同一道题，所以 `bodies` 与 `props` 分开只为 cull 方式不同
  （静态分块 vs 每帧写），深度把它们排在一起。

## 6. 场景的局部坐标：+X 前、+Y 上、+Z 右

`core/math.js` 的 `compose` / `composeInto` 写出的列主序矩阵就是这个朝向：局部 +X 是前方，
+Y 是上，+Z 是右。一个 Y 轴的基本体要躺到炮管上，就用 `helpers.FORWARD`（`-PI/2`）当 pitch；
pitch 已经在手上时用 `FORWARD + p`。`b.put(PRIM.BOX, p, yaw, pitch, sx, sy, sz, …)` 的缩放
三分量对应 **sx 沿炮口、sy 竖直、sz 横向**——最后两个写反就是一根躺下的吊舱。

放置一块零件有两条路，`scene/helpers.js` 各给一个：

* **`at(b, mesh, e, yaw, pitch, ox, oy, oz, …)`**：把偏移按**同一个 yaw** 转出去
  （`x = e.x - sin·ox + cos·oz`，`z = e.z + cos·ox + sin·oz`），适合钉在车身上的东西——
  炮塔、管阵、轮子、腿。
* **`headAt(b, mesh, e, hullYaw, syaw, spitch, right, up, fwd, …)`**：观瞄塔的专用路。
  塔的枢轴由**车身 yaw** 定（`SIGHT_Z`，与 `defs.MECH_SIGHT_Z` 同一个数），只有从枢轴
  出发的偏移（`right` / `up` / `fwd`，含俯仰）跟着 `syaw` / `spitch` 转——塔是绕柱子转，
  而绕着车公转就错了：把视角的角传给 `at` 画出来，头和柱子能差出一米半。`meshPitch`
  留空时网格的俯仰就取 `spitch`。
* `SIGHT_Y`（5.05，与 `defs.MECH_EYE` 同一个数）是塔的光轴高度，座舱眼正坐在那里，
  所以自己的观瞄头不进绘制列表（`scene/units/mech.js` 的 `own`），别人的都画。

## 7. 实例、three、屏幕空间叠层

客户端用 **three.js**（`frontend/package.json` 里唯一的运行时依赖）做设备、场景图与绘制调用，
但每个 program 都是项目自己的 GLSL 3.00 源码（`render/shaders/`）：这个游戏的画面是一组
具体的方程——对数深度、雾、平面着色的地面、抖动过的粒子——用 stock 材质反而要把它们重建
一遍。three 负责的是管道：上下文、program 缓存、attribute 绑定、状态切换、draw call。

* `render/primitives.js` 把十一个基本体建成 `BufferGeometry`，并带上每个基本体的两个事实
  （`facade` 画窗户、`unlit` 不参与光照）与它的 `cap`。基本体有一份顶点缓冲，
  同一个基本体的每一层（不透明 / 混合）只多一条实例缓冲。
* `render/instances.js` 的 `Layer` 是一条实例缓冲：每条实例 20 个 float（16 矩阵 + 4 颜色），
  一个 `InstancedInterleavedBuffer` 同时喂 `iMatrix`（mat4）与 `iColor`（vec4）两个属性。
  `put` 直接写进 `count * STRIDE_FLOATS`；`composeInto` 就是为了这条路存在——早先每条实例
  建一个 `Float32Array` 视图，是客户端最大的每帧分配。
* 每层按基本体各有一个 `cap`，写满就丢（`slot()` 返回 -1），所以一帧的实例数有上限；
  `reset()` 只在帧首把计数归零，buffer 不重建。上传只传用到的区间
  （`addUpdateRange(0, count * STRIDE_FLOATS)`），所以四十个 box 就只上行四十个 box。
* `props.js` 的静态实例一层一个 640 m 块（`StaticLayer`），几何的 `boundingSphere` 就是那一块，
  于是 cull 有两条：`update(cam)` 的距离盒（4600 m）与 three 自己的视锥剔除。
  `mesh.visible` 说的是"这一帧画不画"，实例数据一次也不重传。
* `overlay` 层画告警箭头，用的是世界自己的基本体，只是把 `uViewProj` 换成 HUD 自己的矩阵
  （`renderer.overlayOrigin` / `overlaySun` / `screenArrow`）。三件事是实测定的：透视除法的
  原点取环心（也是每支箭的尾），否则有深度的箭头会斜着抹开；这一趟清并测深度缓冲，因为
  锥底会穿过自己的侧面；`uFar = 2` 把 shader 的对数深度压成 `log2(w)`，正好对 1~1.1 的 w
  单调。整层一个矩阵，所以三支箭仍是一条实例缓冲里的六条实例。
* 一次 `Layer` 的顶点缓冲属于基本体、被它的每一层共用，所以释放一层时要先把共用的属性从
  几何上摘下来（`Layer.dispose`），否则会连带删掉别的层还在画的缓冲。

## 8. 地形两遍

`render/ground.js`：远景是**一张径向 LOD 网格**，随眼在整块地块上重新居中，高度在顶点
shader 里取纹理，16 km 的地图一次也不上 CPU；近景是**一块跟着眼走的地块补丁**
（`TILE_PATCH` = 48 块地，即 96×96 格），每个顶点都是地块角，所以它的面就是地块的面，
OpenTTD 那种坡面才读得出来。远程序用 `uNear` 把补丁那块矩形从自己身上 `discard` 掉，
补丁再盖上去——两种分辨率是交叠的，不是拼接的。补丁自己编成 `PATCH`（`shaders/ground.js`），
不带这条 `discard`：它的边界就是那条切缝，一个共享的 `uNear` 只给远程序读。

`TERRAIN_LOD` 四档随光学倍率（`renderer.setZoomLevel`）：

| 档 | `g` | `k` | `a0` | 含义 |
| --- | --- | --- | --- | --- |
| 1x | 26 | 174 | 48 | 768 m 内一块地一格，网格到 17.6 km |
| 4x | 44 | 193 | 62 | 1 km，8 km |
| 10x | 80 | 247 | 109 | 1.7 km，6 km |
| 20x | 64 | 267 | 188 | 3 km，5 km（雾里，看不出来） |

`a0` 环内的地块间距不合并（一块地一个 quad），之后每 `g` 环间距翻倍。每个顶点在 shader 里
**吸附到本环间距的世界网格**上：跟着眼连续滑动的网格每帧重采高度场，远山就会爬。
碎片颜色用屏幕导数求平面法线，所以一块地/一个坡就是一面，远场的地缝在屏幕空间里画、地块
小于几像素时淡出。高度是 `R32F` 纹理、地块是 `R8UI` 纹理，都由 `textures.js` 上传。

## 9. 贴地的道路

`render/roads.js` 从 `map.roads` 的位掩码建几何：每个方位位一块 quad（直路两块、拐角两块、
十字四块），两条 45° 位各一条斜带。每块多边形先用 `clipHalf` 沿**该地块自己的地形对角线**
切一刀，两半各投到所属的那个三角面上——不切的话一个三角形跨在两面之间，会沉到地面以下，
那正是路肩上闪动的细条。`LIFT` 0.06 让沥青微微浮在草上，半宽 5 m 即 10 m 路面；
`map.heightAt` 与服务器 `Terrain.height_at` 是同一条两三角规则，所以路面顶点落在补丁画的
那个平面上。几何每张地图建一次，由 `roads` 层在地面之后、道具之前画。

## 10. 图标与粒子质量档

* **图标**：`frontend/icons.json` 是唯一事实来源（`name` + `codepoint`），`src/icons.js`
  用 `String.fromCodePoint` 生成 `ICON` 表，并把 vendored 的 Material Symbols 子集用
  `FontFace` 注册（webpack 当资源拷贝到 `vendor/`）。加一个图标 = 在 `icons.json` 加一条 +
  跑 `scripts/fetch-material-icons.sh` 重新子集化（产物几个 KB，进仓库）。字形没落地时
  `drawIcon()` 返回 `false`，调用方保留自己的基本体画法顶最初几帧。
* **质量档**：设置面板里的 `EFFECTS` 写 `app.quality`，`systems/settings.js::setQuality`
  同时写 `renderer.quality` 与 `localStorage`（`QUALITY_KEY`，`core/config.js`）。
  混合层有两个材质（`materials.js::bodyMaterial`）：`quality` 是历来的样子——alpha 混合、
  不写深度；`performance` 把同一批实例编上 `DITHER`，按不透明画，每个片元用逐像素噪声
  （交织梯度噪声）整片留下或整片丢掉。一个体的透明度变成一片斑点的密度，丢掉的像素由后层填上；两档都不写粒子的深度，
  所以层的先后仍是粒子池发出的顺序，斑点是混合的无偏图像——同样的亮度、同样的轮廓。
  `discard` 花 early-Z，这一层本来就吃不了带宽，所以这一档在独显上是一种观感，
  而不是一档帧率。

## 11. 火控只有一份，在 `core/fcs.js`

`core/fcs.js` 与服务器 `Core/FireControl.cs` 是同一个定律的两份实现：`solve` 接一发
（炮口、眼、炮、调零）与一条 track，答出视线该压在哪个点、两个角、管子离管时的仰角与飞行
时间；`leadPoint` / `zeroAt` / `solveElevation` / `flightTo` 是它拆开的那几步。它是纯函数，
没有 `World`、没有 ECS、没有随机数，所以在浏览器、Node 和测试里都能跑。

* `map/` **不转出**它：`map/index.js` 只管地图数据、两条碰撞规则与预测，火控由
  `app/systems/aim.js` 与 `app/systems/lead.js` 直接从 `core/fcs.js` 取。
* 两端一致靠一张用例表 `tests/cases/fcs.json`（`./run_tests.sh` 比对两份解），
  怎么跑见 [checks.md](checks.md)；定律本身的说明在
  [../product/systems/firecontrol.md](../product/systems/firecontrol.md)。
* 客户端另有自己的两条规则：弹道预测在 `map/predict.js`（`sightPoint` / `groundHit`），
  视线在 `map/sight.js`（`sightBlocked` / `canopy*`）——它们必须和服务器同解，否则准星上
  的预测点和真实落点会分开。玩家看到的那几支记号见
  [../product/hud/aim.md](../product/hud/aim.md)。

## 12. 相关页

* 在浏览器里验证、自由相机、截图：同目录 [browser.md](browser.md)
* `--check` 与基线读数：同目录 [checks.md](checks.md)
* 一 tick 的服务端顺序、两端同解的分工：同目录 [pipeline.md](pipeline.md)
* 屏幕上的记号、HUD 与文案：[../product/hud/hud.md](../product/hud/hud.md)、
  [../product/hud/aim.md](../product/hud/aim.md)
* 声音链路：[../product/systems/audio.md](../product/systems/audio.md)
* 线上的帧格式与插值：[../product/systems/net.md](../product/systems/net.md)
