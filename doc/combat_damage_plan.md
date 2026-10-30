# 伤害 / 死亡 / 爆炸 / 残骸 重构设计

状态：**已实现**（一次性落地，不分阶段）。本文既是设计，也是实现记录。
范围：`world.py` 的伤害与死亡路径、`systems/projectiles.py` 的命中结算、
`systems/proximity.py`、新的 `combat.py` / `systems/blasts.py` /
`systems/mortality.py`、`defs.UNIT_DEFS` 的每类型数据、`hp` 组件布局与快照拼装。
不改：玩法数值（除 §9 的一处 tick 延迟）、武器表、AI 瞄准、模式、经济、客户端代码。

---

## 0. 一句话

把「**扣血**」「**判死**」「**面积伤害**」「**留残骸**」从 `World` 里几个带 `etype`
分支的大函数里拆出来：

- **扣血** = `combat.py` 里的一个函数，谁打中谁调，就地生效（不是系统，没有队列）；
- **判死** = `systems/mortality.py`，唯一判据 `hp <= 0`；
- **面积伤害** = `systems/blasts.py` + 一个 `blast` 实体；
- **残骸** = `defs.UNIT_DEFS[...]["death"]` 三元组，`mortality` 按数据造。

血量布局同时收干净：**`hp` 是标量**（唯一的血池，唯一的死法），
只有机甲在它旁边多带一对 `arm_hp`。常规单位身上不带任何"多血池"的东西。

---

## 1. 目标与非目标

### 目标

1. **删掉伤害路径上的类型开关**。改前：`world.damage` 有 `if etype == MECH`，
   `_leave_debris` 有四路 `elif etype ==`，`kill` 里 `etype == BASE`，
   `_death_blast` 写死读 `defs.DEATH_BLAST`，`mathutil.unit_spheres` 五个
   `if etype ==`，`weapons.run` / `ai._aim_and_fire` / `locking.run` 各一处
   `etype == MECH` 只为查臂的血。改后：伤害路径上零个。
2. **一个写者一条规则**。`hp` 只由 `combat.apply` 写；死只由 `mortality` 判；
   面积伤害只由 `blasts` 结算；残骸只由 `mortality` 造、`debris` 收。
3. **单位类型可以大量增加而不碰这些系统**。新加一个会炸、会留残骸、能结束比赛的
   单位 = 往 `UNIT_DEFS` 加一条，`world.py` / `combat.py` / 三个系统一行不改。
4. **行为等价**。同一场战斗在同一随机种子下，新旧两版走到同一个结局（§11 有实测）。

### 非目标（明确不做）

- 不做通用的「伤害类型 / 抗性」体系。现在只有穿透 + 爆炸两半，
  公式仍在 `defs.pen_damage / blast_damage / hit_damage`。
- 不做伤害队列、不做 `damage` 系统（理由见 §3.4）。
- 不做护盾、能量、持续伤害、部位独立动画（掉臂的模型 `scene.js` 里已经有了）。
- **不改客户端**：`UNIT_DTYPE.hp` 仍是 4×u2、索引 `MOD_*`，残骸 `flags` bits 4-5
  不变，`boom` fx 事件字段不变。
- 不改三条**比赛规则**：跨阵营才给钱、死人写 `death_pos` 并问 mode 要
  `respawn_delay`、`ends_match` 判胜负。它们是规则，不是单位属性。

---

## 2. 现状与去向

