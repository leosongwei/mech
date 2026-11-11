# 客户端技术栈调研：能不能只留一套语言

> **状态：调研，未落地（2026-11-10）。** 本文只记录事实、数字和未验证项。
> 不动代码，不改 `MechServer.csproj` 的 `<Version>`（纯文档改动按约定不升版本）。
> 所有外部事实都在 2026-11-10 当天查过一次原始来源，§7 列了来源与复核命令。

> **一句话结论。** 「两种语言」的真实成本只有一处：`map.js` 里 555 行必须与服务端逐行一致
> 的弹道/视线求解器。它是**客户端火控与服务端 AI 火控的孪生实现**——而火控必须在客户端本地
> 解算（服务端替客户端算准星，等于服务器去拧炮塔，并且开出一个作弊面，§6.1），所以只要客户端
> 是 JS，这 555 行就只能靠纪律维持。让它们从「纪律」变成「不可能不一致」的唯一办法是
> **两边编同一份程序集**，也就是 C# 客户端。其余 8000 行（渲染、HUD、音频、主循环）换语言
> 一样要重写。真要换，档位排序是 **NeoVeldrid ≈ Stride.Graphics ≪ Godot**，
> 分水岭只有一处：UI 和音频愿不愿意自己扛。

---

## 1. 现状盘点

### 1.1 两边的体量

| 目录 | 文件 | 行 |
| --- | ---: | ---: |
| `Ecs/` | 3 | 1217 |
| `Core/` | 15 | 6710 |
| `Systems/` | 35 | 5665 |
| `Modes/` | 9 | 1400 |
| `Net/` | 2 | 1871 |
| `Harness/` | 4 | 1175 |
| `Program.cs` | 1 | 80 |
| **服务端合计** | **69** | **18118** |
| **`frontend/src/` 合计** | **14** | **8546** |

客户端按文件（大的四个占了 79 %）：

| 文件 | 行 | 换引擎时的命运 |
| --- | ---: | --- |
| `game.js` | 1819 | 主循环 + 瞄准解算 + 相机 + 设置，**重写** |
| `hud.js` | 1623 | 手搓 canvas2d，**重写**（这是换引擎收益最大的一块） |
| `renderer.js` | 1356 | 9 个 GLSL 程序 + 地形 LOD + 实例化 + 屏幕空间 overlay，**重写** |
| `audio.js` | 946 | 自定义 DSP 链 + 一批实测常数，**重写并重测** |
| `map.js` | 760 | 其中约 **555 行是同源代码的孪生**（见 1.2），只有 C# 客户端能让它们变成一份程序集 |
| `scene.js` | 560 | 代码拼模型（`meshes.js` 原始体），**搬** |
| 其余 8 个 | 1482 | `net.js` 搬、`meshes.js` 搬、`input.js` 换引擎给、`math.js` 搬 |

### 1.2 真正的重复面只有 555 行

`map.js` 里与服务端「必须逐行一致」的部分，是这一份文档（`AGENTS.md`）里反复警告的地方：

| `map.js` 区域 | 行数 | 镜像的是服务端哪个 |
| --- | ---: | --- |
| 188–398 地形查询门（`heightAt` / `wallAt` / `trunksAt` / `trunkHit` / `obstacleAt` / `canopiesAt` / `sightAt`） | ~210 | `Core/Terrain.cs` + `Systems/Sight.cs` |
| 415–760 弹道预测（`predictImpact` / `sightPoint` / `leadPoint`） | ~345 | `Core/Ballistics.cs` + `Systems/Gunnery.cs` |

**这一点决定了整个讨论的重心。** 两套语言的长期成本落在这一条上，而服务端消不掉它：
火控属于客户端（§6.1），服务端替客户端算准星就是服务器去拧炮塔，还顺带开出作弊面。
这 555 行只能靠「两边编同一份程序集」消掉，而那条路要求客户端是 C#。

### 1.3 线上协议已经是契约

