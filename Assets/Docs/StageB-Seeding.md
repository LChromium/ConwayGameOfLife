# 阶段 B：fBM＋域扭曲概率播种

**范围**：只做播种。规则内核未改，未引入更多 GPU 优化，未加 Curl 粒子、持续补种、4D 动画或 GPU 统计回读。
均匀随机保留为对照组。`LifeSimulation` 与阶段 A 的演化/显示路径均未改动。

---

## 1. 生成关系

严格按规格，集中在 `LifeNoiseSeeding.GenerateFbm`：

```
先扭曲采样坐标：  sx = x + warpStrength * (noiseX(x/scale, y/scale) * 2 - 1)
                  sy = y + warpStrength * (noiseY(x/scale, y/scale) * 2 - 1)
再取归一化噪声：  n  = fBm(sx / scale, sy / scale, seed)          ∈ [0, 1]
形成概率：        p  = saturate(density + clusterStrength * (n - 0.5))
逐格判定：        alive = thresholdRandom(x, y, seed) < p
```

- **fBm 固定 4 层**，lacunarity 2、gain 0.5，按总幅度归一化，所以值域不随层数漂移。
- **值噪声**在整数格点上做哈希 + 平滑插值（smoothstep），只用多项式，不用超越函数。
- **哈希**是自带的整数混合（xxHash / Murmur 收尾风格），来源与位模式都钉在文件里，
  不依赖外部噪声库——依赖升级不会改变已发布的盘面。
  归一化取哈希高 24 位乘 2⁻²⁴，在 float 里是精确的。
- **概率随机数与噪声场用不同的种子派生**（`ThresholdSeedSalt` 与 `WarpSeedX/Y` 独立），
  避免两者不必要的相关。

参数只有五个：`seed / density / scale / warpStrength / clusterStrength`。不开放八度编辑。

## 2. 两条退化是结构性的，不是特判

| 条件 | 结果 | 为什么是结构性的 |
|---|---|---|
| `clusterStrength = 0` | p 恒定 = density | 比较退化为「哈希随机 < density」，这正是 Uniform 模式用同一个哈希做的事 |
| `warpStrength = 0` | 采样坐标恰为 `(x/scale, y/scale)` | 扭曲偏移被乘零，不存在"走另一条分支"的可能 |

两者都不是会与另一条路径漂移失步的分支。

## 3. 一个生成器，不是两个

规格写的是「先做纯函数/可复现生成路径，**再接入 GPU 批量初态上传**」，实现按此：
噪声只在 CPU 生成一次，交给当前后端作为一个确定数组批量上传（`LoadBoard` → 一次 `SetData`）。

**这不只是省事。** HLSL 编译器会把 `a*b+c` 合并成 FMA，同样的数学在 C# JIT 与着色器
编译器下最后几位可能不同。让 CPU 和 GPU 各自独立生成浮点噪声、还要求逐格一致，
需要额外的控制与验证（例如改用定点整数运算，或对每次运算做可复现性验证）。
本轮不做这件事：只生成一次，之后要验证的就是**上传路径本身**，
而不是两份可能漂移的实现。

## 4. 交互语义

`LifeSeedingSession` 持有参数、候选和上次确认的参数；`LifeGridElement` 的
`ShowPreview` 把候选写进渲染器自己的上传缓冲，**完全不经过后端**。

### 4.1 三个状态，不是两个

后台生成与用户操作之间的约束靠三个互相独立的问题撑开，混在一起就是上一轮的缺陷来源：

| 问题 | 属性 | 谁可以用它 |
|---|---|---|
| 用户是否在等一个描述当前控件的候选 | `IsGenerating` | **只有它**可以锁运行/单步/绘制，也只有它显示「生成中…」 |
| 后台是否还有任务在收尾 | `IsWorking` | **谁都不能锁**。取消之后它还会真一段时间——被放弃的任务仍要跑完 |
| 是否已发出、尚未采纳的请求 | `HasPendingRequest` | 决定泵送时要不要补发 |

上一版把「有任务在跑」直接当成 `IsGenerating`，于是取消之后的下一次泵送又把界面锁回去。
现在 `IsGenerating` 是**计算属性**（`wantCandidate && 没有与当前参数一致的候选`），
没有可以变陈旧的字段，取消当场为假，之后的每一次泵送也都保持为假。

### 4.2 参数决定能不能采纳，不由完成顺序决定

采纳一个结果的条件是**它由当前参数生成**（`finishedParameters.Equals(Parameters)`），
不是「它的请求号最新」。`SetParameters` 因此**当场**让在途请求失效：
已经跑起来的任务可以跑完，但它产出的是控件已经离开的那组参数，到达时被丢弃，
而不是下一帧被上传显示。