| 改前 | 位置 | 问题 | 改后 |
| --- | --- | --- | --- |
| `hp` / `hpmax` `f32(4,)` 挂在所有单位上 | `components.py` | 步枪兵带着 3 格永不使用的血池 | `hp`/`hpmax` **标量**（全员）+ `arm_hp`/`arm_hpmax`（只机甲） |
| `World.damage` | `world.py:266` | 减血 + 判死 + 清臂弹 + 调 `kill` 四合一的 `etype` 分支 | `combat.apply` 只减血；`mortality` 判死 |
| `World.hit / splash` | `world.py:240 / 253` | 现查 `UNIT_DEFS[etype]` 拿护甲 | `combat.hit / combat.splash`，护甲从 `armor` 组件读 |
| `World.kill` | `world.py:298` | 内联 `blast` 判定、`BASE` 判胜负、三种事件、残骸、殉爆 | `mortality._kill`，配方从 `defs` 读 |
| `World._death_blast` | `world.py:347` | 写死 `wd = defs.DEATH_BLAST`，靠"先解全部距离再落地"防自伤 | 殉爆 = `blasts.spawn`，下一 tick 结算 |
| `World._leave_debris` | `world.py:377` | 4 路 `elif etype ==` | `death["wreck"]` 三元组 |
| `projectiles._impact` | `projectiles.py:328` | 直击 + 半径内逐个 `splash` 循环 | `projectiles.impact`：直击 `combat.hit` + 造 `blast` |
| `proximity.run` | `proximity.py:105` | 借 `projectiles._impact` | 同上（`impact` 现在是公开入口） |
| `unit_spheres(etype,…)` | `mathutil.py:108` | 5 个 `if etype ==`，每 tick 每单位一次 Python 循环 | 几何进 `defs.spheres` + `sphere_geom` 缓存 + `sphere_world` 向量化 |
| `World.last_hit` dict | `world.py` | 非 ECS，销毁时要手动 pop | `last_hit` 组件 |
| `shop.arm_state` / 维修 | `shop.py` | 按 `MOD_*` 索引读 4 槽 | 读 `arm_hp` / `arm_hpmax`；维修 = 车体 + 双臂 |
| `netcode` 的 hp 列 | `netcode.py:102` | `e["hp"][uids]` 直取 4 列 | `hp_columns()` 由标量 + 臂池拼 4 列（线上不变） |

### 管线

改前：

```
rearm, player_input, [mode pre = ai], gunnery, locking, smoke, weapons,
movement, projectiles, proximity, economy, debris, [mode post = waves]
```

改后（`world.py:_build_pipeline`）：

```
rearm, player_input, [mode pre = ai], gunnery, locking, smoke, weapons, movement,
projectiles,      # 飞行 + 碰撞；直击 → combat.hit()；命中点造 blast
proximity,        # 引信：把弹丸搬到爆点，调 projectiles.impact
blasts,           # 面积伤害 → combat.splash()；blast_t -= dt，到零销毁
mortality,        # hp <= 0 → 判死、发 boom、记账、留残骸、造殉爆、判胜负
economy, debris, [mode post = waves]
```

`mortality` 必须在所有伤害源之后（`blasts` 之后）、mode post 之前
（死车不该再替阵营占镇子一个 tick）。

---

## 3. 组件

### 3.1 血量：标量 + 机甲专属的臂池

| 组件 | dtype / shape | 谁有 | 含义 |
| --- | --- | --- | --- |
| `hp` | `f32 ()` | **全员** | 唯一的血池。`hp <= 0` 就是死 |
| `hpmax` | `f32 ()` | **全员** | 出生时 = `defs.UNIT_DEFS[...]["hp"]` |
| `arm_hp` | `f32 (2,)` | 只多血池机体（机甲） | 左/右臂池，索引 = 武器臂编号 |
| `arm_hpmax` | `f32 (2,)` | 同上 | 各 50 |

"谁有臂池"由**工厂**决定，不由 tag 决定：`defs.UNIT_DEFS[...]["arms"]` 存在时
`spawn_unit` 把 `components.ARM_COMPS` 加进组件集。于是每个系统的判据都是
**组件在不在**（`"arm_hp" in bufs` / `e.has(eid, "arm_hp")`），不是类型。

命中球保留 `MOD_*` 词汇（0 车体 / 1 躯干 / 2 左臂 / 3 右臂），映射到存储只发生在
机甲专属的那 8 行里：

| 命中 `module` | 记到 |
| --- | --- |
| `MOD_CHASSIS` (0) | `hp` |
| `MOD_TORSO` (1) | `hp`（躯干只是命中球，没有池） |
| `MOD_LEFT` (2) | `arm_hp[0]`（已归零则 `hp`） |
| `MOD_RIGHT` (3) | `arm_hp[1]`（同上） |

三个简化：

1. **死亡判据对全体就是 `hp <= 0`**，一行、无掩码、无例外。
2. **臂编号 ≡ 武器编号**，`combat.arm_alive(eid, arm)` 就是 `arm_hp[eid, arm] > 0`。
3. 线上仍是 4 列 `[hull, torso, left, right]`，由快照拼装（§8）。

### 3.2 新增（全员）

