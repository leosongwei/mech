# 验证：构建、测试与量测

> **读者**：维护者（跑验证的人）
> **一句话**：服务端怎么建、单元测试证什么、13 个 `--check` 各自量什么、在靶场上怎么量一个
> 机制——一页可操作的手册，读数都标着版本与命令。
> **代码**：`Program.cs`、`Harness/`、`tests/`、`run_tests.sh`、`dev.sh`、`dotnet.sh`
> **核对**：1.39.0 / 2026-11-20

## 1. 构建与重启

### 1.1 服务端

```bash
./dotnet.sh build          # 必须走这个壳：0 警告才算干净（Nullable 是开的）
./dotnet.sh run -- --port 8010
```

`dotnet.sh` 不是方便壳：dotnet 在读完工程前就要往 `$HOME` 和 `XDG_RUNTIME_DIR` 写
一次性哨兵文件和 `mkdtemp`，沙箱里两处都只读，裸 `dotnet build` 会以 `EROFS` 死在编译之前。
脚本把这些状态全部指到仓库根的 `.dotnet_tmp/`（gitignore，跨会话保留）。

### 1.2 客户端

```bash
./npm.sh install && ./npm.sh run build          # -> assets/
./dev.sh build                                   # 同上，外加共享 npm cache
```

`npm run build` 是三步：`scripts/depgraph.mjs`（分层与无环）、`scripts/syntax.mjs`（每个模块
能解析）、webpack。**前端改完只要重建**——服务端按请求读文件，不用重启；**服务端改完必须重启**，
因为 `Netcode.STATIC`（地图 + 全部定义）在启动时构建一次，那张 16 km 地图每次启动付 1.9 s
（`--check battle` 自己打印 `world built in 1903 ms`）。

### 1.3 `dev.sh` 的七个子命令

| 命令 | 做什么 |
| --- | --- |
| `./dev.sh dev` | 先建客户端，再起服务端（第一次就这么来） |
| `./dev.sh start` | 在 byobu/tmux 会话 `mech` 里起服务端（8010，日志 `/tmp/mech-server.log`） |
| `./dev.sh attach` | 接到那个会话（byobu 用 F6 脱离，tmux 用 Ctrl-a d） |
| `./dev.sh stop` | 杀掉 `mech` 与 `mech-range` 两个会话 |
| `./dev.sh status` | 列会话 |
| `./dev.sh build` | 只建客户端到 `assets/` |
| `./dev.sh range` | 起第二个世界：会话 `mech-range`、端口 8011、4 km 靶场（见 §4） |

环境覆盖：`MECH_SESSION`、`MECH_PORT`、`MECH_LOG`、`MECH_LOG_LEVEL`（默认 `INFO`）、
`MECH_RANGE_PORT`（8011）、`MECH_RANGE_LOG`、`MECH_RECORD` / `MECH_RECORD_PATH`。
`dev.sh` 恒定设 `MECH_DEBUG=1`（那是控制台调试消息的闸门，跟日志级别无关，见 §5）。

## 2. 单元测试

```bash
./run_tests.sh            # cs → js → compare，第一个失败就停
./run_tests.sh cs         # 只有服务端（tests/backend）
./run_tests.sh js         # 只有客户端（node --test）
./run_tests.sh compare    # 只比两份解表
./run_tests.sh --filter Fcs   # 按测试名子串过滤，两端都过滤
```

两边都不起服务、不装包、不碰 `assets/`：测试是 `tests/cases/` 里那张表的纯函数。

**契约**（一份输入，两端读）：

| 表 | 内容 | 证什么 |
| --- | --- | --- |
| `tests/cases/fcs.json` | 42 行，一行是一次 `solve(Shot, Track)` | 火控解：瞄准点、yaw/pitch/elevation、飞行时间，以及哪些输入该被 **拒绝**（`valid=false`） |
| `tests/cases/height.json` | 16×16 顶点、tile 16 m、容差 1e-3 | 地形面：`height_at` 是分面平面，**不是**双线性混合，折线只在那条对角线上 |