`Net/Netcode.cs` 的 `static_payload` 把地图和全部定义一次性下发，之后是 15 Hz 二进制快照
（`ROWS` 五类行宽）+ 时钟块 + 小地图块。**服务端不假定客户端是什么语言**，客户端也不读
任何服务端的私有结构。这意味着换客户端是「换一个消费者」，不是「改协议」—— 这件事今天
已经成立，代价是 0。

---

## 2. 前提：换引擎白拿的是基础设施，渲染器本身要重写

这是评估四个候选项时的共同前提，值得先写下来。

现在的渲染器做的全是自定义 pass：

- 9 个 GLSL ES 300 程序写在 `renderer.js` 的字符串里
- 地形两遍画：径向 LOD 远网格 + 瓦片对齐近补丁（`uOrigin` 移动），瓦片缝在屏幕空间画，
  每个三角形的 flat normal 用 fragment 导数求
- 单位和道具走 `WEBGL_multi_draw` 实例化（4 处调用），按 640 m 分块剔除
- 屏幕空间 overlay（RWR 箭头环）复用世界网格、缓冲和着色器，用 `_ovlMat` 换掉相机矩阵
- 烟雾是**不透明**的实体 pass，不做混合

**这些在四个候选里都得自己写。** 引擎/抽象层替你写的是*基础设施*（交换链、内存分配、
描述符池、帧同步、管线缓存），所以选型比较的是「少写多少样板」和「白拿多少外围」，
而不是「谁的渲染效果更好」。

---

## 3. 候选评估

### 3.1 NeoVeldrid —— Veldrid 的活跃分支

**定位：C# 的 wgpu / gfx-hal。** 低层、跨后端图形库，没有场景图、没有资产管线。

抽象层：`GraphicsDevice` / `Buffer` / `Texture` / `ResourceLayout` + `ResourceSet`（描述符）/
`GraphicsPipelineDescription` / `Framebuffer` / `CommandList` / `Swapchain`。pass、着色器、
命令录制全在自己手里；交换链、内存分配、描述符池、帧同步、管线缓存由库负责。最小可跑
程序约 80 行。

实况（2026-11-10 查得）：

| 项 | 值 |
| --- | --- |
| 包 | `NeoVeldrid` / `NeoVeldrid.StartupUtilities` / `NeoVeldrid.SPIRV`，`1.3.1` |
| 发布 | `1.3.1` 于 **2026-10-26**；仓库 `jhm-ciberman/neo-veldrid` 于 2026-11-01 仍在提交 |
| 目标框架 | **`net10.0`** |
| 许可 | MIT（原 Veldrid 与原作者的架构设计） |
| 绑定层 | **全部换成 Silk.NET** |
| 后端 | Vulkan / D3D11 / OpenGL / OpenGL ES；macOS 走 MoltenVK（自动打包） |
| 额外 | headless 模式（无窗口跑 GPU）、有文档站与 samples、CI 测试 |
| 活跃度 | 仓库 49 stars、15 open issue —— 小，但是「一个人认真在维护」的规模 |

**对本案最关键的一条：`NeoVeldrid.SPIRV` 在运行时把 GLSL 编成 SPIR-V 再交叉编译到所有
后端。** `renderer.js` 里那 9 个 GLSL ES 300 程序是四个候选里迁移成本最低的——改版本号、
`attribute`/`varying` 换 `in`/`out`、uniform 改成显式 set/binding。

需要自己接的：UI（配 `ImGui.NET`）、音频（配 `Silk.NET.OpenAL` 或 miniaudio 绑定）。
项目零导入资产，资产管线这一栏是空的。

> 注意：NuGet 上的 `Veldrid` 本体停在 `4.9.0`（`net6.0` / `netstandard2.0`），上游
> `mellinoe/veldrid` 已停止活跃维护。**路线是 NeoVeldrid**，API 完全兼容，换包改名即可。

### 3.2 Silk.NET —— 原始绑定层

**定位：`vulkan.h` 的 1:1 投影。** 是绑定，不是渲染器。

实况（2026-11-10）：