防抖 200 ms 只决定**新的计算什么时候被请求**，不决定旧结果还算不算数。
请求发出时若旧任务仍在跑，请求会挂起（`HasPendingRequest`），
旧任务结束后立即以最新参数启动——一次长拖拽因此合并成「最多多算一次」，
而不是每个事件算一次，也不是一次都不算。

**失败与成功遵守同一条规则。** 一个已经被取代的任务抛异常时，它的失败和它的结果一样被丢弃：
既不写 `FailureMessage`（当前请求并没有失败），**也不清掉取代它的那个请求**。
这里曾经有一个真实缺陷——异常分支无条件复位 `wantCandidate`/`requestPending`，
于是「旧任务崩溃」把用户刚发出的新请求一起取消，界面停在预览打开、没有候选、也没有任何计算的状态。
现在异常分支比对失败任务的参数快照：只有当它**就是**当前请求时才停止等待并上报失败，
否则只释放 worker，让挂起的新请求立刻启动。

跨会话生命周期：`OnDestroy` 调用 `Seeding.Dispose()`，**不在主线程等待任务结束**——
退出路径阻塞等一个没人要的结果会把干净退出变成卡顿。任务自己跑完，结果被 `disposed` 拒绝。

| 动作 | 结果 |
|---|---|
| **未进入预览时调参数** | **只改参数**。不请求生成、不产生候选、不锁任何控件；棋盘照常运行 |
| 进入预览 | 暂停时钟并开始生成；预览期间调参数才防抖重算 |
| 调参数（预览中） | 立即让旧结果失效，防抖后重算。防抖窗口内显示「预览待更新」，计算中显示「生成中…」，应用保持禁用 |
| 预览 | 候选上台，**真实棋盘一格不动**；活细胞显示为克制的琥珀色 |
| 应用 | 候选成为实验初态，世代清零，保持暂停；活细胞回薄荷绿；清除样本选中高亮。若预览尚未与控件一致，意图被记住，**只在一个被采纳的候选上花掉**，被丢弃的结果消耗不了它 |
| 取消 | 丢掉候选，恢复进入预览前的标题与样本高亮，**界面当场恢复可用** |
| 重置 | 恢复上一次确认的初态（阶段 A 的语义，未被本阶段改变） |
| 换种子 | 种子输入框右侧的按钮：由当前种子派生下一个种子（不是重新随机整块盘面，盘面要再点「预览」） |
| 运行 / 单步 / 绘制细胞 | **预览中禁用**，命令入口自身也检查状态 |
| 平移 / 缩放 | **预览中仍可用**——只改视图 |
| 选样 / 清空 / 重置 / 随机播种 / 切换后端 | **先统一结束预览**，再执行原命令 |
| 当前请求的生成抛异常 | 会话停止等待（`wantCandidate` 复位）并记录 `FailureMessage`，界面显示「生成失败」。**不留下一个永远转圈的「生成中」** |
| **已被取代的任务抛异常** | 与它的结果同样丢弃。**不清除取代它的请求**，不写失败信息，worker 一空出就启动挂起的请求 |

`Apply` 返回的是**副本**而不是会话内部缓冲：调用方把它存成实验初态，而会话下一次
生成会覆盖自己的缓冲——不复制的话「重置」恢复的板子会被悄悄改写。

## 5. 证据

### 测试（EditMode 75/75，PlayMode 39/39）

异步时序靠**注入的可控生成器**验证：`LifeSeedingSession` 的构造函数接受一个
`LifeBoardGenerator`，测试用按种子开关的闸门把「旧任务还没跑完」变成可观察状态，
而不是和机器的速度赛跑。

参数、淘汰与生命周期：

