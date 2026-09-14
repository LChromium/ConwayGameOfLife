# 阶段归档 / STAGE ARCHIVE

本文件记录**本阶段可运行版本**的确切身份，以便任何人（或未来的我）能回到这个状态、
复现验证、并清楚知道哪些结论已经验过、哪些仍然未知。

> **阶段归档 ≠ 发布版性能验收。** 本文档记录的构建是**开发版**，
> 用于截图与性能采样；它**不是**发布版，性能结论也不构成对发布版的验收。
> 「规则推进 / 网格重绘 / UI 布局」的分项成本**仍未测量**（见 §6）。

---

## 1. 版本位置

| 项 | 值 |
|---|---|
| Git 标签 | `stage-1-life-terminal` |
| Unity Editor | **6000.6.0f1** (f7f8ed4d1e24) |
| 渲染管线 | Universal RP 17.6.0（2D Renderer）|
| 目标平台 | StandaloneWindows64 |
| 构建类型 | **Development Build**（`BuildOptions.Development`）|
| 构建入口 | `ConwayGameOfLife.EditorTools.PlayerBuild.BuildWindows64` |
| 本阶段最后一次提交 | 见 `git describe --tags` / `git log stage-1-life-terminal -1` |

> 提交号刻意不写死在本文档里：写死了就必然与标签指向的实际提交脱节。
> 用 `git describe --tags --always` 取，或直接查看标签。

---

## 2. 启动入口

### 编辑器内（推荐用于查看界面）

1. 用 Unity **6000.6.0f1** 打开本仓库根目录。
2. 打开 `Assets/Scenes/SampleScene.unity`，点 Play。
   场景里**没有**预先摆放任何物体——`LifeTerminalController` 通过
   `[RuntimeInitializeOnLoadMethod]` 在运行时自举，换任何场景都能启动。

### 构建并运行 Player

```powershell
& "C:\Program Files\Unity\Hub\Editor\6000.6.0f1\Editor\Unity.exe" `
    -batchmode -quit -projectPath . `
    -executeMethod ConwayGameOfLife.EditorTools.PlayerBuild.BuildWindows64 `
    -logFile "$env:TEMP\build.log"

Builds\LifeTerminal.exe -screen-width 1280 -screen-height 720
```

用 `-lifeLayoutProbe` 启动会额外采集布局与帧成本数据、截图，然后退出：

```powershell
# 默认样本（脉冲星）
Builds\LifeTerminal.exe -screen-width 1280 -screen-height 720 -lifeLayoutProbe

# 指定样本（按 EnglishName 精确匹配）
Builds\LifeTerminal.exe -screen-width 600 -screen-height 1000 -lifeLayoutProbe -lifePattern PENTADECATHLON
```

**退出码**：`0` 成功记录；`3` 样本选择失败（未知名称 / 按钮未就绪）——此时**不写任何测量记录**。

### 测试与独立校验

```powershell
unity test . --mode EditMode --output test-results-editmode.xml
unity test . --mode PlayMode --output test-results-playmode.xml