**双端同解怎么比**：每个 runner 把自己那张解表写到 `$TMPDIR/mech-fcs-<side>.json`
（服务端 `--json`，客户端 `tests/frontend/fcs/emit.mjs`），`tests/compare.mjs` 逐行逐字段比，
容差统一在 `tests/frontend/support/tol.mjs`：点 `max(0.25 m, 1e-4·d)`、角度 1e-3 rad、
飞行时间 0.02 s、命中 `0.5·半径`。**一边拒绝一边接受本身就是失败**，因为漂移先出现在拒绝边界上。
两份表可以分机跑，"这边跑、那边比"是允许的。

基线（1.39.0，`./run_tests.sh`）：`cs` 13 passed / 0 failed；`js` 44 pass；`compare`
**42 cases compared, 0 differ**，最坏点 2.10e-2 m（容差 0.25 m）、最坏角 2.39e-5 rad、最坏
飞行时间 5.09e-6 s。这几个数是"两端同一个函数"的证据，不是性能指标。

## 3. `--check` 清单

全部走同一条命令路径：`./dotnet.sh run -- --check <名字> [--minutes N] [--players N] [--from N]`。
进程内驱动一个真的 `World`：不开 socket、不看墙钟。每个 run 结尾都会 `ecs.validate()`，
所以"值比不出来"的记账错误（某行越出缓冲区）会让整跑直接失败。

| 命令 | 测什么 | 看哪个数 |
| --- | --- | --- |
| `battle --minutes 12` | 一整场脚本化战斗（4 个 bot 按同一套脚本开火、拉烟、行驶） | 末尾 `ms/tick incl. wire`、`events:` 一行的 `death` 与 `shot` 之比 |
| `MECH_PERF=1 battle --minutes 12` | 同上，但关掉逐 tick 事件扫描，改印每分钟成本 | 每分钟那一行的 `ms/tick` 与其曲线 |
| `range --minutes 2` | 靶场世界，一个飞行员每 6 s 打一发顶攻 | `shots launched`、`ecs.validate() ok` |
| `hit` | 每种武器对一辆停着的 APC（9 行 = 4 种弹 × 2 距离 + HE 火箭一行） | `after` 与 `min` 两列 hp |
| `intercept` | 每种导弹、每档距离 4 发，打在靶场的导弹防御哨位上 | `shot down` 列（32 发里几发被 30 mm 打下来） |
| `beam` | 驾束弹全链路：命中、烟幕、激光被切断、锥角、通道、涂照、信念、幽灵、弹道 | 每行 `hits` / `miss m`，末行 `profile` 的速度-距离 |
| `fcs` | 同一批场景下 AI 车组与飞行员各打一遍（`FireControl` 的集成半边） | `crew` 与 `pilot` 两列的脱靶量差 |
| `diag --minutes N` | 每 300 tick 打印 6 台 combat unit 的一行状态 | 车队在做什么：`target`、`state`、`mag`、`mem`、`pull` |
| `net --minutes 20 --from 8` | 一个脚本飞行员的下载量，按分钟 | 每分钟 kB/s、末尾 `mean` 与分项占比 |
| `tiers --minutes 14 --from 8` | 同一场战斗里兴趣球分层（6/4.5/4/3/2 km）省了什么 | `snapshot X kB/s -> Y kB/s (Z%)` |
| `drive` | 机甲推力律：功率曲线、g 上限、坡度、刹车、巡航、停坡、直角转向 | `measured` 列对 `theory` 列，末行 `all rows match the law` |
| `terrain` | 地形：分面坡度分布、高度分位、车道可驶性、`height_at` 与 `los` 单次成本 | `max`/`ceiling` 那一对、`ns/call`、`ms/call` |
| `nav` | 面片导航：区域构建、可达面积、12.7 km 越野计划、破袭队 128 m 网格、APC 过山、12 个步兵、三条车道 | `1.02x straight`、`asked/delivered`、车道行低于 10 % 极速的秒数 |
| `stuck --minutes 12` | 战场上有没有车组被下了指令却原地不动 | 每分钟的 `stuck` 列与末尾判词，外加两条 `planted` 行 |

### 3.1 逐条读法