| 项 | 值 |
| --- | --- |
| 仓库 | `dotnet/Silk.NET`（.NET 基金会名下），MIT |
| 最新版 | `v2.23.0`，**2026-01-22**（上一版 2024-11，再上一版 2024-04 —— 约一年一版） |
| 仓库活动 | 2026-11-10 仍在提交，未归档，5263 stars，108 open issue |
| 包 | `Silk.NET.Vulkan` / `.Windowing` / `.Input` / `.WebGPU` / `.OpenAL` / `.Shaderc` / `.Sdl`，全 2.23.0 |

它给的是 `VkDevice` / `VkCommandBuffer` / `vkCmdDrawIndexed` 加上扩展（`Silk.NET.Vulkan.Extensions.KHR`），
以及窗口（GLFW/SDL）和输入。需要自己写的：交换链与 resize、frames in flight、
`VkAllocateMemory` 或自建 suballocator（VMA 是 C++）、descriptor pool、pipeline layout
缓存、render pass 抽象、staging buffer、同步。**在地形画出第一个三角形之前，约 2000–3000 行。**

**旁证它够成熟：Stride 4.4 自己就依赖 Silk.NET 2.23**（SDL 窗口 + Direct3D12 后端）。

`Silk.NET.Vulkan` 的价值在于 Veldrid 封不住的扩展（bindless、mesh shader、multiview、
各类 KHR）。本案的渲染器是一组固定的 pass，用不到那一层。

### 3.3 Stride 4.4

**定位：引擎，但图形层可以单独当库用。** 全 C#，无互操作边界。

实况（2026-11-10）：

| 项 | 值 |
| --- | --- |
| 站点 | <https://www.stride3d.net/>，最新 **4.4** |
| 许可 | MIT，编辑器与运行时全部开源 |
| 语言/运行时 | 官网首页写明 **C# 14 / .NET 10** |
| 4.4 主要内容 | **Vulkan 与 Direct3D 12 后端重写**、新着色器编译器、CLI 工具、Native AOT |
| 编辑器 | **Game Studio 原生只有 Windows**；Linux 走 Wine；官方在重写为跨平台 |
| 目标平台 | Windows / Linux / macOS / Android / iOS |

官方平台表（`doc.stride3d.net/latest/en/manual/platforms/index.html`）：

|  | 编辑器 | 只构建（不用编辑器） | 纯代码项目 |
| --- | --- | --- | --- |
| Windows 10 / 11 | 完整支持 | 支持 | 支持 |
| Linux | 需 Wine | **支持** | **支持** |
| macOS | 不支持 | 支持 | 支持 |

这张表是本案选型的一个关键点：**Linux 上「纯代码项目」是完整支持的**，只有编辑器需要
Windows。而本项目的客户端恰好是纯代码形态（§3.3 末尾），所以这个约束在本案里近乎为空。

`Stride.Graphics 4.4.1` 的 nuspec 实测（这一点是本次调研里最有价值的发现）：

```
targetFramework: net10.0
  ├─ Silk.NET.Sdl 2.23.0             窗口/输入
  ├─ Vortice.Vulkan 3.0.3            Vulkan 后端
  ├─ Silk.NET.Direct3D11 / D3D12     D3D 后端
  ├─ Microsoft.Direct3D.D3D12 1.619.3
  ├─ Silk.NET.Direct3D.Compilers     shader 编译
  ├─ Stride.Shaders.Effects
  └─ Stride.Foundation
（另有 net10.0-android36.0 / -ios26.0 / -macos26.0 目标组）
```

也就是说 4.4 把后端并进了 `Stride.Graphics` 一个包：**Linux 上就是 SDL + Vortice.Vulkan，
没有 4.1 时代那种「平台项目」分工程了**。可以只坐在 `GraphicsDevice` + `CommandList` +
`PipelineState` 这一层写，`Stride.Engine`（`Game` / `GameSystem` / `Services` / 场景图）
整个不碰。**这就是 Veldrid 的档位，外加可选的引擎层。**