dotnet run --project .verify/Verify.csproj -c Release              # 样本周期校验
dotnet run --project .verify/Verify.csproj -c Release -- rules     # 规则 vs 独立参考实现
dotnet run --project .verify/Verify.csproj -c Release -- bench     # 性能基准
dotnet run --project .verify/Verify.csproj -c Release -- selftest  # 证伪校验器自身
```

---

## 3. 已验证内容

全部为**本阶段实测**，可复现。

### 功能

| 项 | 证据 |
|---|---|
| 标准 B3/S23 规则 | 与独立重写的参考实现在 400 个随机盘面（1×1–12×12，两种边界）逐格比对，**0 处不一致** |
| 8 个内置样本行为正确 | 独立工具 `RESULT: all patterns behave as declared.`，周期与分类逐项断言 |
| 稳定 2 / 振荡 2 / 循环震荡 2 / 飞船 2 | `Archive_CoversEveryRequiredCategory` + 各行为测试 |
| 界面可运行、无错误日志 | PlayMode 测试；Console 零 `error CS` / USS 错误 / 缺失字体 |

### 测试

**EditMode 37/37 通过，PlayMode 13/13 通过**（`test-results-editmode.xml`、`test-results-playmode.xml`）。

### 性能（各自出处已标注）

| 数据 | 值 | 出处 | 性质 |
|---|---|---|---|
| 规则吞吐 | 7,600 万格/秒（512×512 固定边界）| `.verify -- bench` | 独立 .NET，**非 Unity** |
| 规则每代成本 | 0.206 ms/代（96×64 固定边界）| `.verify -- bench` | 独立 .NET |
| 微基准每代成本 | 0.14–0.23 ms/代 | `[perf-microbench]` 日志行 | **SYNTHETIC**，含同帧复用 deltaTime 与 SendMessage 开销 |
| Player 暂停 | 8.5 ms/帧 | `player-measurements.jsonl` | 开发版 Player，240 帧 |
| Player 演化中 | 10.6–10.9 ms/帧 | 同上 | 同上；采样期间确认推进 50–52 代 |
| 时钟达成速率 | ≈20 代/秒（请求 20）| 同上 | 三个分辨率一致 |

环绕边界的代价：**吞吐下降 39.5%**，等价于**每代耗时增加 65.4%**（同一现象的两个方向）。

### 布局与画面

| 项 | 证据 |
|---|---|
| 目标分辨率两栏布局 | `player-1280x720.png`、`player-1920x1080.png`（真实 Player）|
| 竖屏触发堆叠布局、操作栏完整 | `player-600x1000.png` |
| 最长样本名标题不裁切 | `player-title-acceptance.jsonl` + `*-longname.png`（首选 215.3 / 可用 218.0）|
| 面板缩放模型 | `PanelScreenFit` 预测与 Player 实测偏差 **0.00**（三种分辨率）|

---

## 4. 随版本保存的证据

```
Screenshots/
├── player-1280x720.png            实机画面（脉冲星）
├── player-1920x1080.png           实机画面（脉冲星）
├── player-600x1000.png            实机画面（竖屏 / 堆叠布局）
├── player-1280x720-longname.png   标题验收（十五周期振荡器）
├── player-600x1000-longname.png   标题验收（竖屏）
├── player-measurements.jsonl      暂停/演化帧成本原始记录（3 行）
└── player-title-acceptance.jsonl  标题测量原始记录（2 行）

Assets/Docs/
├── TechnicalAnalysis.md   技术分析（规则、复杂度、性能实测、未测清单）
├── Implementation.md      实现文档（模块、数据流、扩展方式）
└── PROJECT_LOG.md         工作记录（声称→证据对照、决策、张力清单 T1–T15）

test-results-editmode.xml / test-results-playmode.xml   测试报告
```

---

## 5. 保留问题

详见 `PROJECT_LOG.md` 的张力清单。影响使用的重点：

| # | 问题 | 状态 |
|---|---|---|
| T8 | **分项成本未测量**（规则 / 布局 / 绘制各占整帧多少）| 未测。**因此不指定优化方向** |
| T15 | 首次运行的一次性长帧（单帧 86.9 秒）| 原因未定位；制作人观察到与**窗口失焦**相关、重新聚焦后恢复。已用热身隔离，暂不深查 |
| T5 | 从未产出**发布版**构建 | 未测 |
| T7 | 极窄窗口 | 已实现 compact 断点并验收；更极端比例未穷举 |
| T11 | Player 探针需 Development Build | 已知并记录（`#if DEVELOPMENT_BUILD`）|
| T14 | 标题余量约 3 px | 已验收不裁切；改样本名或字号需重跑 |

**不要**在缺少 Profiler 分项数据的情况下实施 Burst / GPU / 位打包优化。

---

## 6. 本阶段明确未做的事

- **未做**：Unity Profiler 分项采样（规则推进 / UI Toolkit 布局 / Painter2D 绘制）。
- **未做**：发布版（非 Development）构建与性能验收。
- **未做**：玩法内容（放置预览、旋转、碰撞、关卡）。
- **未做**：渲染优化。`LifeGridElement.DrawGrid` 仍逐格绘制；若将来优化，
  **必须保留死细胞的网格底纹外观**，不能退化成纯黑底加亮点。

---

## 7. 阶段结论

核心逻辑与测试体系达到可靠水平；界面完成一次真实画面验收；
性能仅完成**规则推进**与**整帧间隔**的测量，**分项归因未做**。

下一阶段在**独立分支**上开发玩法（选择样本 → 放置预览 → 旋转 → 放下 → 运行观察碰撞），
沿用现有规则内核，不改动 `LifeSimulation`。历史证据保留，不清理、不重写。