| 验收条件 | 测试 |
|---|---|
| 未进入预览时调参不生成任何东西 | 会话：`SetParameters_AloneDoesNotGenerate`（注入计数生成器，断言调用次数为 0）<br>界面：`EditingSeedParameters_WithoutPreview_DoesNotGenerateOrLockTheBoard`（从**运行中**的棋盘出发，驱动真实滑块，等过防抖窗口，再按单步验证控件真的可用） |
| A 未完成 → 改 B → A 完成不得采纳 → B 完成才显示 | `ChangingParametersWhileAGenerationRuns_DropsItAndAdoptsTheNewerOne`（并检查此时 `Apply` 拒绝、B 落地后 `Apply` 落在 B 上） |
| 参数一变，在途请求立即失效 | `SetParameters_InvalidatesAnOutstandingRequestImmediately` |
| 取消后界面立即恢复，且后续每次泵送都不再锁住 | `Cancel_ReleasesTheInterfaceWhileTheAbandonedTaskIsStillRunning`（**在旧任务未完成时**检查 `IsGenerating`/`IsWorking`/`HasPendingRequest`，并连泵 20 次） |
| 取消后重新请求不被旧任务顶替 | `Cancel_ThenRequestingAgain_IsNotServedByTheAbandonedTask` |
| 销毁后迟到结果不被采纳 | 会话：`Dispose_WhileAGenerationRuns_RefusesTheLateResult`（任务在跑时销毁）<br>接线：`DestroyingTheController_DisposesTheSeedingSession`（真的销毁一个控制器实例，查会话被释放） |
| 自动应用意图不被旧结果消耗 | `ApplyingAnOutOfDatePreview_AppliesTheNewestParameters`（同一帧内改参数，等到的初态必须来自最新参数） |
| 生成失败不锁死界面 | `GeneratorFailure_ReleasesTheInterfaceInsteadOfWaitingForever` |
| **过期任务失败不得清除新请求** | `ASupersededTaskFailing_MustNotClearTheReplacementRequest`（A 仍在跑 → 改 B 并请求 → 放行 A 使其抛异常 → B 的请求必须还在、必须启动、必须落地；断言 B 落地前不报失败） |
| —— 且该测试能被证伪 | 把异常分支改回无条件复位后重跑同一条测试：`1 failed`，失败信息停在 `timed out after 30s waiting for the replacement generation to start`——即「新请求被旧任务的失败取消了」这一现象本身。改回修复版即恢复通过（命令见 §11）。`tr-*.xml` 是本地临时输出，按 `.gitignore` 不入库，因此这里记录的是**命令与观测**，不是文件 |
| 当前请求失败仍然上报并释放 | `TheCurrentTaskFailing_StillReportsAndReleases`（防止上一条被修成「什么都不清」，那会把面板焊死） |

播种本身：

| 验收条件 | 测试 |
|---|---|
| 相同参数生成相同初态 | `SameParameters_ProduceTheIdenticalBoard` |
| cluster strength=0 退化为均匀 | `ClusterStrengthZero_IsExactlyTheUniformMode`（与 Uniform 模式**逐格相同**） |
| warp strength=0 退化为未扭曲 fBm | `WarpStrengthZero_IsExactlyTheUnwarpedField`（与测试里独立重组期望逐格相同） |
| —— 且该测试非空转 | `WarpStrengthAboveZero_ActuallyChangesTheBoard` |
| CPU/GPU 初态逐格一致 | `AppliedSeeding_IsCellIdenticalOnBothBackends`（GPU 应用后切 CPU 比对） |
| 应用后演化不再读取噪声 | `EvolutionAfterSeeding_DoesNotConsultTheNoise`（空盘面必须保持空） |
| 应用前不得修改真实棋盘 | `PreviewSeeding_LeavesTheLiveBoardUntouched` |
| 取消可恢复 | `CancelSeeding_LeavesTheBoardExactlyAsItWas` |
| 界面控件真的接上了 | `SeedingControls_DrivePreviewApplyAndCancel`（驱动真实控件，不是调内部方法） |

### 实测

| 项 | 值 |
|---|---|
| 1024² 生成耗时 fBm | 528–563 ms |
| 1024² 生成耗时 均匀 | 10.8–14.3 ms |
| 实际密度（请求 0.32） | fBm 0.3101，均匀 0.3198 |
| 1024² 分块(32)密度标准差 | 均匀 0.0146，fBm 0.0811（**5.56 倍**） |

分块标准差由 `At1024_ClusteringIsMeasurableInTheGeneratedBoard` 在**数据层**测量。
「基础密度不是人口承诺」由此有了实测数字：请求 0.32，fBm 实际 0.3101。

### 画面

| 文件 | 内容 |
|---|---|
| `Screenshots/player-seed-fbm-1024.png` | 第 0 代，1024²，fBm，大尺度域扭曲结构 |
| `Screenshots/player-seed-uniform-1024.png` | 同上参数的均匀随机对照 |
| `Screenshots/player-seed-fbm-1024-evolving.png` | 同一初态演化到第 23 代 |
| `Screenshots/player-seed-preview-1024.png` | 琥珀色预览 |
| `Screenshots/player-seed-ui-1600x900.png` / `-600x1000.png` | 工具区页签与播种面板，宽布局与堆叠布局 |