白拿：`Stride.UI`（真布局、字体、主题、锚点）、`Stride.Audio`（`AudioBus` + 效果器链，
几乎一一对应 `audio.js` 手工搭的 far 床 / 限制器 / reverb send）、`GeometricPrimitive`
（box / sphere / torus / teapot，对应 `meshes.js` 的原始体）。

成本与风险：

- **着色器路线是最大的不确定性**：原生材质系统说的是 SDSL（`.sdsl` + mixin），9 个 GLSL
  程序要么重写成 SDSL，要么直接喂 SPIR-V（包里带了 `Stride.Dependencies.SpirvCross`，
  说明交叉编译是内置的，但对外 API 接不接受原始字节码**没有验证**）。
- `.Game` 工程的资产编译由 MSBuild target 触发。项目零资产（不 `Content.Load`），理论上
  整个跳过，**需要实测**。
- 编辑器 Windows-only 对本案影响很小：模型是代码拼的（`meshes.js` 原始体）、地形程序生成、
  HUD 代码画，全项目没有一条导入资产 —— 这正是「code-only」项目形态。一旦将来要导入
  真模型/动画，Windows-only 编辑器就变成硬约束。

### 3.4 Godot 4.7

**定位：完整引擎，但渲染服务可以单独驱动。**

「只用来画」在 Godot 里是成立的，官方留好了门：

- `RenderingServer`：场景树（`Node3D` / `MeshInstance3D`）只是它的客户端，可以直接
  `instance_create` / `instance_set_transform` / `camera_create`，一个节点都不建。
- `PhysicsServer3D` 是按需的：不创建任何 body 就没有物理，不会有东西去接管 ECS 的位置。
- `RenderingDevice`：Godot 内部的 Vulkan 级 API（compute pipeline、自定义 render pass），
  抽象挡道时往下钻一层。
- 相机可以塞自己的 lookAt basis，`math.js` 的相机模型可以照搬。

实况（2026-11-10）：

| 项 | 值 |
| --- | --- |
| 稳定版 | **4.7.2**（2026-08-18） |
| 许可 | MIT |
| 目标平台 | Windows / Linux / macOS / Android / iOS |
| 编辑器 | **三平台原生**（这一条是它在平台维度上对 Stride 的净优势，见 §3.3 的表） |
| 物理 | 内置 Jolt 等，**按需启用** |

编辑器平台这一栏值得单独说：Stride 的 Game Studio 原生只有 Windows（§3.3），Godot 的
编辑器在 Windows / Linux / macOS 上都是原生。对本案的权重取决于将来要不要编辑器工作流
（现在是零导入资产，权重低）。

白拿：`Control` 控件系统（`hud.js` 1623 行手搓 canvas2d 的最大对手）、`AudioBus` + 效果器
（压缩、限制、混响）、模型/贴图/阴影/后处理、多平台导出、`MultiMesh`（等价于现在的
`multi_draw` 批处理）。

成本与风险：

- **每帧跨 C# ↔ native（GDExtension）互操作边界。** 属性读写都过桥；几千个 hull 逐个设
  `Position` 会疼，必须走 `MultiMesh` 的 buffer 或 `RenderingServer` 批量通道。
- **.NET 版本耦合**：Godot 4.x 的 C# 模板长期是 `net8.0`，4.7 的模板 TFM **本次没查到
  确定值**（见 §5）。`Mech.Core` 需要多目标 `net8.0;net10.0` 才能两边共用。
- 地形瓦片 LOD + `uOrigin` 补丁 + 导数求 flat normal、屏幕空间 overlay：都要在 Godot 的
  `Shader`（契约是 `ALBEDO` / `VERTEX` / `NORMAL`）里重写，或降到 `RenderingDevice`。
- **C# 的 web 导出历来薄弱**（近年才有实验性支持，具体版本**未验证**）。选了 Godot 基本
  就是原生路线。

### 3.5 已排除

