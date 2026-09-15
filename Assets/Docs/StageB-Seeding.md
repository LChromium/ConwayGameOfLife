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

参数变化后**防抖 200 ms**，再由后台线程生成（不可变参数快照 + 两个固定缓冲交替）；
控件更新与 GPU 上传仍在主线程。生成期间可以继续调参或取消，**只采纳最新请求的结果**，
迟到结果一律丢弃。

| 动作 | 结果 |
|---|---|
| 调参数 | 只更新候选。防抖后重新生成；生成期间界面显示「生成中…」，应用保持禁用 |
| 预览 | 候选上台，**真实棋盘一格不动**；活细胞显示为克制的琥珀色 |
| 应用 | 候选成为实验初态，世代清零，保持暂停；活细胞回薄荷绿；清除样本选中高亮 |
| 取消 | 丢掉候选，恢复进入预览前的标题与样本高亮。**棋盘从未被移动过，所以没有东西需要恢复** |
| 重置 | 恢复上一次确认的初态（阶段 A 的语义，未被本阶段改变） |
| 换种子 | 独立按钮，且是显式操作 |
| 运行 / 单步 / 绘制细胞 | **预览中禁用**，命令入口自身也检查状态 |
| 平移 / 缩放 | **预览中仍可用**——只改视图 |
| 选样 / 清空 / 重置 / 随机播种 / 切换后端 | **先统一结束预览**，再执行原命令 |

`Apply` 返回的是**副本**而不是会话内部缓冲：调用方把它存成实验初态，而会话下一次
生成会覆盖自己的缓冲——不复制的话「重置」恢复的板子会被悄悄改写。

## 5. 证据

### 测试（EditMode 65/65，PlayMode 24/24）

| 验收条件 | 测试 |
|---|---|
| 相同参数生成相同初态 | `SameParameters_ProduceTheIdenticalBoard` |
| cluster strength=0 退化为均匀 | `ClusterStrengthZero_IsExactlyTheUniformMode`（与 Uniform 模式**逐格相同**） |
| warp strength=0 退化为未扭曲 fBm | `WarpStrengthZero_IsExactlyTheUnwarpedField`（与测试里独立重组的期望逐格相同） |
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

## 6. 工具区结构

右侧工具区是**「样本 / 播种」两个页签**，边界条件与演算后端是两个页面共用的控件。

这样做的原因：两者都想占满整列，叠在一起时档案只剩 3 个可见项；
堆叠布局下合并高度还会溢出机器，压到棋盘与读数上。页签让每页各得其所。

堆叠布局下工具页在棋盘与读数下方占满整行，播种页自己滚动，
工具区高度有上界，不会推挤棋盘。

## 6. 已知限制

| 限制 | 说明 |
|---|---|
| 1024² 首次生成仍要等约 0.5 秒 | 参数变化防抖 200 ms 后投递到后台线程，界面不再卡住，但结果本身仍要那么久。生成期间显示「生成中…」，应用保持禁用 |
| 噪声在 CPU 生成 | GPU 不独立生成噪声。见 §3 的理由 |
| 未做亚像素密度总览 | 属阶段 C |
| 未做过渡动画 | 规格明确要求先验证"预览与实际播种一致、可取消、可复现"，再打磨动画 |
| 堆叠布局底部有空白 | 工具区在堆叠布局下不伸展（`flex-grow: 0`）。试过让它伸展，结果工具区与棋盘区重叠，故选回不遮挡的一版 |

## 7. 一个测量方法上的失败，如实记录

先想从截图反推聚集程度，**失败了两次**：第一次采样区域画错，测出的密度只有实际的一半；
第二次把浅色 UI 面板误判成活细胞。棋盘区域后来定位正确（1021×532），但颜色分类仍不可靠。

**结论：放弃截图统计。** 量化结论改由数据层测量承担。截图只作为视觉证据。
从截图采样像素来做定量判断，在这个界面上不是可靠的测量通道。

## 8. 一次观察，不是普遍规律

在**本次参数、本次尺寸**下观察到：密度 0.31 的随机盘面在 B3/S23 下演化到第 23 代后，
密度落到约 0.16，分块起伏与均匀播种的差别也变得很小。

这只是这一组参数与这一代数的实测。**不能推广成「生命游戏总会抹平播种结构」**：
抹平的速度与程度取决于密度、团簇尺度与演化代数，本阶段没有做过扫描，
也没有做过多组参数下的对照。要说成普遍结论，需要单独测量。

它对本阶段的实际影响是：**预览必须在第 0 代看**，演化几代之后再比较就说明不了播种本身。

## 9. 复现

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
