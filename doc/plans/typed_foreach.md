当前system里面有很多这种代码：

```csharp
    public static class Saclos
    {
        public static void run(World world, float dt)
        {
            var e = world.ecs;
            foreach (var g in e.groups("pos", "saclos"))
            {
                var posb = g.f("pos");
                var velb = g.f("vel");
                var age = g.f("proj_age");
                var wdv = g.u8("proj_wid");
                var seed = g.f("proj_seed");
                var src = g.i("proj_src");
                ...
```

大量字典存取，不好跳转到定义，还容易写错。

我曾经实现过一个typed ecs，但是是在python里面做的。

大概就是system都来自一个基类，这个基类提供一个迭代器，迭代器的一个参数是具体system的类型

比如：

```python
class SaclosSystemType:
    posb: float = 0.0
    velb: float = 0.0
    age: float = 0.0
    ...

class Saclos(EcsSystem):
    _SYSTEM_TYPE = SaclosSystemType

    def run(self, world: World, dt: float):
        for saclos in self.system_entities():
            # system_entities自动查询符合SaclosSystemType的archetypes，然后为每个archetypes的每个entity迭代出来
            # 此时 saclos就是一个SaclosSystemType实例，且有setter可以写回原位，可以直接写
            saclos.posb = xxxx
            ...

```


设想，向量化可以：

```python
class Saclos(EcsSystem):
    _SYSTEM_TYPE = SaclosSystemType

    def run(self, world: World, dt: float):
        for saclos in self.system_entities_rows():
            # system_entities_rows取出来，SaclosSystemType里面每一个都是所有entity的向量形式
            # 考虑到可能有多个archetype匹配，所以还是要迭代多次
            saclos.posb = xxxx # 做numpy数组运算
            ...
            # 这个在C#里面怎么呈现得考虑一下
```

我想也许会用到一些高档的类型魔法？

# 详细设计

## 0. 结论与范围

**目标：可读性与可维护性。** 现在系统里"读哪个组件"这件事以字符串字面量的形式散落在每个 pass 的循环体前面：少写一行就是静默拿错数据，写错一个字母就是运行期异常，`Go to definition` 无处可跳。改成一个 pass 用一个**声明式的 struct**（schema）：组件名在一处出现一次，字段名编译期检查，行内没有字符串。

**这不是性能项目，论证如下（实测，12 分钟战斗、807 entity、14 archetype）：**

| 实验 | 结果 |
|---|---|
| 每个 entity 的字符串查表次数 | 17,231 次/tick（其中 97% 是 per-entity，`Waves._capture` 62% + `Ai.run` 18%） |
| 把 `View` 的 scattered 读改成"每 archetype 缓存列"，查表降到 | 4,689 次/tick（-73%） |
| 同一轮的 tick 时间 | 1.50 → 1.48 ms/tick（**-1.3%**） |