| 组件 | dtype / shape | 默认 | 含义 | 谁读 |
| --- | --- | --- | --- | --- |
| `armor` | `f32 (2,)` | 0.0 | `[dp, de]`，出生时从 `UNIT_DEFS` 拷入 | `combat.hit / splash` |
| `last_hit` | `i32 ()` | -1 | 打出致命伤害的 player id | `mortality` |

`armor` 组件化只有一个理由：热路径不再查 `UNIT_DEFS[etype]` 字典。
`last_hit` 替掉 `World.last_hit: dict`，随实体销毁自动消失。

### 3.3 新增（爆炸实体，只有爆炸拥有）

| 组件 | dtype / shape | 含义 |
| --- | --- | --- |
| `blast` | tag | 「这个实体是一次爆炸」 |
| `blast_power` | `f32 (2,)` | `[ed, er]` |
| `blast_t` | `f32` | 剩余寿命；`<= dt` = 单 tick 爆炸 |
| `blast_friendly` | `u8` | 0 = 只伤敌军（武器），1 = 不分敌我（殉爆） |
| `blast_skip` | `i32` | 排除的实体（被直击的那个），-1 = 无 |
| `blast_pid` | `i32` | 战功归属，**创建时解析好** |

```
BLAST_COMPS = ["pos", "faction", "blast_power", "blast_t", "blast_friendly",
               "blast_skip", "blast_pid", "blast"]
```

### 3.4 明确不加的东西

记在这里防止以后又加回来。

| 否掉的 | 为什么 |
| --- | --- |
| `body_vital`（致命槽掩码） | 死亡规则对全体就是 `hp <= 0`。机甲不致命的东西压根不是致命池 |
| `body_pool` / `body_arm`（模块映射表） | 臂编号 ≡ 武器编号，映射退化成 4 行机甲专属代码 |
| `hp` 保持 `f32(4,)` | 让常规单位带 3 格恒为 0 的血池，是线上布局的历史包袱，不是语义（线上仍是 4 列，但那是拼出来的） |
| `hs_local` / `hs_r` / `hs_mod`（命中球组件） | 每单位 68 字节只为挪走一个 switch。用按 etype 缓存的 numpy 数组解决（§6） |
| 伤害队列 + `damage` 系统 | 系统拥有状态；伤害是"请求 → 写 hp"的纯函数，没有自己的状态。要当系统就得先造一个队列给它消费，而队列买到的（顺序解耦、批量向量化）在每秒几十条请求的规模上都不值，代价是 hp 晚一个 stage 落地、多一个调试维度。**就地扣血更诚实** |
| 通用「伤害类型 / 抗性」表 | 现在只有穿透 + 爆炸两半，没有第二个使用者之前不建 |

### 3.5 保留不动

- **tag 体系**：`unit` / `combat` / `invulnerable` / `person` / `lockable` 一个不动。
  （`arms` 不是 tag —— 组件集由工厂按 `defs` 里的 `arms` 数据决定。）
- **残骸是另一个实体**：`DEBRIS_COMPS` 没有 `unit` tag，所以 `world.units()`
  永远看不见它，不需要任何排除列表。残骸的 `hp` 也跟着是标量（值 0）。
- **线上 `hp` 仍是 4×u2**，索引 `MOD_*`。
- `defs.MOD_*` / `MODULE_NAMES` / `MECH_HITSPHERES` 保留（后者同时进 init payload
  给客户端画命中反馈）。

---

## 4. 每类型数据（`defs.UNIT_DEFS`）

```python
ETYPE_MECH: dict(
    name="MECH", hp=100, arms=(50, 50), dp=10, de=20, ...,
    splat=DEATH_BLAST["er"], spheres=tuple(MECH_HITSPHERES),
    death=dict(blast=(DEATH_BLAST["ed"], DEATH_BLAST["er"]), loud="blast",
               wreck=(ETYPE_DEBRIS, WRECK_TIME, 2)),
    tags=("unit", "combat", "lockable", "moves")),
ETYPE_APC: dict(
    name="APC", hp=100, dp=10, de=20, ...,
    spheres=((MOD_CHASSIS, 0.0, 1.1, 0.0, 1.7), (MOD_CHASSIS, 0.0, 2.4, 0.0, 1.2)),
    death=dict(blast=(DEATH_BLAST["ed"], DEATH_BLAST["er"]), loud="blast",
               wreck=(ETYPE_DEBRIS, WRECK_TIME, 1)),
    ...),
ETYPE_BASE: dict(name="COMMAND BASE", hp=4000, dp=80, de=20, ...,
                 death=dict(loud="big", ends_match=True)),      # 不留残骸
ETYPE_INF_RIFLE: dict(name="RIFLEMAN", hp=10, dp=1, de=1, ...,
                      death=dict(loud="boom", wreck=(ETYPE_CORPSE, CORPSE_TIME, 0))),
ETYPE_BUNKER: dict(name="BUNKER", hp=999999, dp=1000, de=1000, ...,
                   death=None, tags=("unit", "invulnerable")),
```

