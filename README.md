# Conway's Game of Life — Unity 实现

用 Unity 6000.6.0f1 实现康威生命游戏（Conway's Game of Life）的核心逻辑，附带一套可复现的自动化验证。

![引擎](https://img.shields.io/badge/Unity-6000.6.0f1-black) ![测试](https://img.shields.io/badge/tests-24%20EditMode%20%2B%2011%20PlayMode-brightgreen)

---

## 1. 题目要求与完成情况

| 要求 | 状态 | 实现 / 证据 |
|---|---|---|
| 核心逻辑 | 完成 | `LifeSimulation.cs`，标准 B3/S23 规则 |
| 至少两种**稳定状态** | 完成 | 方块 BLOCK、蜂巢 BEEHIVE |
| 至少两种**振荡状态** | 完成 | 闪烁器 BLINKER、蟾蜍 TOAD（周期 2） |
| 至少两种**循环震荡状态** | 完成 | 脉冲星 PULSAR（周期 3）、十五周期振荡器 PENTADECATHLON（周期 15） |
| 进阶：实现效率 | 完成 | 双缓冲零分配；实测 7,600 万格/秒、每代 0.231 ms；详见 [技术分析](Assets/Docs/TechnicalAnalysis.md) |
| 文档：技术分析 | 完成 | [`Assets/Docs/TechnicalAnalysis.md`](Assets/Docs/TechnicalAnalysis.md) |
| 文档：实现文档 | 完成 | [`Assets/Docs/Implementation.md`](Assets/Docs/Implementation.md) |
| 文档：工作记录 | 完成 | [`Assets/Docs/PROJECT_LOG.md`](Assets/Docs/PROJECT_LOG.md)（含未决张力与决策依据）|

> **关于"循环震荡"的含义**：题目未定义其边界。本项目按**周期长度**分组（周期 2 vs 周期 ≥3）以便并排展示不同时间尺度。在生命游戏的通用术语中两者都属于"振荡器"，因此这只是展示分组，**不是对题目的权威解释**。无论采用哪种读法题目要求都被满足——详见技术分析 §3.2。

**8 个内置样本**全部由自动化测试断言其行为，声明与实际不会脱节：

| 样本 | 类型 | 周期 | 活细胞数 |
|---|---|---|---|
| 方块 BLOCK | 稳定状态 | 1 | 4 |
| 蜂巢 BEEHIVE | 稳定状态 | 1 | 6 |
| 闪烁器 BLINKER | 振荡状态 | 2 | 3 |
| 蟾蜍 TOAD | 振荡状态 | 2 | 6 |
| 脉冲星 PULSAR | 循环震荡 | 3 | 48 |
| 十五周期振荡器 PENTADECATHLON | 循环震荡 | 15 | 12 |
| 滑翔机 GLIDER | 飞船 | 4 | 5 |
| 轻型飞船 LWSS | 飞船 | 4 | 9 |

---

## 2. 快速开始

### 在编辑器中运行

1. 用 Unity **6000.6.0f1** 打开本目录。
2. 打开 `Assets/Scenes/SampleScene.unity`，点击 Play。
3. 界面会自动构建（无需手动挂载任何物体）：选择左侧样本 → 点「▶ 运行」。

> 场景里**没有**预先摆放任何 Life 相关物体。`LifeTerminalController` 通过
> `[RuntimeInitializeOnLoadMethod]` 在运行时自举，因此换任何场景都能启动。

### 操作方式

| 控件 | 作用 |
|---|---|
| ▶ 运行 / Ⅱ 暂停 | 开始或暂停自动演化 |
| ▸ 单步 | 前进一代 |
| ↺ 重置 | 重新载入当前样本 |
| 速率 | 1–20 代/秒 |
| 边界条件 | 固定边界 / 环绕边界（环形拓扑）|
| 随机播种 / 清空 | 生成随机活细胞 / 清空网格 |
| 网格上拖拽 | 手动绘制细胞（左键涂画）|

---

## 3. 自动化验证

本项目把"样本行为"变成了可机器检查的断言，而不是只靠肉眼观察。

### 运行测试

```powershell
unity test . --mode EditMode --output test-results-editmode.xml
unity test . --mode PlayMode --output test-results-playmode.xml
```

或在 Unity 中打开 **Window → General → Test Runner** 分别运行 EditMode / PlayMode。

**当前结果：EditMode 24/24 通过，PlayMode 11/11 通过。**

### 测试覆盖了什么

- **规则正确性**：`Step()` 与一份独立重写的 B3/S23 参考实现，在随机棋盘（含 1×1 到 12×12 各种尺寸）上逐格比对，固定边界与环绕边界各 100 个随机盘面。
- **样本行为**：稳定状态 4 代不变；周期 2 与周期 ≥3 振荡器在**绝对坐标**下"恰好"于声明周期逐格复原，并断言中途**没有**提前复原；飞船的**完整活细胞集合**必须按同一位移整体平移（不是只看细胞数与包围盒）。
- **边界语义**：环绕与非环绕必须产生不同结果；边缘上的闪烁器在环绕下保持 3 个活细胞。
- **簿记**：世代计数、存活数统计、越界写入忽略、`Clear`/`LoadCentered`/`Randomize` 的状态重置。
- **界面自举**（PlayMode）：运行时自举必须装配出完整界面树、成功加载主题样式、按样本数生成按钮，且**不产生任何错误日志**——任何一条 `Debug.LogError` 都会让测试失败。这层专门用来拦住"样式表损坏 / 资源缺失 / Awake 空引用"这类只在运行时暴露的问题。
- **界面交互**（PlayMode）：用真实按钮事件驱动，断言「单步」推进世代计数、「载入样本」刷新存活数、运行/暂停切换状态文本；并**跨真实帧验证时钟真的在推进**（运行后世代必须增长，暂停后必须不变）。
- **布局适配**（PlayMode）：每个样本按钮必须落在其面板内且不与边界下拉框重叠；面板空间与屏幕像素两种坐标必须正确换算，机器区域必须完整落在可见视口内。
- **运行时性能采样**（PlayMode）：经真实 `Update()` 时钟测量每代耗时并断言其远低于 60 fps 帧预算。

### 独立校验工具（可选）

仓库根目录的 `.verify/` 是一个独立的 .NET 控制台工程，它**直接链接** `Assets/Scripts` 下的真实源码，因此不需要打开 Unity 就能验证逻辑：

```powershell
dotnet run --project .verify/Verify.csproj -c Release                    # 校验全部样本的周期
dotnet run --project .verify/Verify.csproj -c Release -- rules           # 规则引擎 vs 独立参考实现
dotnet run --project .verify/Verify.csproj -c Release -- bench           # 性能基准
dotnet run --project .verify/Verify.csproj -c Release -- probe           # 从 RLE 解码并校验规范样本
dotnet run --project .verify/Verify.csproj -c Release -- selftest        # 证伪：校验器必须拒绝假声明
```

所有子命令都把结论传递到**进程退出码**（`0` 通过 / `1` 校验失败 / `2` 用法错误或数据无意义），可直接接入 CI。

它当初就是用来发现"我手写的坐标是错的"这一问题的——见技术分析中关于样本来源的说明。

---

## 4. 项目结构

```
Assets/
├── Scripts/
│   ├── ConwayGameOfLife.Runtime.asmdef
│   ├── LifeSimulation.cs          # 纯 C# 规则引擎（不依赖 UnityEngine）
│   ├── LifePatterns.cs            # 8 个内置样本及其分类
│   ├── LifeGridElement.cs         # 用 Painter2D 自绘网格 + 鼠标编辑
│   └── LifeTerminalController.cs  # UI Toolkit 界面装配与演化驱动
├── Tests/
│   ├── EditMode/                  # 规则、边界、簿记、样本行为（24 项）
│   │   ├── ConwayGameOfLife.Tests.EditMode.asmdef
│   │   ├── LifeSimulationTests.cs
│   │   └── LifePatternTests.cs
│   └── PlayMode/                  # 界面自举、交互、布局适配、性能采样（11 项）
│       ├── ConwayGameOfLife.Tests.PlayMode.asmdef
│       └── LifeTerminalBootstrapTests.cs
├── Resources/
│   ├── LifeTerminal.uss           # 终端风格样式
│   └── LifeRuntimeTheme.tss       # 主题入口
└── Docs/
    ├── TechnicalAnalysis.md       # 技术分析：规则、架构、复杂度、性能实测
    ├── Implementation.md          # 实现文档：模块、数据流、扩展方式
    └── PROJECT_LOG.md             # 工作记录：声称→证据对照、决策、未决张力

.verify/                            # 独立校验工具（不属于 Unity 工程）
```

> `.verify/`、`test-results-*.xml` 与根目录的图表工件都不在 `Assets/` 下，
> 不会被 Unity 导入，也不进入构建产物。

---

## 5. 已知边界与后续可做的事

- **渲染成本尚未采样**：规则推进每代仅 0.231 ms，但 `LifeGridElement.DrawGrid` 每次重绘会为全部 6,144 个格子逐个构建路径。**在没有 Profiler 数据前不要优化规则内核**——先分别采样「规则推进 / 网格重绘 / UI 布局」三段。若将来优化绘制，必须保留死细胞的网格底纹外观。
- **环绕边界有性能代价**：实测取模运算使**吞吐下降 39.5%**（7,600 万 → 4,600 万格/秒），换算成**每代耗时增加 65.4%**。优化方式是"幽灵边框"（ghost border）而非逐邻居取模。
- **窄窗口的响应式布局未实现**：内容实测需 949px 而参考高度为 900px，已做多处收紧并给样本列表加滚动；更矮的窗口会优先挤压装饰性的页脚。真正的窄屏纵向堆叠需要 USS 断点或运行时切换类名。详见 [工作记录](Assets/Docs/PROJECT_LOG.md) 的 T7。
- **样本数量**：目前 8 个。若要展示更多经典结构（如 Gosper 滑翔机枪、繁殖者），
  `LifePatterns.All` 追加一项即可，测试会自动覆盖新条目。
