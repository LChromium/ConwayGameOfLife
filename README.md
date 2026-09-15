# Conway's Game of Life — Unity 实现

用 Unity 6000.6.0f1 实现康威生命游戏（Conway's Game of Life）的核心逻辑，附带一套可复现的自动化验证。

![引擎](https://img.shields.io/badge/Unity-6000.6.0f1-black) ![测试](https://img.shields.io/badge/tests-78%20EditMode%20%2B%2039%20PlayMode-brightgreen)

---

## 1. 题目要求与完成情况

| 要求 | 状态 | 实现 / 证据 |
|---|---|---|
| 核心逻辑 | 完成 | `LifeSimulation.cs`，标准 B3/S23 规则 |
| 至少两种**稳定状态** | 完成 | 方块 BLOCK、蜂巢 BEEHIVE |
| 至少两种**振荡状态** | 完成 | 闪烁器 BLINKER、蟾蜍 TOAD（周期 2） |
| 至少两种**循环震荡状态** | 完成 | 脉冲星 PULSAR（周期 3）、十五周期振荡器 PENTADECATHLON（周期 15） |
| 进阶：实现效率 | 完成 | 双缓冲零分配；独立工具实测 7,600 万格/秒、0.206 ms/代；详见 [技术分析](Assets/Docs/TechnicalAnalysis.md) |
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

**当前结果：EditMode 86/86 通过，PlayMode 60/60 通过。**

### 测试覆盖了什么

- **规则正确性**：`Step()` 与一份独立重写的 B3/S23 参考实现，在随机棋盘（含 1×1 到 12×12 各种尺寸）上逐格比对，固定边界与环绕边界各 100 个随机盘面。
- **样本行为**：稳定状态 4 代不变；周期 2 与周期 ≥3 振荡器在**绝对坐标**下"恰好"于声明周期逐格复原，并断言中途**没有**提前复原；飞船以**归一化后的完整形状**判定——要求首次复现即声明周期（把 LWSS 的周期 4 写成 8 会被拒绝，该反例本身也是测试）。
- **边界语义**：环绕与非环绕必须产生不同结果；边缘上的闪烁器在环绕下保持 3 个活细胞。
- **簿记**：世代计数、存活数统计、越界写入忽略、`Clear`/`LoadCentered`/`Randomize` 的状态重置。
- **界面自举**（PlayMode）：运行时自举必须装配出完整界面树、成功加载主题样式、按样本数生成按钮，且**不产生任何错误日志**——任何一条 `Debug.LogError` 都会让测试失败。
- **界面交互**（PlayMode）：用真实按钮事件驱动；**跨真实帧验证时钟**（请求 20 代/秒，实测达成 20.0）；**编辑真实棋盘**（保留控制器的模拟与回调）后断言暂停状态、棋盘人口与读数三者一致。
- **布局适配**（PlayMode + 单测）：样本列表为 `ScrollView`，逐项滚动到可见并可选中，且视口不遮挡边界控件；面板空间与屏幕像素两种坐标正确换算。跨分辨率适配由 [`PanelScreenFit`](Assets/Scripts/PanelScreenFit.cs) 纯函数 + 单元测试覆盖（1280×720 / 1920×1080 / 2560×1440 / 1440×900 / 1920×1200）。
- **性能口径**（PlayMode）：跨帧测时钟速率，并在同一测试里用**测试自建的参考后端**量规则步进成本。原先那个 SYNTHETIC 人工调用微基准已在阶段 D **删除**：它测的是「同步 `Update()` 内的推进」，而后台化之后同一条循环只会测到提交成本；引用它的历史文档已标注为「历史记录、不再可复现」。
- **时钟过载与运行状态**（PlayMode）：用一个每步固定耗时的慢后端，验证每帧推进上限、长帧不放大追赶、暂停/重置清掉欠账与速率报告、恢复不补算旧账；时钟一律**通过真实按钮**（`▶ 运行` / `Ⅱ 暂停`）驱动。界面自报速率与**同一起止时刻的完成世代差 ÷ 墙钟**对照，文档同时写明这个 0.5 秒窗口只有约 2 代/秒的分辨力。
- **后台演化**（PlayMode，`LifeBackgroundEvolutionTests` 17 条）：CPU 规则在 worker 上算，主线程只接管完整世代。真线程池下验证「计算期间主线程读到的仍是已接管的那一代」与「算完但未接管的棋盘不可见」；用**可控任务**（调度器与规则步进都可注入）验证暂停冻结＋暂存、恢复按顺序接管、单步优先接管、重置/切换后端/销毁不让旧结果落地；**边界切换**（在途 / 待接管两种时机）与**任务失败**（先推进再抛异常）都要求「拒绝之后从显示棋盘重建」，并与同步参考逐格一致。三条证伪各只改一处，分别让 5 条、2 条、2 条失败。

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

## 4. 开发环境说明

### 网络代理（本机配置，不属于项目）

本机直连 `github.com:443` 会被重置（`Recv failure: Connection was reset`），推送需要走本机代理。
**这属于开发环境配置，故意不写入仓库**（仓库配置会随克隆分发到其他机器，而其他机器未必有同样的代理）：

```powershell
# 仅在需要时对单次命令生效，不污染仓库配置
git -c http.proxy=http://127.0.0.1:7897 push origin main
```

若报 `schannel: failed to receive handshake, SSL/TLS connection failed`，
改用 OpenSSL 后端即可绕过（本机 Windows schannel 经该代理握手失败）：

```powershell
git -c http.proxy=http://127.0.0.1:7897 -c https.proxy=http://127.0.0.1:7897 `
    -c http.sslBackend=openssl push origin main
```

若你的环境可直连 GitHub，则无需任何配置。

### 复现验证所需

| 项 | 版本 / 说明 |
|---|---|
| Unity Editor | 6000.6.0f1（含 Windows Build Support 模块，生成 Player 截图时需要）|
| .NET SDK | 8.0+（运行 `.verify` 独立校验工具）|
| 可选 | 本机代理，仅在直连 GitHub 失败时用于推送 |

---

## 5. 画面验收与实机测量

`Screenshots/` 是从**真实 Windows Player 构建**中捕获的画面与测量数据，用于确认响应式布局与可读性。
PlayMode 测试宿主固定在 640×480，无法代表目标分辨率，因此这部分必须由 Player 产出。

| 文件 | 内容 |
|---|---|
| `player-1280x720.png` | 1280×720 实机截图（脉冲星样本）|
| `player-1920x1080.png` | 1920×1080 实机截图（脉冲星样本）|
| `player-measurements.jsonl` | 两个分辨率的布局测量与帧时间原始数据 |

复现方式：

```powershell
unity command build --project-path . --target StandaloneWindows64 --outputPath Builds/LifeTerminal.exe --confirm
Builds\LifeTerminal.exe -screen-width 1280 -screen-height 720 -lifeLayoutProbe
Builds\LifeTerminal.exe -screen-width 1920 -screen-height 1080 -lifeLayoutProbe
```

测量结果存放在 `%USERPROFILE%\AppData\LocalLow\DefaultCompany\ConwayGameOfLife\layout-probe\`。

**实测摘要**（两个分辨率完全一致，说明设计空间是尺度不变的）：

| 指标 | 1280×720 | 1920×1080 |
|---|---|---|
| 可见设计空间 | 1600×900 | 1600×900 |
| `PanelScreenFit` 预测 | 1600×900 | 1600×900 |
| **预测 vs 实测偏差** | **0.00 / 0.00** | **0.00 / 0.00** |
| 两栏布局（compact 关闭）| 是 | 是 |
| 网格高度 | 546 px | 544 px |
| 平均帧时间 / 帧率 | 6.25 ms / 160 fps | 6.24 ms / 160 fps |

> 帧时间来自**真实 Player 的空转采样**（240 帧，无合成调用），包含渲染与 UI 更新；
> 这与 `TechnicalAnalysis.md` §5.3 中标注为 SYNTHETIC 的规则引擎微基准是两回事，不可混用。

---

## 6. 项目结构

```
Assets/
├── Scripts/
│   ├── ConwayGameOfLife.Runtime.asmdef
│   ├── LifeSimulation.cs          # 纯 C# 规则引擎（不依赖 UnityEngine）
│   ├── LifePatterns.cs            # 8 个内置样本及其分类
│   ├── ILifeBackend.cs            # 演化后端接口（CPU / GPU 同构）
│   ├── ILifeAsyncBackend.cs       # 后台后端接口：在途/待接管/接管结果（含版本、世代、人口）
│   ├── CpuLifeBackend.cs          # 参考后端（同步，仍是测试与基准的参照物）
│   ├── LifeAsyncCpuBackend.cs     # 阶段 D：CPU 规则跑在 worker 上，主线程只接管完整世代
│   ├── GpuLifeBackend.cs          # Compute Shader 后端
│   ├── LifeBoardRenderer.cs       # 状态缓冲 → 视口贴图的公共显示路径
│   ├── LifeGridElement.cs         # 网格显示 + 鼠标编辑 + 琥珀色预览
│   ├── LifeNoiseSeeding.cs        # 纯函数 fBm + 域扭曲播种（CPU，可复现）
│   ├── LifeSeedingSession.cs      # 参数 / 候选 / 后台生成状态机（纯 C#）
│   ├── PanelScreenFit.cs          # 面板缩放数学（纯函数，可跨分辨率单测）
│   ├── LifeTerminalController.cs  # UI Toolkit 界面装配与演化驱动
│   ├── LifePerfProbe.cs           # Player 内 `-lifePerf` 测量探针（阶段 A）
│   ├── LifeBoardBench.cs          # Player 内 `-lifeBench` 大棋盘基准（阶段 C，五口径分开）
│   ├── LifeBenchStatistics.cs     # 基准统计（中位数/十分位，纯函数，可单测）
│   └── RuntimeLayoutProbe.cs      # Player 内 `-lifeLayoutProbe` 布局校验探针
├── Tests/
│   ├── EditMode/                  # 规则、边界、簿记、样本行为、噪声、会话状态机、基准统计（86 项）
│   │   ├── ConwayGameOfLife.Tests.EditMode.asmdef
│   │   ├── LifeSimulationTests.cs
│   │   ├── LifePatternTests.cs
│   │   ├── LifeNoiseSeedingTests.cs
│   │   ├── LifeSeedingSessionTests.cs
│   │   └── LifeBenchStatisticsTests.cs
│   └── PlayMode/                  # 界面自举、交互、布局与文字适配、性能口径、时钟过载、后台演化（60 项）
│       ├── ConwayGameOfLife.Tests.PlayMode.asmdef
│       ├── LifeTerminalBootstrapTests.cs
│       ├── LifeSeedingIntegrationTests.cs
│       ├── LifeClockOverloadTests.cs
│       ├── LifeBackgroundEvolutionTests.cs
│       └── GpuCpuEquivalenceTests.cs
├── Editor/
│   ├── PlayerBuild.cs             # Player 构建入口：开发版 / 帧时间版 / 发布版
│   └── CaptureLayoutTool.cs       # 编辑器内布局截图诊断
├── link.xml                       # 保留运行时探针，防止托管剥离移除
├── Resources/
│   ├── LifeGpu.compute            # Step / Render 两个 kernel
│   ├── LifeTerminal.uss           # 终端风格样式
│   └── LifeRuntimeTheme.tss       # 主题入口
└── Docs/
    ├── TechnicalAnalysis.md       # 技术分析：规则、架构、复杂度、性能实测
    ├── Implementation.md          # 实现文档：模块、数据流、扩展方式
    ├── StageA-Gpu.md              # 阶段 A：GPU 演化与显示
    ├── StageB-Seeding.md          # 阶段 B：fBM + 域扭曲概率播种
    ├── StageC-Benchmark.md        # 阶段 C：大棋盘基准（生成/上传/演化/显示/内存）
    ├── StageD-BackgroundEvolution.md # 阶段 D：CPU 演化移到后台（仍不承诺吞吐提升）
    └── PROJECT_LOG.md             # 工作记录：声称→证据对照、决策、未决张力

Screenshots/                        # 真实 Player 截图与原始测量记录
Tools/                              # 截图脚本（客户区抓图，DPI 感知）
.verify/                            # 独立校验工具（不属于 Unity 工程）
```

> 阶段 C 的基准原始记录在仓库根目录：`stage-c-bench-r5.jsonl`（当前）、`stage-c-bench-r4.jsonl`、
> `stage-c-bench-r3.jsonl`、`stage-c-bench-r2.jsonl` 与 `stage-c-bench.jsonl`（前几轮，**原样保留**；
> 被修正的说法逐条列在文档 §0）。每条记录自带 `recordRound` / `buildGuid` / `dataPath`
> 以及各字段自己的可用性说明，因此一个数字来自哪个配置、哪个场景不需要靠文件名猜。
> r4 只重跑了受影响的帧场景（`-lifeBenchScenarios frame`），其余口径在记录里写 `null`
> 并列进 `phasesSkipped`——**没测就是没测，不写 0**；r5 另加了后台计算/结果复制/整盘上传
> 与 `pauseResponse` 三笔分开的成本。

> `.verify/`、`test-results-*.xml` 与根目录的图表工件都不在 `Assets/` 下，
> 不会被 Unity 导入，也不进入构建产物。

---

## 7. 归档清单（阶段一）

本阶段可运行版本已归档。**完整信息见 [`Assets/Docs/StageArchive.md`](Assets/Docs/StageArchive.md)。**

| 项 | 内容 |
|---|---|
| **版本位置** | Git 标签 `stage-1-life-terminal`（提交号用 `git describe --tags` 取，不写死在文档里）|
| **Unity 版本** | 6000.6.0f1（URP 17.6.0 / 2D Renderer，StandaloneWindows64）|
| **构建类型** | **Development Build** —— 探针受 `#if DEVELOPMENT_BUILD` 保护 |
| **启动入口** | 编辑器：打开 `Assets/Scenes/SampleScene.unity` 按 Play（运行时空场景自举）<br>Player：`ConwayGameOfLife.EditorTools.PlayerBuild.BuildWindows64` 构建后运行 `Builds/LifeTerminal.exe` |
| **测试报告** | EditMode **37/37**、PlayMode **13/13**（`test-results-*.xml` 随版本保存）|
| **画面验收** | `Screenshots/` 下 5 张真实 Player 截图（1280×720 / 1920×1080 / 竖屏 / 两组长标题）|
| **原始测量** | `Screenshots/player-measurements.jsonl`（帧成本）、`player-title-acceptance.jsonl`（标题）|
| **保留问题** | T8 分项成本未测、T15 首次运行长帧（观察到与窗口失焦相关）、T5 无发布版构建 |

> **阶段归档 ≠ 发布版性能验收。** 记录的是开发版构建；且「规则推进 / 网格重绘 / UI 布局」
> 的分项成本**仍未测量**，因此**不指定优化方向**。

> **阶段 B（fBM + 域扭曲概率播种）已归档**，标签 `stage-b-life-seeding`（主体验收）与
> `stage-b-life-seeding-r2`（请求身份补丁）；清单、证据与保留问题见
> [`Assets/Docs/StageB-Seeding.md`](Assets/Docs/StageB-Seeding.md) §11。
> 主体验收通过，范围已关闭，不再扩展。

> **阶段 C 进行中**：大棋盘基准（256²/1024²/2048²/4096²）把生成、上传、演化、显示、内存占用
> **五个口径分开记录**，见 [`Assets/Docs/StageC-Benchmark.md`](Assets/Docs/StageC-Benchmark.md)
> 与 `stage-c-bench-r5.jsonl`。两个关键数字：**4096² 的 fBm 生成约 8.9 秒**（同步生成函数耗时，
> 不是端到端等待），**同尺寸下只有 1.67% 的盘面可见**。
> **时钟过载保护已实现**（每帧推进上限＋步间预算，超限丢弃追赶欠账、不跳过演化步骤）；
> 暂停/重置/切后端**结束整段时钟状态**（欠账、速率窗口、自报速率、过载标志），
> 窗口形成前界面显示「采样中」而不是把初始化 0 当成实测；界面速率与墙钟实测并列记录。
>
> **阶段 D 已实施**：CPU 规则移到 worker，主线程只接管完整世代，见
> [`Assets/Docs/StageD-BackgroundEvolution.md`](Assets/Docs/StageD-BackgroundEvolution.md)。
> **计算期间界面可响应**：CPU 后端场景的帧中位从 2048² 的 206.4 ms / 4096² 的 739.2 ms
> 降到 **0.27 ms**；暂停命令 ≤0.06 ms，在途的一代算完后进等待槽、恢复时按顺序接管（五轮实机一致）。
> **仍然存在的**：主线程的整盘复制与上传是**已测得的显著阻塞来源**（2048² 13.5 ms、4096² 50–56 ms；
> 帧最大 19.7/63–72 ms）——与最差帧同量级、时间上相容，但**本轮没有证明它解释了最差帧的全部耗时**；
> 另有每代新增的结果复制（4096² 约 79 ms）。**吞吐没有提升**（worker 步进与同步参考同量级，−12%~+10%，
> 参考值自身两轮间波动 ±10%）。
> **边界切换、失败与恢复已闭合**：规则改变或结果被拒之后，worker 一律**从显示棋盘重建**；
> 任务失败按会话身份处理，当前失败停止自动提交、显示停在最后一个完整世代，只有显式重试或换盘才清除。
> 「只准备并上传可见区域」是评审认可的**下一阶段独立设计**（要保持视口内容、人口与世代一致，
> 并明确平移后如何取得新区域），本轮不做——Unity 图形资源操作不能直接套一层 `Task.Run`。
> 亚像素密度总览按评审意见**暂缓**；GPU 独立生成噪声仍是**远期可选实验**；CPU 单生成器保留。

---

## 8. 已知边界与后续可做的事

- **分项成本仍未测量**：规则推进本身很便宜（独立工具 0.206 ms/代），但「规则推进 / UI Toolkit 布局 / Painter2D 绘制」各占整帧多少**尚未测量**。因此**不指定优化方向**——在取得 Profiler 分项数据前，不实施 Burst / GPU / 位打包。若将来优化绘制，必须保留死细胞的网格底纹外观。
- **环绕边界有性能代价**：实测取模运算使**吞吐下降 39.5%**（7,600 万 → 4,600 万格/秒），换算成**每代耗时增加 65.4%**。优化方式是"幽灵边框"（ghost border）而非逐邻居取模。
- **窄窗口**：已实现代码驱动的 compact 断点（USS 无媒体查询，由 `GeometryChangedEvent` 切换类名），竖屏 600×1000 已实机验收，操作栏完整可见。详见 [工作记录](Assets/Docs/PROJECT_LOG.md) 的 T7 / T13。
- **样本数量**：目前 8 个。若要展示更多经典结构（如 Gosper 滑翔机枪、繁殖者），
  `LifePatterns.All` 追加一项即可，测试会自动覆盖新条目。
- **首次运行存在未解释的长帧**：一次未热身运行记录到单帧 86.9 秒，原因未知、后续未复现。
  采样已改为先热身并丢弃，但根因未定位（T15）。
