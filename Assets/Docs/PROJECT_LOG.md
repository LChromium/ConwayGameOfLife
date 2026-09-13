# 项目工作记录 / PROJECT LOG

本文件是可溯源的工作主线。每条记录都标明"声称"与"证据"的对应关系——**凡是写进本文件的结论，都必须能由一条可复现的命令验证**。

- [R2 · 2026-09-13](#r2--2026-09-13--实现生命游戏核心-样本验证与文档)
- [R1 · 2026-09-10](#r1--2026-09-10--初次签入)
- [决策记录](#决策记录跨轮次生效)
- [张力清单](#张力清单未决问题)
- [镜链原则](#镜链原则)

---

## R2 · 2026-09-13 — 实现生命游戏核心、样本验证与文档

**起点**：上一轮（R1）只留下了骨架与一份架构图，没有任何文档与测试。外部评审（GPT）在接手前先做了一次"感悟"，指出三处缺口，其中一条在本轮被证实。

### 本轮声称 → 证据对照

| # | 声称 | 证据（可复现） | 状态 |
|---|---|---|---|
| 1 | 6 个内置样本全部行为正确 | `dotnet run --project .verify/Verify.csproj -c Release` → `RESULT: all patterns behave as declared.` | 已证实 |
| 2 | 规则引擎 = 标准 B3/S23 | `-- rules` → 400 个随机盘面（1×1–12×12，两种边界）与独立参考实现 **0 处不一致** | 已证实 |
| 3 | 覆盖题目三类要求 | EditMode `Archive_CoversEveryRequiredCategory`；稳定 2 + 振荡 2 + 循环震荡 2 + 飞船 2 = 8 | 已证实 |
| 4 | 界面能正常构建与交互 | PlayMode 6/6；含真实按钮事件驱动与"布局非零尺寸"断言 | 已证实 |
| 5 | Console 无错误 | `unity run .` 后 `Select-String -Path Logs\Editor.log -Pattern 'error CS\|USS parsing error\|Font not found'` → 空 | 已证实 |
| 6 | 效率达到"进阶要求" | `-- bench` → 固定边界 ~7,600 万格/秒；环绕边界慢 41% | 已证实（数据见技术分析 §5） |
| 7 | 从全新检出即可复现 | `git clone` 到空目录后跑上述命令，全部通过（含 Unity 冷启动） | 已证实 |

> **第 7 条的完整证据**（无 `Library/` 缓存的冷启动）：
> ```
> dotnet run --project .verify/Verify.csproj -c Release   → RESULT: all patterns behave as declared.
> unity run .                                             → 编译错误：NONE
> unity test . --mode EditMode                            → total=23 passed=23 failed=0
> unity test . --mode PlayMode                            → total=6  passed=6  failed=0
> ```
> 该检查发现并修掉了 T6（见张力清单）——它是本轮唯一由外部评审发现、而非自测发现的问题。

### 本轮实际改动

**核心逻辑**
- `LifePatterns.cs`：`LifePatternKind` 拆为 `StillLife / Oscillator(周期2) / PeriodicOscillator(周期≥3) / Spaceship`；新增 **PULSAR（周期 3，48 细胞）** 与 **PENTADECATHLON（周期 15，12 细胞）**。
- `LifeTerminalController.cs`：棋盘 64×40 → **96×64**（原尺寸放不下 13×13 的脉冲星）；样本档案计数改为从 `LifePatterns.All.Length` 派生。
- `LifeGridElement.cs`：`TryGetCell` 增加空引用防护。

**组装**
- 新增 `ConwayGameOfLife.Runtime.asmdef` 与 EditMode / PlayMode 两个测试程序集，把"规则引擎不得依赖 `UnityEngine`"从约定变成编译期约束。

**测试（新增 29 项）**
- EditMode 23 项：规则正确性（对独立参考实现）、样本行为、边界语义、簿记。
- PlayMode 6 项：界面自举完整性、主题样式真实生效、按钮交互驱动。

**文档**
- `README.md`、`Assets/Docs/TechnicalAnalysis.md`、`Assets/Docs/Implementation.md`。

**工具**
- `.verify/`：独立 .NET 工程，**直接链接** `Assets/Scripts` 真实源码，可在不启动 Unity 的前提下做规则交叉验证、RLE 解码、样本校验与性能基准。

### 提交链

```
9e0bec6  实现 Conway 生命游戏核心逻辑、样本验证与文档
74a68f0  修复可复现性：把手写的 .verify/Verify.csproj 纳入版本控制
```

（`e93648e 初次签入` 来自 R1）

### 本轮踩到的坑（都是"测量工具错了"，不是被测对象错了）

| 现象 | 真实原因 | 修正 |
|---|---|---|
| 手写的脉冲星坐标测不出周期 | 坐标根本不是脉冲星（凭记忆写错） | 改用权威 RLE + 自写解码器生成坐标 |
| 自写校验工具对 2 个样本报 FAIL，而 Unity 测试通过 | 工具 `switch` 的 `default` 分支吞掉了新加的 `PeriodicOscillator` 枚举值 | 补齐 case，并删除过时的手写候选样本 |
| PlayMode 4 项断言失败 | 断言写错：把稳定态 `BLOCK` 断言成"应当演化"；`Q("header")` 误当按 name 查找（实为 class）；包围盒宽度算错；"原地不动"用左上角而非中心判定 | 逐条改为正确断言 |
| `LifeTerminalBootstrapTests.cs` 变成非 UTF-8 | 用 PowerShell 正则替换含中文的源码，写出时用了非 UTF-8 编码 | 字节级从 UTF-8 副本重建；并扫描 `Assets` 全部文本文件确认无其他受害者 |

### 本轮的认知转变

R1 留下的 `conway-architecture-v2.json` 中写着：

> "Unity CLI 已验证 Editor ready / 编译通过，Console 零错误"

而实际状态是：`LifeRuntimeTheme.tss` 里装的是别人的 LeetCode C++ 题解，Unity 每次导入都报 **11 条 USS 解析错误**。

**这份"文档"不是在描述项目，而是在描述一个希望中的项目。** 这是本轮最重要的发现——它决定了后续所有工作的形式：不再写"我们实现了 X"，而是写"命令 C 输出 R，R 蕴含 X"。

---

## R1 · 2026-09-10 — 初次签入

- Unity 2D 模板工程建立（Unity 6000.6.0f1）。
- 骨架代码：`LifeSimulation`（B3/S23 + 双缓冲 + 两种边界）、`LifePatterns`（6 个样本）、`LifeGridElement`（Painter2D 自绘 + 鼠标编辑）、`LifeTerminalController`（UI Toolkit 全代码构建界面，`RuntimeInitializeOnLoadMethod` 自举）。
- 产出 `conway-architecture*.html/json` 架构图。

**遗留问题（R2 才发现）**
- `LifeRuntimeTheme.tss` 内容损坏（C++ 代码），Console 持续报错。
- 引用了一个不存在的字体资源（`Font not found for path: LifeTerminalFont`）。
- 无任何文档、无任何测试。
- `Assets/Scripts/`、`Assets/Resources/` 整体未提交。

---

## 决策记录（跨轮次生效）

| 决策 | 理由 | 若被推翻会怎样 |
|---|---|---|
| 规则引擎不依赖 `UnityEngine` | 可在无编辑器环境下交叉验证与基准测量（秒级 vs 分钟级） | 失去 `.verify` 工具链，验证成本大幅上升 |
| "循环震荡"按**周期 ≥ 3** 定义 | 与"振荡（周期 2）"形成可判定的界限，且脉冲星/十五周期振荡器是文献公认的长周期振荡器 | 见张力 T1 |
| 样本坐标一律由 RLE 解码，不手工转录 | 本轮已发生一次手写坐标错误 | 样本正确性失去来源保证 |
| 测试的参考实现写在测试内、**不复用被测代码** | 复用会让同一个 bug 在两边同时出现，测试通过但毫无意义 | 测试退化为自证 |
| 项目专属 gitignore 规则置于文件末尾 | gitignore 以"最后匹配者生效"解决冲突 | 模板规则会再次吞掉手写文件（本轮已发生） |

---

## 张力清单（未决问题）

这些是尚未消解的分歧或风险，**保留而非掩盖**。

### T1 — "循环震荡"的两种读法（开放）

题目原文：*两种稳定状态、两种震荡状态、两种循环震荡状态*。

- **当前采用**：按周期长度区分——振荡 = 周期 2，循环震荡 = 周期 ≥ 3。
- **另一种读法**：循环震荡 = "形态循环且位置平移"，即滑翔机/LWSS 这类（当前归入 `Spaceship`）。
- **影响面**：若评审采第二种读法，现有分类的命名需要调整（分类本身都有实例支撑，只是标签不同）。
- **消解方式**：在文档中显式说明两种读法并给出本文档的选择与理由，而不是假装只有一种解释。已在 `README.md` 与 `TechnicalAnalysis.md` §3.2 说明。

### T2 — 渲染层是真正的性能瓶颈，尚未处理

规则内核 0.21 ms/代，但 `LifeGridElement.DrawGrid` 每代对 6,144 个格子逐个构建路径并填充。技术分析 §7 已量化并给出四条改进路径，**本轮未实施**。

- **风险**：若评审关注"效率"，可能先看渲染而看不到内核数据。
- **下一步**：改为"背景一次填充 + 只画活细胞"，稀疏样本下工作量降三个数量级。

### T3 — 环绕边界的取模代价未优化

实测环绕模式吞吐比固定边界低 41%（76.2M → 46.1M 格/秒），根因是每邻居两次 `%`。技术分析 §6.1 给出"幽灵边框"方案（预估 +65%），**本轮未实施**。

### T4 — R1 的架构图已过时

`conway-architecture*.html/json/png` 描述的是改造前的状态（写着 "06 ENTRIES"、棋盘 64×40、且 json 含乱码）。已加入 `.gitignore` 不再提交，但**文件仍在工作区**。

- **风险**：评审若打开这些文件会被误导。
- **下一步**：删除，或按当前架构重新生成。

### T5 — 手册已验证，真实玩家构建未验证

从未产出过 Player 构建（`unity build`）。PlayMode 测试覆盖了编辑器内的运行时，但独立构建的行为（尤其 UI Toolkit 主题在 Player 中的处理）未验证。

### T6 — 手写工程文件被模板忽略规则吞掉（本轮已修复）

`Verify.csproj` 是手写的，但 Unity 官方 `.gitignore` 模板的 `*.csproj` 规则把它排除了，导致独立校验工具只有源码、没有工程文件。

- **发现者**：外部评审（GPT），**不是**我自己的测试。
- **根因**：项目专属规则写在模板规则**之前**，而 gitignore 以"最后匹配者生效"解决冲突。
- **修复**：把项目专属规则移到文件末尾并加 `!/.verify/*.csproj` 例外（提交 `74a68f0`）。
- **为何自测没发现**：我的"复现"是在**原工作区**里跑命令——那里文件本来就在，绕过了忽略规则。这印证了镜链原则第 2 条：可复现性只能靠空目录 clone 来判定。
- **已建立防回归机制**：每次改动后执行下面这段"空目录复现"检查。
  ```powershell
  $fresh = Join-Path $env:TEMP "cgl-fresh-check"
  Remove-Item -Recurse -Force $fresh -ErrorAction SilentlyContinue
  git clone --no-hardlinks . $fresh
  Push-Location $fresh
  dotnet run --project .verify/Verify.csproj -c Release
  unity run .
  unity test . --mode EditMode
  unity test . --mode PlayMode
  Pop-Location
  ```

---

## 镜链原则

本轮反复出现的失败模式高度一致，可归纳为一条：

> **凡"声称"与"事实"可能分离的地方，都必须让声称变成机器可检查的。**

四种形态，同一个病根：

| 形态 | 本轮实例 | 处方 |
|---|---|---|
| 文档与人脑分离 | 架构图声称"Console 零错误"，实际 11 条 USS 错误 | 把断言写成测试，而不是写成文字 |
| 声明与数据分离 | `LifePattern.Period` 是声明值，可能与实际行为脱节 | 测试遍历 `LifePatterns.All` 断言每个声明 |
| 测试与被测对象分离 | 参考实现若复用被测代码，会同步继承同一个 bug | 参考实现独立重写 |
| 工具与贡献者分离 | `Verify.csproj` 未提交，别人复现不了 README 的命令 | 忽略规则显式化 + 全新检出验证 |

由此得到两条操作准则：

1. **验证工具本身也要被验证。** 本轮 4 次失败中，有 2 次是校验工具的 bug，2 次是断言的错误——被测的规则引擎从头到尾都是对的。当结果与预期不符时，先怀疑测量，再怀疑被测对象，最后才怀疑预期。
2. **可复现性是仓库的属性，不是代码的属性。** 因此每次改动后，判定标准是"在一个空目录里 clone 下来能不能跑通"，而不是"在我机器上能不能跑通"。