界面验收以真实截图为准，不以测试通过代替：四张界面截图逐张确认
**棋盘、世代/人口/状态读数、工具区、底部操作互不遮挡**。
堆叠布局尤其要看这一项——宽布局通过不代表竖屏通过。

**文字也逐项确认，而且可以量。** 上一版只检查了控件的外框，于是漏掉了三类被裁切的文字。
现在 `SeedingPanel_UsesThePageWidth_AndShowsItsWholeText` 用 `MeasureTextSize` 把每个值的
渲染文字与画它的盒子逐项比对，宽布局与堆叠布局各跑一遍（测试里临时改参考分辨率来得到
竖屏的面板宽度，不动窗口）。它当场量出了两件事：

| 量到的 | 数值 | 后果 |
|---|---|---|
| 下拉框自己的文本元素 | 盒高 **10.7**，字号 14 | 三个下拉框的选中值都被切掉一半 |
| 种子输入的可编辑文本元素 | 盒高 **8.6**，字号 11 | 「20260915」只剩一条模糊的横带 |

原因不同，修法也不同：前者是主题给 popup 文本固定了一个 10.7 的行盒（给 `min-height` 即可），
后者是主题给可编辑文本的上下内边距吃掉了行高（滑块自己的数值框一直正常，因为它把内边距清零了）。
两类都记在 `LifeTerminal.uss` 里，附上量到的数字。

**图标也是同一类问题。** 种子按钮原来写的是 `⟳`（U+27F3），运行时字体**没有这个字形**，
Player 里画成一个空心方框。字体覆盖范围无法从源码判断，只能在实机截图里看，所以换成了
一个这类构建**已经证明能画**的字符类：`换`（界面上每一处中文标签都正常），完整措辞放进 tooltip。
顺手把其余图标也在实机上放大核对过一遍：`▶ 运行` / `▸ 单步` / `↺ 重置` / `Ⅱ 暂停` / `●`
都正常，只有 `⟳` 缺字形。

## 6. 工具区结构

右侧工具区是**「样本 / 播种」两个页签**，边界条件与演算后端是两个页面共用的控件。

这样做的原因：两者都想占满整列，叠在一起时档案只剩 3 个可见项；
堆叠布局下合并高度还会溢出机器，压到棋盘与读数上。页签让每页各得其所。

堆叠布局下工具页在棋盘与读数下方占满整行，播种页自己滚动，
工具区高度有上界，不会推挤棋盘。

### 6.1 播种页为什么曾经挤在左窄列

播种面板的控件加进的是 `ScrollView` 的**内容容器**，而堆叠布局的规则写在 `ScrollView` 上。
容器因此完全没被管到，宽度收缩到最宽子元素（约 220 单位），右侧整片空着。
控件本来就该由容器负责排版，所以现在给容器一个 `seed-page` 类，
堆叠布局下它是**占满宽度的换行行**：模式与种子同一行，四个滑块每行两个（百分比宽度，
任何面板宽度下都成立），读数与按钮各占一整行。

顺带量出来的两处宽度：

| 控件 | 之前 | 现在 | 原因 |
|---|---|---|---|
| 后端下拉框（宽布局） | 列宽 236，文本框 154 | 列宽 **280**，文本框 198 | 「GPU（Compute Shader）」需要 165 单位，popup 自己占掉 82 |
| 后端/边界下拉框（竖屏） | 固定 180 | 固定 **260** | 同上，180 只能显示到「GPU（Compute Shade」 |

## 7. 已知限制

| 限制 | 说明 |
|---|---|
| 1024² 首次生成仍要等约 0.5 秒 | 参数变化防抖 200 ms 后投递到后台线程，界面不再卡住，但结果本身仍要那么久。生成期间显示「生成中…」，应用保持禁用 |
| 噪声在 CPU 生成 | GPU 不独立生成噪声。见 §3 的理由 |
| 未做亚像素密度总览 | 属阶段 C |
| 未做过渡动画 | 规格明确要求先验证"预览与实际播种一致、可取消、可复现"，再打磨动画 |
| 堆叠布局底部有空白 | 工具区在堆叠布局下不伸展（`flex-grow: 0`）。试过让它伸展，结果工具区与棋盘区重叠，故选回不遮挡的一版 |

## 8. 一个测量方法上的失败，如实记录

先想从截图反推聚集程度，**失败了两次**：第一次采样区域画错，测出的密度只有实际的一半；
第二次把浅色 UI 面板误判成活细胞。棋盘区域后来定位正确（1021×532），但颜色分类仍不可靠。