字典查表本身不是成本。真正占 tick 的是 `Waves._capture`（23%，towns × units 双重循环，算法问题）和 `Ai.run`（20.8%）。这两件事与本文档无关，记录在 [§7](#7-不做留待以后)。

因此**明确不做**：组件 id / `by_id` 数组 / 位图 `has`、按 arity 铺开的泛型（`Q<A,B,C>`）、`static abstract` 接口魔法、Roslyn source generator、SIMD。

**范围**：代码里共 10 处 `groups(...)` 调用——`Flights` 4、`Movement` 2、`Gunnery` 1、`Ai` 1、`Weapons` 1（换弹 pass）、`Grid` 1（动态标签）；另有 `Expire` 用 `world.projectiles()` 的散点 id 表，本质是同一类行循环。批次 1–3 覆盖 `Flights` 5 个 pass（含 `Expire`）、`Gunnery`、`Movement` 2 个、`Ai` 与 `Grid`；`Weapons` 的换弹 pass、`Sweep`、`FirstHit`、`Impact`、`Mortality`、`Netcode` 留到批次 4 按需。动态标签查询与散点访问（`ecs.slot` / `View`）继续用字符串门，不追求 100% 覆盖。

## 1. 形态：一个查询 = 两个类型（列 + 行），跟着 system 放

一个 schema 是**一个 pass 需要的东西**，不是"一种 entity 是什么"。同一个 archetype 会被多个 schema 绑定（每个 pass 各绑一次），两份 schema 有重叠字段是正常的。

一个查询拆成两半，因为"整列"和"这一行的值"想用的是同一个名字（`pos` 既是整条 `float[3n]`，又是这一行的 `Vec3`），塞进一个类型就只好加前缀。拆开之后两个类型各占一个名字，谁都不用让：

- **列**（`RoundColumns`）：一个 archetype 一份。字段名 = 组件名，值 = 这个 archetype 的整条缓冲区（`float[] pos`、`byte[] wid`…）。
- **行**（`RoundRow`）：一个 entity 一份。16 字节——列的引用 + 行号，**它自己不装数据**。属性名 = 组件名（`pos` 是 `Vec3`、`age` 是 `float`），读写直接落在列上。

### 1.1 概念层：`Ecs/Columns.cs`

两个类型各有一个**抽象基**，把"列 / 行"这两个概念定下来，具体 schema 只填组件清单。基类只有一个、接口只有一个，和 schema 的数量无关：

```csharp
namespace Mech.Server;

/// <summary>一个 archetype 的**列**：`n` 行、row→id 的 `ids`，以及"第 i 行"的门。
///
/// 每个 schema 只实现三件事：`bind`（组件名 → 缓冲区）、`row`（第 i 行的记号）、
/// `Names`（组件清单，也是查询名）。</summary>
public abstract class Columns<TRow> where TRow : struct
{
    public readonly int n;          // 本 archetype 的行数
    public readonly int[] ids;      // row -> entity id

    // 泛型类的静态字段按 TRow 各存一份：每个 schema 一生只校验一次名字
    private static bool _checked;

    protected Columns(Group g)
    {
        n = g.n;
        ids = g.ids;
        if (!_checked && n > 0)
        {
            // 组件名写错的唯一静默失败模式是"查询匹配不到任何 archetype"，
            // 所以对着 ECS 的组件表核一遍：`Names` 是每个 schema 的静态清单，
            // 这里读它没有"子类字段还没初始化"的问题（见 §3.5）
            foreach (var name in Names) _ = g.arch.ecs.spec(name);
            _checked = true;
        }
    }

    /// <summary>把本 schema 的组件名换成这个 archetype 的缓冲区。
    /// 由具体 schema 的构造函数调用一次（`base(g)` 之后）。</summary>
    protected abstract void bind(Group g);

    /// <summary>第 i 行。调用 `n` 次。</summary>
    public abstract TRow row(int i);

    /// <summary>本 schema 的组件清单。与 `bind` 同文件、紧挨着。</summary>
    public abstract string[] Names { get; }

    /// <summary>行按顺序走一遍，所以一个 pass 读作 `foreach (var r in c)`。
    /// 枚举器是结构体（不分配），`Current` 就是 `row(i)`。</summary>
    public Enumerator GetEnumerator() => new Enumerator(this);

    public struct Enumerator
    {
        private readonly Columns<TRow> _c;
        private int _i;

        public Enumerator(Columns<TRow> c) { _c = c; _i = -1; }

        public TRow Current => _c.row(_i);

        public bool MoveNext() => ++_i < _c.n;
    }
}

/// <summary>一个 entity 的**行**：哪个列的第几行。读写都落在列上。</summary>
public interface IRow<out TColumns> where TColumns : class
{
    TColumns columns { get; }
    int index { get; }
}
```

**C# 的硬约束：struct 不能继承 class。** 行必须是 `readonly struct`（每行一个堆对象不可接受），所以行的"抽象基"只能是**接口**；`Columns` 是 class，所以能有一个抽象基类共享 `n` / `ids` / 名字校验。两个对象、两种抽象，这是语言决定的结构。

接口的用处是"概念 + 约束"：它规定了每个行结构都答得出"我属于哪个列、我是第几行"，也让泛型 helper 能写 `void log<TRow>(IRow<Columns<TRow>> r)`。日常代码一律用具体类型（`var r = c.row(i)`），接口不参与运行时、不装箱。`eid` 不放在接口的默认实现里（那会要求按接口类型调用才拿到），每个行结构自己写一行 `public int eid => _c.ids[i];`。

### 1.2 具体 schema：写在 system 所在文件里

`Ecs/Columns.cs` 只有上面那一个基类和一个接口。**具体 schema 跟它的 system 放在同一个文件里**（`Systems/Flights.cs` 顶上就是 `RoundColumns` / `RoundRow`），这样"这个 pass 读哪些组件"和"这个 pass 怎么用它们"在一屏之内：

```csharp
// Systems/Flights.cs
namespace Mech.Server.Systems;

/// <summary>One in-flight round's columns: the buffers every round owns
/// (`Components.PROJ_COMPS`) plus the `first_hit_record` the flight systems
/// re-arm and publish.  All four kinds bind this one schema; the kind itself
/// stays a query tag (`OnBullet`, `OnSaclos`, ...), never a branch.</summary>
public class RoundColumns : Columns<RoundRow>
{
    // bound by `bind`, which the constructor calls: hence the `null!`
    public float[] pos = null!, vel = null!, prev = null!, hit_pos = null!;
    public float[] age = null!, life = null!, trav = null!, seed = null!;
    public float[] hit_t = null!, hit_gap = null!;
    public byte[] wid = null!, hit_kind = null!, hit_mod = null!;
    public int[] src = null!, hit_unit = null!;

    internal static readonly string[] names =
    {
        "pos", "vel", "proj_prev", "first_hit_pos",
        "proj_age", "proj_life", "proj_trav", "proj_seed", "first_hit_t", "first_hit_gap",
        "proj_wid", "first_hit_kind", "first_hit_mod",
        "proj_src", "first_hit_unit",
    };

    public static readonly string[] OnBullet = Components.Cat(names, "bullet");
    public static readonly string[] OnRocket = Components.Cat(names, "unguided_missile");
    public static readonly string[] OnSaclos = Components.Cat(names, "saclos");

    public RoundColumns(Group g) : base(g) { bind(g); }

    public override string[] Names => names;

    protected override void bind(Group g)
    {
        pos = g.f("pos"); vel = g.f("vel"); prev = g.f("proj_prev");
        hit_pos = g.f("first_hit_pos");
        age = g.f("proj_age"); life = g.f("proj_life"); trav = g.f("proj_trav");
        seed = g.f("proj_seed"); hit_t = g.f("first_hit_t"); hit_gap = g.f("first_hit_gap");
        wid = g.u8("proj_wid"); hit_kind = g.u8("first_hit_kind");
        hit_mod = g.u8("first_hit_mod");
        src = g.i("proj_src"); hit_unit = g.i("first_hit_unit");
    }

    public override RoundRow row(int i) => new RoundRow(this, i);
}

/// <summary>One round: which columns, which row.  Every accessor is the buffer
/// element itself, so a write lands in the archetype and nothing is copied
/// back.</summary>
public readonly struct RoundRow : IRow<RoundColumns>
{
    private readonly RoundColumns _c;
    public readonly int i;

    public RoundRow(RoundColumns c, int i) { _c = c; this.i = i; }

    public RoundColumns columns => _c;
    public int index => i;
    public int eid => _c.ids[i];

    public Vec3 pos
    {
        get => new(_c.pos[i * 3], _c.pos[i * 3 + 1], _c.pos[i * 3 + 2]);
        set { _c.pos[i * 3] = value.x; _c.pos[i * 3 + 1] = value.y; _c.pos[i * 3 + 2] = value.z; }
    }
    public Vec3 vel { get => …; set => … }              // 同 pos，三个 float
    public Vec3 prev { get => …; set => … }
    public Vec3 hit_pos { get => …; set => … }

    public ref float age => ref _c.age[i];
    public ref float life => ref _c.life[i];
    public ref float trav => ref _c.trav[i];
    public ref float hit_t => ref _c.hit_t[i];
    public ref float hit_gap => ref _c.hit_gap[i];
    public ref byte hit_kind => ref _c.hit_kind[i];
    public ref byte hit_mod => ref _c.hit_mod[i];
    public ref int hit_unit => ref _c.hit_unit[i];
    public float seed => _c.seed[i];
    public byte wid => _c.wid[i];
    public int src => _c.src[i];
}

/// <summary>The top attack's columns: the round's, plus the velocity its stare
/// point walks at.  It reuses `RoundRow` - the two extra columns are read off the
/// column object, so there is no second row type to keep in sync.</summary>
public sealed class TopColumns : RoundColumns
{
    public float[] tvel = null!;

    private static readonly string[] top_names = Components.Cat(names, "tgt_vel");
    public static readonly string[] OnTop = Components.Cat(top_names, "top_attack");

    public TopColumns(Group g) : base(g) { }

    public override string[] Names => top_names;

    protected override void bind(Group g)
    {
        base.bind(g);
        tvel = g.f("tgt_vel");
    }

    public Vec3 tvel_at(int i) => new(tvel[i * 3], tvel[i * 3 + 1], tvel[i * 3 + 2]);
}
```

**命名与取值规则**

| 规则 | 内容 |
|---|---|
| 两个类型 | 列 `XxxColumns : Columns<XxxRow>`、行 `XxxRow : IRow<XxxColumns>`，成对出现在 system 文件里 |
| 读整列 | 列对象上：`c.pos`、`c.age`、`Array.Copy(c.pos, …)`、`c.pos.AsSpan()` |
| 读写一行 | 行对象上：`r.pos = r.pos + v * dt`、`r.age += dt`、`r.eid` |
| stride 3 的组件 | 行上是 `Vec3`，`get` / `set` 整体赋值（`Vec3` 是 readonly struct，改不了 `r.pos.y`） |
| stride 1 的组件 | 行上是 `public ref float age => ref _c.age[i];`——一个访问器，读写同一个门，还能 `ref` 传给 helper |
| stride 2 的组件 | 只在列上：`c.arm_hp[i * 2 + arm]`（和今天的代码逐字相同，没有更好的抽象） |
| 只读的组件 | 行上只 `get`（`wid`、`seed`、`src`）——能不能写由 schema 决定，写漏了编译不过 |
| 可选组件 | 列上 `public bool has_vehicle;` 与 `public byte[] vehicle;`（不在时给 `Array.Empty<byte>()`），行上转发并先判 |
| 分配 | 列是 class：一个 archetype、一个 pass、一个 tick 一个对象——和今天 `groups()` 给每个 archetype 造的 `Group` 同量级。行是 16 字节 struct，`c.row(i)` 是栈上的值，不分配、不进 GC |

**语法说明**（给不熟 C# 的人）：`new RoundColumns(g)` 是普通构造函数调用（`base(g)` 是"先让基类记下 n/ids，再干我自己的事"）；`bind` 是 `override` 出来的抽象方法，名字由基类规定。`RoundRow` 是 `readonly struct`：值类型，`c.row(i)` 只是把"哪个列 + 第几行"拷到栈上；`r.age = x` 写的是 `_c.age[i]`，**没有回写这一步**。`ref float age => ref _c.age[i]` 是"引用返回属性"：读和写都直接指向那个数组元素。

## 2. 迭代协议

**两层循环：外层 archetype、内层行**——和今天的代码结构一一对应，只是内层从
`for (int row = 0; …)` 变成 `foreach (var r in c)`：

```csharp
public static class Saclos
{
    public static void run(World world, float dt)
    {
        var e = world.ecs;
        foreach (var g in e.groups(RoundColumns.OnSaclos))     // 每个匹配的 archetype 一次
        {
            var c = new RoundColumns(g);                       // 绑定：每 archetype、每列一次查表
            foreach (var r in c)                               // 每 entity 一次；行内零查表
            {
                if (!_begin(r)) continue;
                var p0 = r.pos;
                var wd = Defs.WEAPONS[r.wid];
                float speed = r.vel.len;
                if (wd.burn <= 0f || r.age < wd.burn) speed = MathF.Min(wd.maxvel, speed + wd.thrust * dt);
                else speed = MathF.Max(0f, speed - wd.drag * speed * speed * dt);
                …
                _move(world, r, p0, new Vec3(vx, vy, vz), dt);
            }
        }
    }
}
```

`_begin` / `_move` 也吃行对象（**按值传，不需要 `ref`**：写的是数组，行对象本身不会被改），于是它们那 7 次 per-entity 查表一起消失：

```csharp
private static bool _begin(RoundRow r)
{
    if (r.hit_t <= 1f) return false;                // condemned: do not fly
    r.hit_t = Defs.FIRST_HIT_ARMED;                 // this tick's record is fresh
    r.prev = r.pos;
    return true;
}

private static void _move(World world, RoundRow r, Vec3 p0, Vec3 v, float dt)
{
    r.vel = v;
    var p1 = new Vec3(p0.x + v.x * dt, p0.y + v.y * dt, p0.z + v.z * dt);
    r.pos = p1;
    r.trav += MathF.Sqrt(v.x * v.x + v.y * v.y + v.z * v.z) * dt;
    world.sweep.add(r.eid, p0, p1);
}
```

**为什么是两层而不是一个迭代器**：把 archetype 那层也藏进迭代器（`foreach (var r in Rows.of(e))`），就等于把"匹配哪些 archetype、按什么顺序"藏起来，系统读起来更绕，而且那个迭代器要么每行分配，要么要 C# 13 的 `allows ref struct`。两层各自显式，谁都能一眼看出"这是 archetype、这是 entity"——这正是这次要买的东西。

内层的 `foreach` 是**结构体枚举器**（`Columns<TRow>.Enumerator`）：不分配、不走 `IEnumerable<TRow>` 的接口派发，`Current` 就是 `row(i)`。和显式 `for` 的实测对比（12 分钟战斗，各 3 次）：`for` 1.53–1.55 ms/tick，`foreach` 1.51–1.53 ms/tick——差别在噪声里，所以按手感选了 `foreach`。

**列对象也可以只当绑定器**：用于"已经拿到一个 Group"的场合（动态 tag 查询），例如 `Core/Grid.cs`（批次 3 才做）：

```csharp
foreach (var g in ecs.groups(tag, "pos"))   // 动态标签，查询名保持字符串
{
    var c = new PlaceColumns(g);            // 只借它的列名，不借它的 names
    for (int i = 0; i < c.n; i++) { … }
}
```

## 3. 规则与不变式

1. **列对象只在 archetype 层构造**，行循环里不出现 `g.f("…")`。这一条是这次改动的全部意义，code review 只看这一点。
2. **一个 pass 迭代某个 archetype 的过程中，不得向它 spawn/destroy。** `Archetype._resize` 会换掉缓冲区数组（并 `_gen++`），schema 持有的是旧数组的引用，`Group` 又已经快照了 `n`/`ids`。第一批（Flights）不 spawn、不 destroy，安全；后续批次逐个 pass 检查，遇到需要这样做的 pass（`Blasts`、`Mortality`）就保持现状、不迁移。
3. **浮点表达式一字不改。** 迁移只做"`age[row]` → `r.age`"这种替换，不合并、不重排、不引入中间变量改变舍入。`float` 运算顺序是这个模拟的一部分（`MECH_SEED` 下可复现）。
4. **不引入缓存。** schema 字段就是 archetype 缓冲区的引用，不做"上次用的数组"之类的旁路缓存。
5. **组件名字符串只出现在 `bind` 与 `Names` 里**，两者在同一个 schema 里紧挨着，一屏之内能对完。写错名字的唯一静默失败模式是"查询匹配不到任何 archetype，系统静默不工作"，所以 `Columns<TRow>` 的构造函数在首次绑定时把 `Names` 逐个对照 `ecs` 的组件表（`ecs.spec(n)`），未知名字直接抛异常。校验集是 `Columns<TRow>` 里的 `static HashSet<Type>`，按**具体的 schema 类**（`GetType()`）记账，因此每个 schema 一生只校验一次、继承来的 schema（`TopColumns`）也各查各的，且不需要任何中心注册表——这也是 schema 能散到各 system 文件里的前提。基类要拿到组件表，所以 `Archetype` 加了一个 `internal ECS ecs => _ecs;`。
6. **行内不再出现字符串；散点访问仍走 `ecs.slot` / `View`。** 一个系统只碰几个"别的" entity 时（`Weapons.fire` 里的射手、`Seeker.track` 里的被锁 hull），那本来不是行循环，保持现状。

## 4. 迁移批次

**状态：批次 1、2 已完成（2026-02 交付），批次 3、4 不做**——今后谁维护 `Ai` / `Weapons` / `Sweep` / `FirstHit` / `Impact` / `Mortality` / `Netcode`，谁按 §1 的形状顺手迁，不列为待办。


### 批次 1：`Systems/Flights.cs`（样板，含两个共享 helper）

| schema | 组件 | 用于 |
|---|---|---|
| `RoundColumns` / `RoundRow` | pos, vel, proj_prev, first_hit_pos, proj_age, proj_life, proj_trav, proj_seed, first_hit_t, first_hit_gap, proj_wid, first_hit_kind, first_hit_mod, proj_src, first_hit_unit | `Expire`、`_begin`、`_move`、`Ballistic`、`Rocket`、`Saclos` |
| `TopColumns`（复用 `RoundRow`） | 上面全部 + tgt_vel | `Top` |

- `Expire` 从 `world.projectiles()`（`query("proj_kind")` 的散点 id 表）改成 `e.groups(RoundColumns.names)`。两者顺序完全相同（都是 archetype 顺序 + 行顺序），所以行为不变——这也是为什么 `Expire` 可以一起迁。
- 四种弹各自保留自己的 pass 与 tag（`OnBullet` / `OnRocket` / `OnSaclos` / `OnTop`），`Defs.KIND_TAGS` 的语义不动。
- `Seeker.track(world, r.eid, p0, dt)` 暂不改签名（它按 id 读自己的 `target` / `tgt_mem`）。
- **`TopColumns` 继承 `RoundColumns`、复用 `RoundRow`**：`bind` 是 `override`，先 `base.bind(g)` 再补 `tgt_vel`；`Names` 覆盖成 `names + tgt_vel`。于是 `_begin` / `_move` 对四种弹是同一个签名，`Top` 里多出来的那一列读自列对象（`c.tvel_at(i)`），不需要第二个行类型。`bind` 是构造函数里唯一一次虚调用，它只做赋值，所以在派生实例就绪前分派也是安全的（schema 里有一行注释说明）。

### 批次 2：`Gunnery` + `Movement`

| schema | 组件 | 用于 |
|---|---|---|
| `SightColumns` / `SightRow` | sight_yaw, sight_pitch, yaw, pitch, turret_yaw, etype（+ 可选 vehicle） | `Gunnery.run` |
| `SteerColumns` / `SteerRow` | vel_cmd, leg_yaw, etype（+ 可选 vehicle） | `Movement._turn_legs` |
| `MoverColumns` / `MoverRow` | pos, vel, vel_cmd, wheel_speed, backout_t, blocked_t, leg_yaw, yaw, hp, etype（+ 可选 vehicle） | `Movement.run`、`Movement._vehicle` |

注意 `MoverColumns` 把 `yaw` 写进了 `names`（今天的 `groups(...)` 没列它，靠"所有 unit 都有 yaw"隐式成立）。写进去之后查询语义仍然是同样的 archetype 集合，且少一条隐性依赖。`turret_yaw` 则是每个 unit 都有的**数据**（`has_vehicle` 是**标签**），所以 `SightColumns` 无条件绑定它，只有 `vehicle` 那个标签是可选的。

`Movement._vehicle(...)` 现在是 7 个数组参数的函数，改成一个 `MoverRow`（按值传）。

### 批次 3：`Ai` + `Grid`

| schema | 组件 | 用于 |
|---|---|---|
| `MissileColumns` / `MissileRow` | pos, vel, tgt_mem, faction, proj_kind, target, proj_wid | `Ai._missiles` 的 6 次批量读（今天是 6 个 `View.get(ids, …)`，每个 id 一次查表） |
| `CrewColumns` / `CrewRow` | 按 pass 再拆：`Ai._advance` 一份、`Ai._aim_and_fire` 一份 | `Ai.run` |
| `PlaceColumns` / `PlaceRow` | pos（+ ids） | `Grid.build` 的绑定器用法 |

`Ai` 是剩下查表最多的 pass（3,129 次/tick），但它的结构最复杂：一个 hull 的 pass 会读"别的"entity（目标、射手、飞来的弹）。所以先做 `_missiles`（纯批量读，最好迁），再按 pass 逐个拆 `CrewColumns`，不追求一次迁完。

### 批次 4（按需，不承诺）

`Weapons` 的开火 pass 是 `e.locate(ids, …)` + 逐 id 的 `a.f("…")`；要迁就得先把它改成按 archetype 遍历（会改动 `spawn_round` 的调用顺序），属于行为风险，单独立项。`Sweep`、`FirstHit`、`Impact`、`Mortality`、`Netcode` 同理，谁在维护谁顺手迁。**不做完也是可以接受的终局**：字符串门留在原地，schema 覆盖"行循环"这一类。

## 5. 验收

每批都必须过：

1. `./dotnet.sh build` 零警告零错误。
2. **逐字相同的行为**。这次改动不改迭代顺序、不改浮点表达式，所以在 `MECH_SEED=1` 下 `--check` 的输出应当**逐字一致**，而不是"量级相同"：

```bash
MECH_SEED=1 ./dotnet.sh run -- --check battle --minutes 12
MECH_SEED=1 ./dotnet.sh run -- --check range  --minutes 2
MECH_SEED=1 ./dotnet.sh run -- --check hit
MECH_SEED=1 ./dotnet.sh run -- --check intercept
```

基线（本机，**四个 check 全部带 `MECH_SEED=1`**）。不带种子时 `hit` 的长机距离结果和 `intercept` 的击落数都会随机波动（实测 `topattack 1400` 在同一次改动前后都能从 10 掉到 0），带种子才可比。批次 1、2 完成后逐字复现了这些数字：

```
battle  12 min: entities=807 archetypes=14
        events: boom=54352 capture=29 intercept=172 seeker=25 shot=54411 smoke=16
        census (etype: n): 1:85, 2:145, 3:11, 4:25, 5:2, 6:45, 7:1, 8:5
        1.50-1.55 ms/tick incl. wire（同一台机器噪声 ±3%，见 §6）（0.069-0.070 是 wire；改动前后都在这个区间）
range    2 min: entities=22 archetypes=5, shots launched: 20, mine alive: 19
                census (etype: n): 3:12, 4:3, 9:5
hit (MECH_SEED=1): rpg 400/700 → 100 100; rocket 700/1200 → 100 100;
          saclos 1000 → 0, 1600 → 100; topattack 900/1400 → 10 10
intercept (MECH_SEED=1): rpg 400/700 → 3 4 shot down; rocket 700/1200 → 0 0;
          saclos 1000/1600 → 0 0; topattack 900/1400 → 4 4
```

`--check` 每次都以 `ecs.validate()` 收尾，行/archetype 记账错会直接失败。

3. 每个系统自己的 `#region` 说明（如果有）跟着改；`doc/` 里描述这些系统实现的段落若引用了具体写法，同一批里同步。这次是内部重构、无玩家可见变化，**不 bump 版本、不写 `doc/product/CHANGELOG.md`**（AGENTS.md）。

## 6. 工作量（实测，批次 1 + 2）

| 文件 | 变化 |
|---|---|
| `Ecs/Columns.cs`（新） | +115 行：基类 `Columns<TRow>`（含结构体枚举器）+ 接口 `IRow<TColumns>` |
| `Ecs/Ecs.cs` | +4 行：`Archetype.ecs`（`internal`，基类校验名字用） |
| `Ecs/Components.cs` | `Cat` 由 private 改 public（schema 用它给 `names` 拼 tag） |
| `Systems/Flights.cs` | 5 个 pass + `_begin`/`_move` 迁移，新增 `RoundColumns` / `RoundRow` / `TopColumns` |
| `Systems/Gunnery.cs` | 新增 `SightColumns` / `SightRow` |
| `Systems/Movement.cs` | 新增 `SteerColumns` / `SteerRow` / `MoverColumns` / `MoverRow`；`_vehicle` 从 7 个数组参数改为一个 `MoverRow` |
| 合计 | 约 530 行插入 / 217 行删除（含注释）；tick 时间在噪声内（`for` 版 1.53–1.55、`foreach` 版 1.51–1.53 ms/tick，各 3 次） |

成本与收益：每个 schema 有两个类型（列 + 行），行类型的访问器和列的 `bind` 是同一份组件清单的两次书写。换来的是**每个 pass 的组件依赖在一处可见、字段名编译期检查、行内无字符串、两个门各自用最自然的组件名**，以及 `_begin` / `_move` / `_vehicle` 这些 helper 不再各自按字符串取缓冲区。

## 7. 不做、留待以后

- **`Waves._capture` 的 O(towns × units) 双重循环**：占 tick 23%，每个 (town, unit) 对做 2 次 `View` 读。正确做法是用 `world.grid` 只问 170 m 环内的 hull，或每 tick 把 hull 按 town 分一次桶。独立议题，与本设计无关，但它才是这张表上的第一个数字。
- **`Ai.run`（20.8%）**：同样的行为优化议题（每 hull 的 `_acquire`/`_aim_and_fire` 开销），不在这次范围里。
- **组件 id / `by_id` 数组 / `View` 列缓存**：实测收益 1.3%，不做。若以后某天要做，注意它和本设计是正交的——schema 只是"名字 → 缓冲区"的搬运，换成 id 也不影响 schema 的形状。
- **source generator**：不做。仓库单 csproj、零第三方包；10 个查询手写一遍比引入一个构建期依赖便宜。
- **SIMD / 批量循环**：不做。`AGENTS.md` 里"批量循环比标量贵"已被测过两次。