`death` 字段的语义：

| 键 | 含义 |
| --- | --- |
| `blast=(ed, er)` | 原地殉爆，不分敌我（`systems/blasts.py`）；缺省 = 不炸 |
| `loud="blast"/"big"/"boom"` | 客户端音效与震屏档，也决定 `boom` 事件的 `big` 位 |
| `wreck=(etype, 秒, 线上 kind)` | 留下什么；缺省 = 什么都不留 |
| `ends_match=True` | 这个死了就判胜负（基地） |

**删除**：`defs.DEATH_BLAST_ETYPES`。
**保留**：`DEATH_BLAST`（`death["blast"]` 的值来源）、`CORPSE_TIME` / `WRECK_TIME`
（`death["wreck"]` 里引用，`debris.py` 的注释仍然成立）、`MECH_HITSPHERES`。

`REPAIR_COST_PER_HP` 的分母改成 `hp + sum(arms)`（仍是 200 → $2/hp）。

---

## 5. 代码结构

### 5.1 `combat.py`（新，server 根目录，**不是系统**）

和 `ballistics.py` / `shop.py` / `grid.py` 同一层：被系统调用的库，没有 `run()`
也没有 `run(world, dt)`。

```python
apply(world, eid, module, amount, pid=-1)   # 唯一的 hp 写者
hit(world, eid, module, wd, dist=0.0, pid=-1)     # 穿透 + 爆炸两半
splash(world, eid, module, wd, dist, pid=-1)      # 只有爆炸那一半
arm_alive(world, eid, arm) -> bool                # 这条臂还能不能开火
_mech_hit(bufs, row, module, amount)              # 机甲的三条规则，10 行
_stop(bufs, row)                                  # 归零的车体当场停住
```

调用关系：

```
apply  ←─ hit    ←─ projectiles.impact（直击）
       ←─ splash ←─ systems/blasts（面积）
       ←─ debug_hurt（原始数值，main.py）
```

`apply` 的骨架（`combat.py`）：

```python
if not e.alive[eid] or amount <= 0.0:            return
if e.has(eid, "invulnerable"):                   return   # 混凝土
if <pilot>.god:                                  return   # 调试
bufs, row = e.slot(eid)
if "arm_hp" in bufs:   _mech_hit(bufs, row, module, amount)
else:                  bufs["hp"][row] = max(0.0, bufs["hp"][row] - amount)
if bufs["hp"][row] > 0.0:  <记 last_hit = pid>     # 只有还活着才记
else:                      _stop(bufs, row)       # 归零：停住，等 mortality
```

两个细节值得记住：

- **`last_hit` 只在车体还活着时写**。死判晚一个 stage，不这么写就变成
  "本 tick 最后一个碰到它的拿钱"，而不是"打出致命那一下的拿钱"。
- **`_stop` 把 `vel` / `drive` 清零**。判死晚一个 stage 意味着归零的车体在本 tick
  剩下的时间里还在记录里；不清零的话 `ai` / `movement`（都在伤害之前跑）会让它
  再开一段。这条让"死车不动"重新成立，也让求和统计与旧版逐位一致。

### 5.2 `systems/mortality.py`（新）

**唯一判死的地方。**

```python
def run(world, dt):
    ids = world.units()
    for eid in ids[e["hp"][ids] <= 0.0]:
        _kill(world, int(eid))
```

`_kill` 按顺序做：发 `boom`（`blast` / `big` 位来自 `death`）→ `_award`（跨阵营
才给钱）→ `_plan_respawn`（`deaths`、`pl.mech = -1`、`death_pos`、
`mode.respawn_delay`）→ `ends_match` 判胜负 → `_leave_wreck`（三元组，`None` 跳过）
→ `destroy` → `blasts.spawn(friendly=True, pid=credit)`。

### 5.3 `systems/blasts.py`（新）

