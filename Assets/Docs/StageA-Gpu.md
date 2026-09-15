# 阶段 A：GPU 演化与显示

本文件记录 GPU 实验阶段 A 的实现、证据与已知限制。

**范围**：只做 GPU 演化与显示。**未接入噪声**（阶段 B），未动 Burst、位打包、幽灵边框。
`LifeSimulation` 保持为 CPU 正确性参考，本阶段一行未改。

---

## 1. 结构

```
LifeTerminalController        命令、运行状态、后端选择、重置语义
        │
        ├── ILifeBackend ──┬── CpuLifeBackend   包装未改动的 LifeSimulation
        │                  └── GpuLifeBackend   两个 uint 缓冲 + Compute Shader
        │
        └── LifeGridElement   显示层
                 └── LifeBoardRenderer   两个后端共用的显示路径
                          └── LifeGpu.compute  Render 内核 → RenderTexture → UI Toolkit
```

后端接口刻意保持薄：尺寸、世代、边界、载入确定盘面、清空、单格写入、推进一步、
人口查询、全盘回读。**它不提供"给我一个随机盘面"**——两个后端必须拿到同一份数组，
这样 CPU/GPU 不一致就不可能用"种子不同"来解释。

## 2. 规则实现

`LifeGpu.compute` 的 `Step` 内核，一线程一格：

- 两个 `uint` 状态缓冲，元素只取 0/1，索引 `y * width + x`。
- 从旧缓冲读自身与八邻居，写入新缓冲，随后交换。**同一代内不会原地覆盖输入。**
- 线程组 8×8，派发数量向上取整，越出棋盘的线程直接返回。
- 固定边界：外部格子算死亡。环绕：`(v + n) % n`，与 `LifeSimulation.CountNeighbours` 同式。
- **八个邻居偏移各自独立计数，不做归并。** 1×1、1×N 环绕时多个偏移会映射到同一格，
  CPU 参考实现逐个数，GPU 必须一致。这一条是被主动证伪验证过的，见 §4。

显示由 `Render` 内核完成：状态缓冲直接画进与视口等大的纹理，**GPU 路径零 CPU 回读**。
CPU 对照模式把状态上传到同一形状的缓冲后走同一个内核，上传耗时单独记录在
`LifeGridElement.LastUploadMilliseconds`。

## 3. 正确性证据

`gpu-cpu-equivalence.json`，由 `GpuCpuEquivalenceTests` 生成。

同一份确定盘面同时交给两个后端，在预定世代比较**完整状态**。差异会报出首个不同格的
坐标、世代、边界模式与双方取值。

| 尺寸 | 用例数 | 结果 |
|---|---|---|
| 1x1 / 1x7 / 2x9 / 7x9 / 17x19 / 96x64 | 各 20 | 全部匹配 |
| 256x256 | 4 | 全部匹配 |
| 1024x1024 | 4 | 全部匹配 |
| **合计** | **128** | **128/128 匹配，两种边界各 64 例** |

盘面来源：8 个内置样本 + 固定种子随机盘面（seed 12345 / 777 / 4242 / 99）。

**防空转**：128 例中有 **89 个盘面确实发生了变化**。稳定样本 `BLOCK` / `BEEHIVE`
的 `changed=false`，`BLINKER` 周期为 2、第 8 代回到原状所以也是 `false`——这些都与
样本定义相符，说明该标志测的是真实状态变化。

## 4. 工具自身的证伪

正确性工具必须能变红，否则"全绿"没有意义。

把 `SampleNeighbour` 改成在宽度 ≤2 时丢掉重复偏移（正是规格禁止的那种"优化"），
重跑对照：