| 路线 | 为什么 |
| --- | --- |
| **Blazor WebAssembly** | 只有 WebGL2（经 JS 互操作），拿不到 Vulkan；且 WASM 下的 .NET 与现有 `net10.0` 服务端逻辑仍要裁剪 |
| **MonoGame + DesktopVK** | Vulkan 后端一直不是主线，`net10` 目标与维护状态都要另查 |
| **换 WebGPU 而非 Vulkan** | 唯一能「一份代码同时跑原生和浏览器」的 API（`Silk.NET.WebGPU`），但原理上要求客户端留在浏览器里；C# 原生客户端用不上这个收益。**如果最终决定继续留在浏览器，这条路要重新评估** |
| **继续 JS，但换 TypeScript** | 保留浏览器 + 孪生弹道（§6.1 认可的形态），只是让 8000 行更好维护。与「换客户端」是两件事，可以并行 |

---

## 4. 横评

四个候选其实是**同一个技术栈的四个高度**（Stride 和 NeoVeldrid 都建在 Silk.NET 上），
所以不存在「选错阵营」的风险。

|  | Silk.NET.Vulkan | NeoVeldrid | Stride.Graphics 4.4 | Godot 4.7 |
| --- | --- | --- | --- | --- |
| 抽象档位 | Vulkan 原始 | wgpu 档 | wgpu 档 | 引擎（渲染服务可单独用） |
| 自己写的样板 | 2000–3000 行 | ~0 | ~0 | ~0 |
| 自己写的 pass | 全部 | 全部 | 全部 | 全部 |
| GLSL 复用 | 经 Shaderc | 经 SPIR-V，直通 | SDSL 重写，或 SPIR-V（未验证） | `Shader` 语言重写 |
| `.NET` | 任意 | **net10.0** | **net10.0** | **待查（历史为 net8.0）** |
| 互操作边界 | 有（绑定调用） | 有（绑定调用，仅在提交时） | **无（全 C#）** | **每帧每属性都过桥** |
| 窗口/输入 | `.Windowing` / `.Input` | `StartupUtilities` | 自带（SDL） | 自带 |
| UI | ImGui.NET | ImGui.NET | Stride.UI | **Control（最强）** |
| 音频 | OpenAL / SDL | 自己接 | Stride.Audio（带总线） | **AudioBus（最强）** |
| 编辑器平台 | 无 | 无 | Windows-only | 三平台 |
| 维护 | 活跃，年更 | **活跃（2026-10）** | 活跃（4.4 大版本） | 活跃（4.7.2） |
| 生态规模 | 5263★ | 49★ | 中等 | 大 |

**分水岭只有一处：UI 和音频。** 其余维度上 NeoVeldrid 和 Stride.Graphics 打平，而 Godot
在这两项上明显领先、在互操作和 .NET 版本上明显落后。

本案的判据是这两个复杂度都偏高：`hud.js` 1623 行手搓且带屏幕空间 overlay，
`audio.js` 946 行自定义 DSP 且带着一批实测常数（`CLIP_KNEE` 0.7 / 阈值 -12 dBFS /
`FAR_CEIL` -20 dBFS / `MAX_VOICES` 48 / 3492 个节点释放的教训）。**音频尤其重**：
Stride 和 Godot 都能把那套总线结构直接对上，ImGui + OpenAL 手写则要重新实现并重测所有常数。

---

## 5. 未验证清单（每条都很可能翻盘，别跳过）