**`battle`** —— 通行性体检：一整场跑完不抛异常、`ecs.validate()` 过、census 按分钟长起来、
钱不会发散、"杀了多少除以打了多少发"落在合理区间。1.40.0 这一跑：`2.48 ms/tick incl. wire`，
其中 0.059 ms 是 15 Hz 的线上开销；12 min 用 53.6 s CPU（403 ticks/s）；
`death=536` 对 `shot=28742`，约 54 发一个杀。同一 seed 上 1.39.0 读到 1.69 ms/tick、385 个单位，
这一跑长到 514 个——战斗进程本来就不逐 tick 重合，**这一栏按量级读，别当基线 diff**。
`--players N` 换 bot 数，`--minutes` 换时长。

**`MECH_PERF=1 battle`** —— 同一场，只关事件扫描，改成每分钟印一次：`min 4: 1.37`、`min 8: 2.49`、
`min 12: 2.38 ms/tick`（1.39.0）。战场成熟后曲线是平的，这是"这个 tick 有多贵"的唯一读法。

**`range`** —— 靶场世界的一个最小冒烟测试：能起、能打、能验。2 min 用 0.4 s（8989 ticks/s），
`shots launched: 20`。

**`hit`** —— 不是拦截，是"弹能不能到"。射手是傀儡（`crew: false`），只有视线瞄准、没有火控解，
所以无制导弹在远距离会掉在目标前：1.40.0 读到 `saclos` 1000/1600 与 `topattack` 900/1400 把
100 hp 打到 0 与 10，`rocket` 700 打到 60，而 `rpg` 400/700 与 `rocket` 1200 这一跑读到 0 伤害。
**读的是"制导弹到、无制导弹随距离变差"这个形状**，不是某一行必须是几位数。

**`intercept`** —— 一条链三件事一起看：雷达发现、引信在 2.5 m 内起爆、10 hp 对爆炸。32 发里
1.40.0 这一跑拦下 30 发（`rpg 400`、`rocket 1200`、`saclos 1000/1600`、`topattack 900/1400` 都是 4/4）。
单发窗口只有 3-4 个点射，所以这一跑接近掷硬币：**三跑一起读量级**。`MECH_IC_ONLY=rpg` 只跑一种武器。

**`beam`** —— 驾束弹最全的一页：`clear` 四档 3-4/4 命中、脱靶 0.5-1.6 m；`laser`/`window`（激光被切、
激光不可达）0/4；`cone` 视线偏 30 度再偏回来仍 4/4；`paint` 一行是"对方机身上有没有一束激光"的六种
情形；`belief`/`ghost`/`held` 是烟幕记忆与信念走位；末两行 `profile` 给出速度-距离-飞行时间和
`burst 1.3 m from the hull's centre`，是"这一发到底落在哪"的判据。

**`fcs`** —— 单元测试证两端算同一个函数，这一条证同一个函数在同一个世界里飞同一发弹。
判词是"车组不比飞行员差 2 m 以上"，两列都印出来：1.40.0 的六个 scenario 全 `ok`，两列在
1.1-8.3 m 之间（`ac30 + pod` 那一行是机组 1.3 m 对飞行员 8.3 m，无制导弹的提前量本来就是两
个人差得最远的地方）。末尾四块是走廊里放不下的：`chassis`（APC、步兵、RPG 各自的炮口）、
`arsenal`（7 套 AI 装备模板各自左右臂的发射数——主臂必须开火、副臂必须填满装填空档）、
`pull`（2.40 测成 2.40）、`smoke`（烟前 4 发、记忆点 3 发、烟后 0 发）。

**`diag`** —— 没有判词，只有观测。`target=-1`、`state=0`、`pull=0.0` 一起出现就是"这台车组既没
目标也没在动"，是查 AI 的第一站（`--check stuck` 查的是它的必然结果）。

**`net`** —— 一条连接的下载量。1.39.0，`--minutes 20 --from 8`：**mean 56.9 kB/s**，其中
snapshot 36.5（64 %）、meta 18.4（32 %）、fx 1.8（3 %）、帧头 0.2；一帧 2.44 kB × 15 Hz，
45 行 × 35 B；压缩后 83 %。末尾那块"同样一场可以装成什么"是用改 wire 之前先读一个数的地方。
`--from N` 决定统计从第几分钟开始（判别力都在成熟期）。这个数随脚本飞行员站在哪而变。