```python
spawn(world, pos, power, friendly=False, skip=-1, pid=-1, faction=-1, life=0.0) -> int
run(world, dt)          # 每个 blast：结算 → blast_t -= dt → 到零销毁
```

`_burst` 用**全场共享的命中球表**（`world.sphere_table()`）一次算完所有单位的距离，
每个单位取最近球、代码与旧的 `_impact` 面积部分逐行同源（同样的 `lexsort` 去重），
只是数据源换成了表、战功来自 `blast_pid`。

**为什么殉爆也走实体**：

- 面积伤害只有一份实现：弹丸、引信、殉爆、以后的地雷 / 弹药车 / 燃烧弹都是
  「造一个 blast」；
- 连锁反应变成结构性的：殉爆下一 tick 才结算，不可能炸到刚变成的残骸；
- 战功在创建时冻结，不用在结算时判 `alive[shooter]`；
- `blast_t > dt` 就是一块燃烧区，`blasts` 每 tick 结算一次即 DoT。

成本：只有 `ed/er` 非零的弹丸才造 blast（机枪弹不造），每秒几十个。

### 5.4 改动的既有系统

| 文件 | 改动 |
| --- | --- |
| `systems/projectiles.py` | `_impact` → 公开的 `impact`：`combat.hit` + `blasts.spawn`；`sphere_table` 用 `sphere_geom` 缓存 + `sphere_world` 向量化，并回排成"单位 id 升序、单位内球序"（碰撞平局按表位置决） |
| `systems/proximity.py` | 末尾调 `projectiles.impact`（引信仍是"弹丸在此处炸开"） |
| `systems/player_input.py` | 车体 `hp[eid] > 0.0`；两个扳机 → `combat.arm_alive` |
| `systems/weapons.py` | 臂检查 → `e.buffers("arm_hp")[arch]`（从手边的构造取即可，不查实体） |
| `systems/locking.py` | 同 `combat.arm_alive` |
| `systems/movement.py` | `hp[row, MOD_CHASSIS]` → `hp[row]` |
| `systems/ai.py` | `hp[row]`；第二臂 → `"arm_hp" in bufs and arm_hp[row, 1] > 0`。**瞄准本身的 etype 分支不动** |
| `shop.py` | `arm_state` 读 `arm_hp/arm_hpmax`；维修需求 = 车体 + 双臂；维修写 `hp = hpmax; arm_hp = arm_hpmax` |
| `netcode.py` | `mech_hp_slots()`（init 里 `mech.hp`）、`hp_columns()`（快照与 meta 的 4 列） |
| `main.py` | `debug_hurt` → `combat.apply` |
| `world.py` | 删 `hit/splash/damage/kill/_death_blast/_leave_debris` 六个方法、`last_hit` dict；`spawn_unit` 写标量 hp + 臂池 + 护甲；新增 `blasts()` / `sphere_table()`；管线加两站；grid 的 tag 集合补 `unit` |

---

## 6. 命中球：缓存 + 向量化，不是组件

```python
# defs.UNIT_DEFS[...]["spheres"] = (module, right, up, forward, radius) ...
# mathutil.py
sphere_geom(etype) -> (local[n,3], radius[n], module[n])     # 每 etype 建一次
sphere_world(local, pos[m,3], yaw[m]) -> centres[m*n,3]      # 一次 numpy 旋转
unit_spheres(etype, pos, yaw) -> [(module, centre, radius)]  # 单人版本，保留
```

`sphere_world` 与旧的 `unit_spheres` 同式（`rxw = -sin, c = cos`），所以
`ballistics`（AI 的瞄准解算）、`proximity`、`blasts` 的几何结果逐位一致。
`projectiles.sphere_table` 现在按 etype 分组、每组一次向量化，再 `argsort` 回
单位序——把"每 tick 每单位一次 Python 循环"整段删掉了。

`world.sphere_table()` 把表**按 tick 缓存**：`projectiles` 和 `blasts` 共用一份，
`World.update` 顶部清掉。

---

## 7. 数据流

```
weapons ──► 弹丸实体
                 │
projectiles ─────┼──► combat.hit()  ──► hp / arm_hp / last_hit / (归零 → 停住)
                 └──► blast 实体
proximity ──────────► projectiles.impact ──► 同上 + blast 实体
                          │
blasts ───────────────────┴──► combat.splash() ──► hp / arm_hp / last_hit
                                                       │
                                                  mortality ──► destroy
                                                       ├──► 残骸实体 ──► debris ──► destroy
                                                       └──► 殉爆 blast 实体（下一 tick）
                                                       │
                                                  Player ──► economy ──► respawn
```