| # | 假设 | 验证方法 | 风险 |
| --- | --- | --- | --- |
| 1 | `Stride.Graphics` 单包能在 Linux 上裸起窗口并 Present | 单 `net10.0` 工程只引 `Stride.Graphics`，`GraphicsDevice.New()` + 一帧 | 高 |
| 2 | 零资产工程能绕开 AssetCompiler 构建 | 同上工程 `dotnet build`，不建 `Assets/` | 中 |
| 3 | Stride 接受原始 SPIR-V，或用 SDSL 重写 9 个程序的成本 | 把 `renderer.js` 里最小的一个（瓦片缝那个）移植一遍 | **最高** |
| 4 | NeoVeldrid 在 Linux + Vulkan 上跑通本机驱动 | 引 3 个包，画一批 instanced box | 低 |
| 5 | Godot 4.7 的 C# 模板 TFM | 新建工程读 csproj 的 `TargetFramework` | 低但影响 `Mech.Core` 多目标 |
| 6 | Godot 4.7 的 C# web 导出状态 | 查官方 release notes | 中（决定是否失去浏览器退路） |
| 7 | 16 km 场景的 far plane 与深度精度（两个引擎都要试） | far 拨到 32 km，看远处地形是否 z-fighting | 低 |
| 8 | 几千实例 per-frame 更新的实际开销 | 300 / 1000 / 3000 个 instanced box 各测一次帧时间 | 中 |
| 9 | `RenderingServer` 直驱能否完全避开场景树的帧开销 | 同上，改用 `instance_set_transform` 批量提交 | 中 |

**验证方式：一个探路程序，不碰本仓库。** 独立目录、`net10.0`、零资产，分别用
NeoVeldrid 和 Stride.Graphics 各起一次窗口、各画一批 instanced box、各接一个从现有 GLSL
改过来的 shader。跑完以后任务 1–4、7–8 全部变成数字。

---

## 6. 建议

### 6.1 一条硬约束：火控在客户端解算

这条先立，它决定了后面所有事。

准星、两个 lead ring、冲击点标记、射程读数，都是**客户端拿本地输入算出来的**：线上行的
`yaw` / `pitch` 是「往哪看」，不是「帮我瞄」。服务端负责仿真——弹道、命中、伤害、死亡；
客户端负责自己这台机器怎么打。

**「服务端算好预测、随快照下发」这条路已经否决**，两条理由：

1. **它会让服务器去拧炮塔。** lead 修正、目标吸附、冲击点一旦由服务端给出，客户端的炮塔
   角度就不再是玩家的输入，而是服务端的一个建议值——这些逻辑属于驾驶舱。
2. **它开出作弊面。** 任何被服务端算出来的「理想瞄准点」，都是改过的客户端可以直接读、
   也可以要求重算的东西；把「准星该多准」做成服务端接口，等于把瞄准也变成一条可以伪造的
   消息。**输入是输入，仿真才是权威**——这条边界一松，后面每一条防线都要重谈。

（顺带记录：带宽从来不是这条路的问题。冲击点 + 两组 lead 点 + 准星距离是十几个
float × 15 Hz，相对 `--check net` 的 67.8 kB/s 可以忽略。否决它的是权限归属，不是代价。）

所以目标形态是：

> 客户端火控与服务端 AI 火控**共用一部分代码**（同一份 `Ballistics` / 地形视线 /
> `solve_lead`），**共用一次运行是不存在的**（两个进程，各算各的）。同一份源码，两个宿主。

这句话有一个直接后果：**共享只能靠编译器，不能靠协议。** 客户端还是 JS 的今天，这 555 行
只能是一份手工维护的孪生实现，靠 `AGENTS.md` 的纪律维持；要让它从「纪律」变成「编译期
保证」，客户端必须能 `ProjectReference` 那个程序集——这就是 C# 客户端唯一不可替代的收益。

### 6.2 第一步：抽 `Mech.Core`（与引擎无关）

`Core/` `Systems/` `Modes/` `Ecs/` 目前和 `Program.cs` + `Net/` 挤在一个
`Microsoft.NET.Sdk.Web` 工程里。抽成一个类库之后，任何一个引擎客户端都能直接
`ProjectReference`，地形、弹道、defs、`Grid`、`Nav` 只有一份。

这一步同时也是 §6.1 的落点：`Core/Ballistics.cs` 与 `Systems/AI/Support/Engage.cs` 的
`solve_lead`、`Core/Terrain.cs` 的视线门，正是客户端火控要复用的那一份，它们进库之后
成为 AI 与客户端**同源的代码**，而不是两份要人工对齐的实现。