**`tiers`** —— 一张分层表值不值：1.39.0，`--minutes 14 --from 8` 读到
`126.2 kB/s -> 18.6 kB/s (15%)`，每帧留 29 行。它同时印出分层表**没**覆盖到的角落
（指挥基地、烟幕、制导弹、爆炸），改层半径之前先读这段。

**`drive`** —— 纯解析对纯实测，没有战场噪声：1.39.0 平跑 49.67 对 50.00 m/s、上坡 38.01 对 38.18、
下坡 56.78 对 57.10、180 km/h 刹停 78.4 m、12 m/s 巡航保住 11.90、停坡 10 s 位移 0.00 m、
直角 4.10 s 到 45° / 16.7 s 到正横（解析 4.07 / 16.7）。末行 `all rows match the law` 是判词。

**`terrain`** —— 地图的契约：最陡分面 0.820 对生成器上限 0.850（-3 %）、高度 p50 57 m、
三条车道各 3652/3652/2574 个采样点里 0 个超 APC 的 0.58、`height_at 68.8 ns/call`、
`los 0.021 ms/call @1000 m`。改地形生成、坡度上限或 `los` 走法之后先看这一页。

**`nav`** —— 面片导航全部：12.7 km 越野计划 17345 m（1.02× 直线，824 ms）；破袭队 128 m 网格
15625 节点里 0 个因为太陡关闭、25/25 个镇可达、11.8 ms 建完；APC 过山 106.5 s（25.8 m/s 均值）、
问 15 次绕行送 15 次；12 个步兵 12/12 到达；三条车道各 240 s 里只有 1.4 s 低于 10 % 极速，
全场 1443 次绕行。车道的读法是"低于 10 % 极速的秒数"，不是位移。

**`stuck`** —— 战场会不会停住，是任何 census 都看不出来的失败。1.39.0：两条 `planted` 行
（把机甲和 APC 直接放上最高建筑的屋顶）移到 10 m 外的空地、速度 0.00 m/s；12 分钟里
`stuck` 列全 0，判词是"没有车组被下令却原地不动 120 s"。出现 fault 时它会印一行明细
（在哪个结构里、八个方位能踩几个、坡度对自己上限多少、离基地多远）——那是去修地形的入口。

## 4. 靶场用法：先量机制，再进战场

```bash
./dev.sh range      # 会话 mech-range，http://127.0.0.1:8011，日志 /tmp/mech-range.log
```

**为什么不在战场上量**：战场上有两队 AI、三条车道、波次同时在开火、开死、开车，一个音量、
一个节点数、一次命中或一段频谱读出来主要是"飞行员当时站在哪"和"别人也在开枪"。靶场的
固定物不参与地面战（靶车从不开火，哨位只拦导弹），所以除了控制台要的东西之外没有别的东西在动、
在响。规则详见 [`../product/modes/range.md`](../product/modes/range.md)。

控制台入口（`const g = window.__game`；消息完整清单见同目录 [`browser.md`](browser.md)）：

```js
g.net.send({ t: 'debug_spawn', etype: 3, dist: 400 });         // 400 m 外一辆 APC
g.net.send({ t: 'debug_missile', wid: 5, dist: 2000, alt: 40 }); // 一发顶攻迎面来
g.net.send({ t: 'debug_trees', dist: 150, radius: 2 });        // 中间长一片树
g.net.send({ t: 'debug_god', on: true });                      // 免得 AI 打断量测
```

**顶攻记录**：靶场默认开着记录（`Modes/Range/Record.cs`），每 tick 一行 JSONL 写到
`MECH_RECORD_PATH`（默认 `/tmp/mech-range-trace.jsonl`，`MECH_RECORD=0` 关掉），带每发顶攻的
`pos` / `vel` / `proj_age` / `tgt_mem` / `tgt_vel` / `target` 与"这发锁着的车"的真值，外加当 tick 的
`boom` / `seeker` / `intercept` 事件。**没有顶攻弹的 tick 不写行**。命中点和丢锁由此从文件里读，
而不必开一跑能淹掉日志的 DEBUG。