没有中间队列：`combat.*` 被调用的那一刻 hp 就变了。

---

## 8. 边角与协议

| 位置 | 调整 |
| --- | --- |
| `grid.build(ecs, ("moves", "combat", "unit"))` | 补 `"unit"`：今天 `unit ⊋ moves ∪ combat` 的唯一成员是碉堡（`unit + invulnerable`），`blasts` 若漏掉它会少一次（无效的）判定 |
| `netcode.hp_columns()` | 4 列 = `[hp, 0, arm_hp[0], arm_hp[1]]`；`MOD_TORSO` 列恒 0（躯干没有池）。常规单位 `[hp, 0, 0, 0]` |
| `netcode.mech_hp_slots()` | init 里的 `mech.hp` 由 `defs` 的 `hp + arms` 拼出来 |
| 客户端 `net.js` / `hud.js` / `scene.js` / `shop.js` | **零改动** |
| `blast` 实体 | **不进快照**（活 1~2 tick，15 Hz 抓不到）；火球仍由 `boom` fx 事件驱动 |
| `components.register` | 新组件在任何实体创建前注册（`ecs.py` 的既有约束） |

---

## 9. 行为差异（刻意的）

1. **殉爆晚一 tick 结算**（30 Hz 下 33 ms）。首爆伤害不变，连锁的第二跳晚一帧，
   换来的是"连锁不可能炸到刚变成的残骸"变成结构性的（不再靠"先解全部距离再落地"）。
2. **`hp <= 0` 的实体在本 tick 剩下的 stage 里仍然存在**（`blasts` 会打到它，
   `max(0.0, …)` 吃掉；`last_hit` 不再更新），到 `mortality` 才消失。
   `_stop` 保证它不再移动。收益是**没有任何系统在自己的循环里销毁实体**了。
3. **`blast` 实体占用并释放 id**，`ecs._free` 的 LIFO 顺序因此与旧版不同 →
   同种子的战斗从第一次爆炸起开始分岔（`tools/replay.py` 的 fixture 已按此重基线）。
   这与"每发弹丸都是一个实体"是同一类既有行为，不是本次引入的新性质。

除此之外没有第三处：伤害数值、命中几何、弹道、AI 决策全部逐位一致（§11 实测）。

---

## 10. 已拍板的两个决策

### 决策 1：`hp` 收成标量，只有机甲特殊 —— **采纳**

- `hp` / `hpmax` 标量（全员），`arm_hp` / `arm_hpmax` `f32(2,)`（只带臂的机体）。
- 线上仍是 4×u2、索引 `MOD_*`，由 `hp_columns()` 拼（§8）。客户端零改动。
- 代价：`shop.py` / `netcode.py` / 5 个系统的读取点改写（§5.4 清单）。
- 收益：常规单位身上不再有"三格永不使用的血池"；死亡判据退化成 `hp <= 0` 一行；
  臂编号 ≡ 武器编号，映射表不需要。

### 决策 2：殉爆延迟一 tick —— **接受**

连锁反应的安全性变成结构性的，`_death_blast` 那段"先解全部距离再落地"的手法
整段删除。

### 一次实现，不分阶段

重构一次性落地（`combat.py`、`systems/blasts.py`、`systems/mortality.py` 三个新文件
+ 15 个文件的改动）。不设阶段闸门，验收靠 §11 的实测：先把**行为等价**量出来，
再接受 §9 那两处刻意差异并重基线 fixture。

---

## 11. 实现与验证记录

### 落地清单

新增 3 个文件：

```
backend/server/combat.py            伤害解析，hp 的唯一写者
backend/server/systems/blasts.py    面积伤害 + 爆炸实体的寿命
backend/server/systems/mortality.py 判死、残骸、殉爆、战功、胜负
```

改动 15 个文件 `+401 / -338` 行（含 `tools/`）。

### 验证 1：新回归（已进 `tools/replay.py`，每次跑）

- `_damage_model()`：机甲 100/50/50；打左臂不掉车体、臂归零丢弹、
  `arm_alive` 变假、打已掉的臂记车体、打躯干记车体、APC 没有臂池、
  30mm 直击 = `hit_damage` 恰好一次、碉堡不掉血。