```
19 of 128 CPU/GPU comparisons disagreed.
pattern:BLOCK    1x7 wrap: first difference at generation 1, cell (0,1) = y*W+x 1, CPU=1 GPU=0
pattern:BLINKER  1x7 wrap: first difference at generation 1, cell (0,2) = y*W+x 2, CPU=1 GPU=0
pattern:PULSAR   2x9 wrap: first difference at generation 1, cell (1,0) = y*W+x 1, CPU=0 GPU=1
```

全部落在 `1x7 wrap` 与 `2x9 wrap`——正是注入缺陷的目标区间。`1x1 wrap` 未被捕获，
原因是 1×1 活细胞的 CPU 邻居数为 8、GPU 为 5，**两者都判死**，结果相同。
随后该缺陷已撤销。

## 5. 性能实测

由 `LifePerfProbe`（`-lifePerf` 启动参数）在 **Player 构建**中采集，每行一个棋盘尺寸，
原始记录见 `stage-a-perf.jsonl`。环境：Unity 6000.6.0f1，NVIDIA GeForce RTX 4070 Ti，
Direct3D12，StandaloneWindows64 发布构建（`isDevelopmentBuild=false`），
`vSyncCount=1`，`targetFrameRate=120`，采样期间窗口保持前台。

| 棋盘 | 格数 | CPU 规则 | GPU 仅提交 | GPU 提交+强制同步 | 显示 Refresh |
|---|---|---|---|---|---|
| 96x64 | 6 144 | **0.176 ms/代** | 0.0008 ms/代 | 0.017 ms/代 | 0.0035 ms/次 |
| 256x256 | 65 536 | **2.457 ms/代** | 0.0016 ms/代 | 0.026 ms/代 | 0.0034 ms/次 |
| 1024x1024 | 1 048 576 | **38.66 ms/代** | 0.0005 ms/代 | 0.415 ms/代 | 0.0024 ms/次 |

中位数；CPU 一列为 7 次重复 × 20 步，各档最小值落在中位数的 0.87–0.95 倍、
最大值落在 1.04–1.52 倍。

**每个数字该怎么读：**

- **CPU 规则**：`Stopwatch` 包住 `CpuLifeBackend.Step()`，同步托管计算，这个数就是 CPU 规则成本。
  三档近似线性，约 **27 M 格/秒**。
- **GPU 仅提交**：包住 N 次 `Step()` 且不同步。**这只是把工作交给 GPU 的开销，不是 GPU 执行耗时。**
- **GPU 提交+强制同步**：N 步之后跟一次 `ComputeBuffer.GetData`，会阻塞到 GPU 完成。
  这是包含提交、执行与同步开销的**上界**，且其中的全盘回读被摊到 20 步里。
  它同样不是干净的 GPU 执行时间。
- **显示 Refresh**：仅提交侧（渲染派发 + 背景赋值）。

**未测**：GPU 执行时间。枚举 profiler 标记需要 `Unity.Profiling.LowLevel.Unsafe`，
且发布版 Player 中不保证存在 GPU 计时标记。按规格要求，此处**保留未测，不用估算补齐**。

### 5.1 本阶段不作性能归因

**不得从本节的数字推导 GPU 相对 CPU 的加速比。** 理由：

- GPU 执行时间未测。表里的两个 GPU 列，一个只有提交开销，另一个是含同步与整盘回读的上界，
  两者都不是可以拿去和 CPU 列相除的量。
- 因此本阶段只能说：**GPU 后端在大棋盘上具备可运行性**（1024² 已跑通并有画面证据）。
  不宣称"快多少"。

**结论的适用范围**：以上全部数字来自 NVIDIA GeForce RTX 4070 Ti、Direct3D12、
Windows StandaloneWindows64 发布构建、vsync 开启、`targetFrameRate=120` 这一台机器与这一套配置。
**不可泛化到其他硬件或图形 API**；换环境必须重测。

CPU 参考后端继续保留：它既是正确性基线，也是低规模下的回退路径。

### 帧间隔