**分工**：`--check` 是无头、可重复、跑到判词为止的回归；靶场是"这个机制在真客户端下长什么样"
的观测面（模型怎么画、烟幕什么观感、RWR 怎么响、炮口和管口在不在该在的位置）。改完一个机制，
先跑对应的 `--check` 拿到数，再上靶场看一眼，最后进战场看它是否合在一起。

## 5. 日志

| 变量 | 管什么 |
| --- | --- |
| `MECH_LOG_LEVEL` | 日志级别，默认 `INFO`。`=DEBUG` 才有武器级行 |
| `MECH_DEBUG` | **websocket 闸门**：`debug_spawn` / `debug_god` / `debug_tp` / `debug_hurt` / `debug_trees` / `debug_restart` 与设置面板的 DEBUG 区块。跟日志级别无关 |

`dev.sh` 每次都以 `MECH_LOG_LEVEL=INFO` 起服，所以**武器级的一行正常跑一条都不写**。要读的时候
明确开：

```bash
MECH_LOG_LEVEL=DEBUG ./dev.sh start      # 再 tail -f /tmp/mech-server.log
```

`mech.prox`（引信在哪儿起爆、飞过的腿、最近接近）、`mech.smoke`（导引头丢锁 / 重新锁上）、
`mech.intercept`（导弹被打下来）、击杀行，全是 `DEBUG`：它们在一场战斗里每分钟来几百条，
一次 20 分钟的脚本跑曾写过 693 行 / 148 KB，其中 95 % 是引信那一行。`INFO` 留给一局一次的事。
Microsoft 自己的日志全被过滤掉，文件保持可读。

## 6. 沙箱与常设陷阱

* **沙箱里 `/tmp` 是一个还没建的每会话目录**，tmux 建不了自己的 socket
  （`error creating /tmp/tmux-1000/default`）。先建一次，之后 `dev.sh` 照文档用：
  `mkdir -p /tmp/tmux-$(id -u) && chmod 700 /tmp/tmux-$(id -u)`
* **绝不用 `nohup ... &`、`start-stop-daemon` 或任何后台作业起服务**：工具调用会挂在那个没关的
  管道上，进程随生它的沙箱一起死。要起就起在 tmux 会话里（`./dev.sh start`），然后用
  `./dev.sh status`、`curl http://127.0.0.1:8010/`、`tail /tmp/mech-server.log` 从外面读。
* **`MECH_SEED` 只保证单线程可复现，不是基线**。一个线程做全部抽样时（`--check` 就是这种情形）
  一跑可重复；接上 socket 或某个 `run()` 被并行之后两次跑就不同。所以：
  **读量级、读判词、读曲线形状，不 diff 两次运行的具体数字**；验证靠的是量级和逐 tick 的
  不变量（`ecs.validate()`、`hp <= 0` 的唯一判官、`pos`/`vel` 的唯一写入者）。
* **空服务器是暂停的服务器**：`CONNECTIONS` 为空时 tick 循环跳过 `World.update`，只睡觉。
  从浏览器之外评审时用 `MECH_DEBUG=1` 关掉这个暂停。`GET /events/kills` 的读者是听众不是飞行员，
  它不会把世界叫醒；直接驱动 `world` 的工具（`Harness/*.cs`）压根不走那个循环。
* **两个批处理结论，都是实测出来的，别凭直觉推翻**：
  * **几何可批**：一个 tick 里所有弹的腿一次问地形（`terrain.ground_hits_many`），然后一条腿
    一条腿地判——腿是 36 m 的地形采样，几十条一起走值得批。
  * **积分不批**：把积分本身批量化（一两个分量的批量循环替代标量算术）两次实测都比替代对象更贵；
    把**单条**视线向量化也是陷阱，五个采样内联比十几次小数组操作便宜（试过，实测慢 10 倍）。
    所以要批的是"很多条独立的腿"，不是"一条腿里的几步"。