- `_death_blast()`：殉爆不分敌我（自家 APC 掉血、4 m 和 20 m 的人受伤但不死）、
  60 m 外不碰、留下且只留下一个残骸。**因为殉爆延后一 tick，这条回归里跑两次
  `world.update`**——`world.damage` 现在是 `combat.apply`，判死与爆炸分属两站。

```bash
.venv/bin/python tools/replay.py          # ok - 900 ticks match
.venv/bin/python tools/gunnery_check.py   # all checks passed
```

### 验证 2：新旧逐位对照（一次性，用 `git archive HEAD` 导出的旧树）

| 场景 | 旧 | 新 |
| --- | --- | --- |
| 30mm 打 APC，120 m / 400 m / 800 m，10 s | 1 / 4 / 5 命中，hp 87 / 48 / 35 | **完全相同** |
| 机甲 + APC + 步兵 + 远处 APC，机炮 + 火箭 13 s | 4 死，3 残骸 | **完全相同** |
| 同种子 battle 跑 5400 tick（3 分钟），无玩家干预 | 3 个阵亡，148 个单位 | **完全相同** |

逐位一致的路径包括：弹道积分、线段—球碰撞、命中球几何、`pen_damage` /
`blast_damage`、AI 的瞄准与开火判定。

### 验证 3：线上格式（真 websocket 连运行中的服务器）

```
init  mech.hp = [100, 0, 50, 50]                    # 与改前一致
snapshot  机甲 [100, 0, 50, 50]，APC/基地/平民 [hp, 0, 0, 0]
meta      hp [100, 0, 50, 50]，arms [(1,50,50), (3,50,50)]
debug_hurt module=2 amount=20  →  机甲 [100, 0, 30, 50]   # 只有左臂掉
```

### 已知遗留

- 客户端未在浏览器里实跑一遍（本次会话的浏览器桥未启动）。风险面为零：
  客户端代码未改，线上 4 列与 `meta.hp` 已用真连接逐字段核对（验证 3）。
- 用户可见数值未变，因此按 AGENTS.md 未写 `doc/product/CHANGELOG.md`；
  若 §9.1 的 33 ms 连锁延迟实测可感知，再补一条。

---

## 12. 验收标准（自查用）

1. `grep -rn "etype ==" backend/server/systems/` 在 `mortality` / `blasts` /
   `projectiles` / `proximity` 里零命中。
2. `grep -rn '\["hp"\]' backend/server` 的写入点只有 `combat.py`（伤害）与
   `shop.py`（维修 `hp = hpmax`）。
3. `world.py` 里 `ETYPE_` 只剩 `spawn_unit` / `spawn_mech` 两个工厂；
   `damage` / `hit` / `splash` / `kill` / `_death_blast` / `_leave_debris` 六个方法消失。
4. 新加一个会殉爆、会留残骸的单位 = 只加一条 `defs` 数据，不改代码。
5. 新加一个多血池但不是机甲的单位（两条腿各 50 血：`hp=100, arms=(50,50)`）=
   只加 `defs` 数据，不改代码 —— 这正是"`arm_hp` 是能力，不是机甲别名"的证据。
6. 快照里机甲四列是 `[hull, 0, left, right]`，常规单位 `[hp,0,0,0]`。
7. `tools/replay.py` 与 `tools/gunnery_check.py` 通过。

---

## 13. 明确留到以后

- 伤害类型 / 抗性体系（火、EMP、穿甲分类）：等第二个使用者。
- 持续伤害 / 燃烧区：`blast_t > dt` 已经是钩子，但需要一个 `burn` 组件和一个
  `burns` 系统。
- 护盾 / 能量 / 反应堆弱点：各自一个只它有组件 + 一段 `if "xxx" in bufs`。
  **当一个 tick 里出现三个以上这种分派时**，再升级成挂在 archetype 上的处理器表
  （`_handlers[arch.index]`，每 archetype 算一次，运行时零分支）。在那之前，
  两条分支的 if 比一张通用表好读。
- `damage` 若将来真的需要批处理（每秒上千条请求），再把 §3.4 否掉的队列拿出来谈。
- 想消掉 §9.3 的 id 分岔：把 `blast` 做成常驻池（`blast_t <= 0` 即空闲）而不是
  每炸一次 create/destroy 一个实体。现在不值得——一次性几十个实体而已。