| 棋盘 | vsync 开 运行 / 暂停 | vsync 关 运行 / 暂停 |
|---|---|---|
| 96x64 | 6.2500 / 6.2500 ms | 0.390 / 0.318 ms |
| 256x256 | 6.2437 / 6.2500 ms | 0.463 / 0.348 ms |
| 1024x1024 | 6.2500 / 6.2500 ms | 0.385 / 0.304 ms |

**两条必须一起看的注意事项：**

1. **vsync 开启时帧间隔被钉在显示器刷新率上（6.25 ms ≈ 160 Hz），运行与暂停完全相同。**
   这组数字描述的是节拍，不是工作量，不能用来推导瓶颈。
2. **关闭 vsync 后采样窗口只有约 0.1 秒**（240 帧 × 0.4 ms），期间只推进了 1–2 代。
   所以这组数字描述的是"显示开着、时钟近乎空转"的帧成本，**不包含持续演化的开销**。

端到端帧间隔**包含**：显示渲染派发、UI Toolkit 面板合成、时钟逻辑（近乎空转）。
**不包含**：初始状态生成、状态上传、校验回读。

## 6. 已知限制

| 限制 | 说明 |
|---|---|
| GPU 统计未实现 | 人口读数显示 `—`。做全盘回读取代是不可接受的；补齐需 GPU 分组归约 + 异步小结果回读，并携带世代与重置编号 |
| 无 compute 时的回退会逐格绘制 | `LifeBoardRenderer` 建不起来时，`LifeGridElement` 退回 Painter2D 逐格画，并做一次全盘读。此时**根本不存在 GPU 棋盘**，读的是 CPU 后端自己的数组，与"把 GPU 棋盘读回 CPU"不是一回事；但这条路径确实是逐帧全盘读，如实记录 |
| 亚像素密度总览未做 | 最小缩放到一格一个屏幕像素。1024² 棋盘在 1216×533 视口下装不下，只能平移查看，符合本阶段规格 |
| GPU 单格查询走全盘回读 | 点击涂色时 `IsAlive` 会做一次全盘读。只发生在点击、不在逐帧路径，但不干净 |
| 无运行中热切换 | 切换后端会暂停并从保存的同一初态重新开始 |
| GPU 执行时间未测 | 见 §5 |
| 帧间隔在 vsync 下是节拍值 | 见 §5 |

## 7. 复现

```powershell
# 正确性对照（PlayMode，会写出 gpu-cpu-equivalence.json）
unity test . --mode PlayMode --filter GpuMatchesCpu_AcrossPatternsSizesAndBoundaries --output tr-equivalence.xml --timeout 900

# 性能采集：每个尺寸跑一次，结果追加到 <构建目录>/stage-a-perf.jsonl
Builds\LifeTerminal.exe -screen-width 1600 -screen-height 900 -screen-fullscreen 0 -lifeBoard 1024x1024 -lifePerf

# 画面证据
Builds\LifeTerminal.exe -screen-width 1600 -screen-height 900 -screen-fullscreen 0 `
    -lifeBoard 1024x1024 -lifePattern PULSAR -lifeZoom 8 -lifeRun
```

启动参数：`-lifeBoard WxH`、`-lifePattern <EnglishName>`（精确匹配，未知名称会告警并列出可用名）、
`-lifeZoom <像素/格>`（0 = 能装下就装下）、`-lifeRun`（启动即运行）、`-lifePerf`（测量后退出）。

---

## 8. 阶段结论

> GPU 演化与显示已经完成正确性闭环，1024² 已有真实运行证据；GPU 纯执行时间暂未测量，
> 因此不作绝对性能归因。CPU 参考后端继续保留，作为正确性基线和低规模回退。
> 阶段 B 可以开始，范围限定为 fBM＋域扭曲概率播种，不改规则内核，不同时引入更多 GPU 优化。

后续阶段（2048² / 4096²）属于压力测试，届时再实测；本阶段不预先承诺帧率，也不承诺 8192²。