实测过它的外部依赖面，**很干净**：`Core/` + `Systems/` 里唯一的第三方引用是
`Microsoft.Extensions.Logging.Abstractions`（`Combat.cs` / `Seeker.cs` / `FirstHit.cs` /
`Mortality.cs` / `Modes/Range/Record.cs`），没有 ASP.NET 的东西。

建议多目标 `net8.0;net10.0`：`net10.0` 给服务端和 Stride/NeoVeldrid 客户端，
`net8.0` 留着 Godot 这条路（§5 #5 未定）。

这是纯重构，不碰玩法，`--check battle|range|intercept` 直接验证。

### 6.3 第二步：探路程序（§5），用数字代替讨论

成本不变，但它的**权重上升了**：6.2 之后，「孪生弹道」这件事仍然只剩 C# 客户端一条路，
所以换客户端不再只是口味问题，而是唯一能让那份共享真正成立的方案。

- 想消掉孪生弹道、想只留一套语言 → 这一步就是答案
- 想要脱机可分发的原生客户端 → 答案
- 想用引擎白拿 UI / 音频 / 后期 → 答案
- 想继续留在浏览器 → 接受 555 行孪生实现，把它当纪律资产维护；`Mech.Core` 照抽，留给
  将来的客户端和离线工具

### 6.4 档位结论

| 情况 | 选 |
| --- | --- |
| 客户端定为 C#，UI + 音频愿意自己扛（ImGui.NET + OpenAL/自写） | **NeoVeldrid** — 少写 2000 行、GLSL 直通、`net10.0`、headless 可自动验证 |
| 客户端定为 C#，想白拿 UI 控件 + 音频总线，接受 SDSL 或 SPIR-V 的不确定 | **Stride.Graphics** — 同档 + 可选引擎层，全 C# 无互操作 |
| 客户端要导入真美术资产、要编辑器工作流 | **Godot 4.7** — 代价是每帧互操作 + .NET 版本耦合 + 失去浏览器 |
| 客户端继续留在浏览器 | 保留 555 行孪生实现；`Mech.Core` 照抽，给离线工具和将来的客户端用 |

---

## 7. 来源与复核

全部在 **2026-11-10** 查得：

| 事实 | 来源 |
| --- | --- |
| Stride 4.4 / C# 14 / .NET 10 / Vulkan+D3D12 重写 | `curl -sSL https://www.stride3d.net/` |
| `Stride.Graphics 4.4.1` 的 TFM 与依赖清单 | `https://api.nuget.org/v3-flatcontainer/stride.graphics/4.4.1/stride.graphics.nuspec` |
| Stride 的平台表（编辑器 Windows-only） | `https://doc.stride3d.net/latest/en/manual/platforms/index.html` |
| Silk.NET `v2.23.0`（2026-01-22）与仓库活动 | `api.github.com/repos/dotnet/Silk.NET`（releases / pushed_at / stars） |
| Silk.NET 各包版本 | `api.nuget.org` flat container，`silk.net.vulkan` 等 |
| `Veldrid 4.9.0` 停止活跃、`NeoVeldrid 1.3.1`（2026-10-26） | nuget flat container + `jhm-ciberman/neo-veldrid` README 与仓库元数据 |
| NeoVeldrid 后端表、headless、MoltenVK、net10.0 | 同上 README + `neoveldrid.nuspec` |
| Godot 稳定版 `4.7.2`（2026-08-18） | `api.github.com/repos/godotengine/godot/releases` |
| Godot 编辑器三平台 / 目标平台 | `docs.godotengine.org` 平台文档 |

**已知的自查失败项**：Godot 4.7 的 C# 默认 `TargetFramework` 没查到（`Godot.NET.Sdk`
的 `Sdk.props` 不写默认值，模板生成器在 `godot` 主仓里的路径本次没定位到）。
`4.7.2` 与 `4.8.0-dev.7` 两个 SDK 包都翻过，都没有 TFM 字符串 —— 这条留给 §5 #5 实测。