**结论：放弃截图统计。** 量化结论改由数据层测量承担。截图只作为视觉证据。
从截图采样像素来做定量判断，在这个界面上不是可靠的测量通道。

## 9. 一次观察，不是普遍规律

在**本次参数、本次尺寸**下观察到：密度 0.31 的随机盘面在 B3/S23 下演化到第 23 代后，
密度落到约 0.16，分块起伏与均匀播种的差别也变得很小。

这只是这一组参数与这一代数的实测。**不能推广成「生命游戏总会抹平播种结构」**：
抹平的速度与程度取决于密度、团簇尺度与演化代数，本阶段没有做过扫描，
也没有做过多组参数下的对照。要说成普遍结论，需要单独测量。

它对本阶段的实际影响是：**预览必须在第 0 代看**，演化几代之后再比较就说明不了播种本身。

## 10. 复现

```powershell
# 应用一次 fBm 播种（1024²）
Builds\LifeTerminal.exe -screen-width 1600 -screen-height 900 -screen-fullscreen 0 `
    -lifeBoard 1024x1024 -lifeZoom 1 -lifeSeedApply `
    -lifeSeed 20260915 -lifeDensity 0.32 -lifeScale 60 -lifeWarp 10 -lifeCluster 0.7

# 只看候选（琥珀色，不应用）
Builds\LifeTerminal.exe ... -lifeSeedPreview

# 均匀随机对照
Builds\LifeTerminal.exe ... -lifeSeedApply -lifeSeedUniform -lifeDensity 0.32
```

参数：`-lifeSeedApply` / `-lifeSeedPreview` / `-lifeSeedUniform` / `-lifeSeed <int>` /
`-lifeDensity <float>` / `-lifeScale <float>` / `-lifeWarp <float>` / `-lifeCluster <float>`。

两张界面截图由 `Tools/capture-player-window.ps1` 拍摄（它是本轮的产物：上一轮的截图取自
窗口矩形，右边缘与底边缘各带进来一条桌面像素）。脚本按**客户区**抓图并做 DPI 感知：

```powershell
$seed = '-lifeBoard 256x256 -lifeZoom 1 -lifeSeedPreview -lifeSeed 20260915 ' +
        '-lifeDensity 0.32 -lifeScale 40 -lifeWarp 8 -lifeCluster 0.7'
.\Tools\capture-player-window.ps1 -Width 1600 -Height 900 -Arguments $seed `
    -Output Screenshots\player-seed-ui-1600x900.png
.\Tools\capture-player-window.ps1 -Width 600 -Height 1000 -Arguments $seed `
    -Output Screenshots\player-seed-ui-600x1000.png
```

---

## 11. 归档（阶段 B）

| 项 | 内容 |
|---|---|
| **版本位置** | Git 标签 `stage-b-life-seeding`（提交号用 `git describe --tags` 取，不写死在文档里）|
| **范围** | fBM + 域扭曲概率播种：生成器、预览/应用/取消/重置语义、后台生成与界面约束、工具区页签 |
| **测试报告** | EditMode **75/75**、PlayMode **39/39**（`test-results-*.xml` 随版本保存）|
| **证伪记录** | 见 §4.2 与 §5：把异常分支改回无条件复位，`unity test . --mode EditMode --filter ASupersededTaskFailing_MustNotClearTheReplacementRequest` 从通过变为 1 failed，停在 `timed out after 30s waiting for the replacement generation to start`；`git stash` 回来即恢复通过。阶段一的 `tr-falsify.xml`、阶段 A 的 `gpu-cpu-equivalence.json` 同理可复现 |
| **画面验收** | `Screenshots/player-seed-ui-1600x900.png`、`-600x1000.png`（工具区两页签、播种面板、逐项文字核对）|
| **原始测量** | `Screenshots/player-seed-*.png` 四张盘面、`stage-a-perf.jsonl`（阶段 A 帧成本）|
| **保留问题** | 1024² 首帧仍约 0.5 s；噪声只在 CPU 生成；堆叠布局底部有空白；未做过渡动画 |

**未纳入本阶段**（明确留给阶段 C）：大棋盘基准、亚像素密度总览、GPU 独立生成噪声。
本阶段范围到此关闭，不再扩展。

> **阶段归档 ≠ 发布版性能验收。** 截图与测量都来自**开发版**构建；
> 性能口径见 [`StageA-Gpu.md`](StageA-Gpu.md) §5：GPU 提交耗时只算命令提交，
> 提交+同步包含整盘回读，是上界，**不据此声称任何 CPU/GPU 加速比**。
