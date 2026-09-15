# 阶段 D：CPU 演化移到后台

**目标（评审原话）**：**计算期间界面仍能响应；不承诺算法吞吐提升。**

**范围**：只动 CPU 演化路径。GPU 后端保持同步；**不**追加 Burst、GPU 独立噪声或密度总览；
CPU 规则参考实现 [`LifeSimulation`](../Scripts/LifeSimulation.cs) **一行未改**。
本轮只新增两个文件（`ILifeAsyncBackend`、`LifeAsyncCpuBackend`）与一个 PlayMode fixture。

> **本阶段不是算法优化。** 同一份 `LifeSimulation`、同样的格数、同样的规则、同样的每代成本
> （§4.4 逐尺寸对照，同一进程内与参考实现相差 ≤4%）。变的只有**哪条线程被阻塞**。

---

## 1. 评审六条要求 → 实现 → 证据

| # | 要求 | 实现 | 证据 |
|---|---|---|---|
| 1 | 后台任务独占模拟状态；主线程不读正在写入的棋盘 | worker 独占 `LifeSimulation`；主线程只读自己的 `display` 缓冲，worker **从不接触**它；结果只能通过「接管」进入显示 | §3 测试 1、2；§2.1 |
| 2 | 同时最多一个演化任务，不逐帧堆积；未接收的结果不被覆盖 | 在途或待接管时**拒绝**提交并计数；流水线严格串行，没有队列 | §3 测试 3、4、8 |
| 3 | 结果携带会话版本、世代、人口；重置/编辑/载入样本/切后端/销毁后旧结果不得覆盖新棋盘 | `LifeStepOutcome{Version, Generation, Population}`；接管要求**版本相等**且**世代紧接显示世代** | §3 测试 5、6、7、9、10；证伪一 |
| 4 | 暂停语义：暂停后显示世代立即冻结，在途计算完成后暂存，恢复或单步时按顺序接收；不得悄悄跳过一代 | 控制器暂停时**停止接管**：结果留在等待槽（不显示、不丢弃）；恢复或单步先接管它 | §3 测试 11、12；证伪二；§4.3 实机 |
| 5 | GPU 资源上传与 UI 更新留在主线程；分别记录后台计算、结果复制/上传、暂停响应 | 显示缓冲、`TryReadAllCells`、`renderer.Upload` 全在主线程；记录拆成 `backgroundComputeMs`、`backgroundResultCopyMs`、`backgroundHandoverCopyMs`、`gridReportedUploadMs` 与 `pauseResponse` | §4.2、§4.3 |
| 6 | 用可控任务验证操作交叉顺序；再做 2048²/4096² 短时实机检查；保留 CPU 规则参考实现 | 调度器是构造参数，测试**持有任务**并决定何时完成；`stage-c-bench-r5.jsonl` | §3、§4 |

---

## 2. 机制

### 2.1 四块缓冲，各自只有一个主人

| 缓冲 | 主人 | 谁能读 | 谁能写 |
|---|---|---|---|
| `display` | 主线程 | 主线程（世代、人口、整盘读取、上传） | 主线程（**只在接管与命令时**） |
| `spare` | 轮换 | worker（作为输出目标） | 主线程在提交时写入一次（重建用），随后交给 worker |
| `LifeSimulation.current` / `next` | worker | worker | worker |

- 主线程的**每一次**读取（`Generation`、`TryGetPopulation`、`TryReadAllCells`）都只碰 `display`，
  而 `display` 只在**接管**时被换掉、或在命令（载入/清空/编辑）时被写。
- worker 只读它自己的两块缓冲：需要重建时，主线程把 `display` **复制到 `spare`** 再交出去
  （复制发生在主线程，正是为了 worker 永远不读主线程还能写的数组）。
- **接管 = 交换**：`display ↔ 完成的那块`，主线程零拷贝；它原先读的那块成为 worker 的下一个目标。

内存代价：**4 B/格**（`display` + `spare` + 模拟的两个缓冲）。原先同步 CPU 后端是 2 B/格
（只有模拟的两个），因此本阶段**每格多 2 字节**，4096² 上约 **+33.5 MB**。

### 2.2 一次提交到一次接管

```
主线程                                    worker
──────                                    ──────
clock 到点、且流水线空闲
  （在途或待接管 → 拒绝并计数）
若棋盘变过：display → spare（主线程复制，计时）
Step() 提交 ─────────────────────────►  重建模拟（仅首次/编辑后，计时）
                                          sim.Step()（计时 = 后台计算耗时）
                                          读盘面 → 结果缓冲（计时 = 结果复制耗时）
                                          publish{版本, 世代, 人口, 三笔耗时}
PumpEvolution 每帧：若允许 → 接管（版本+世代校验）
  → 交换缓冲、更新读数、MarkBoardDirty
  → 主线程整盘上传（计时 = gridReportedUploadMs）
```

### 2.3 会话版本与世代

- 任何**移动棋盘**的命令都让 `sessionVersion++`：`LoadBoard`、`Clear`、`SetCell`、边界改变。
- 结果带回**它算的那块棋盘**的版本；接管要求 `Version == sessionVersion` **且**
  `Generation == 显示世代 + 1`。
- 两者都不满足时：**拒绝并计数**（`RefusedGenerations`），新棋盘原样保留，且该结果的缓冲被回收，
  流水线不会被一个陈旧结果卡住。
- 编辑之后 worker 的模拟会在下一次提交时**从 `display` 重建**（`generationBase` 记住显示世代，
  因为 `LifeSimulation` 自己的计数器从 0 开始且本阶段不改它）。所以编辑**不会**让世代重新编号。

### 2.4 暂停语义（控制器决定，后端不参与）

后端对暂停**没有意见**：它只是把完成的结果留在 `HasCompletedGeneration`。控制器在
`PumpEvolution` 里决定是否接管：

| 状态 | 接管？ | 含义 |
|---|---|---|
| 运行中 | 是 | 正常前进 |
| 暂停且有在途/已完成的一代 | **否** | 显示冻结在它当时显示的那一代；结果留在等待槽 |
| 恢复运行 | 是，先接管 | 把暂停期间算完的那一代按顺序显示出来，再继续 |
| 按「▸ 单步」 | 是，先接管 | 用户要的是「屏幕上再多一代」，不是「再算一代」；已有结果就先用它 |
| 重置/编辑/载入样本/切后端/销毁 | 结果被**版本**拒绝 | 一次性命令换掉了棋盘：那是显式作废，且在记录里计数 |

**「不得悄悄跳过一代」**：正常暂停/恢复路径上没有任何一代被丢弃（§4.3 实机五轮
`completionWaitingWhilePaused=true`、`advancedByOneOnResume=true`）。
唯一被作废的情况是**显式命令换掉了棋盘**，而那一条是计数 + 记入记录的。

### 2.5 主线程仍然支付的部分（本阶段没有消除）

- **整盘上传**：每次接管后 `LifeGridElement` 要把整盘拷进上传缓冲并 `SetData`——
  4096² 上运行期读数中位数 **56.3 ms**（§4.2）。这一笔在同步时代就存在，现在**依然在主线程**。
- **提交前的重建复制**（仅在棋盘被命令改过之后）：4096² 约 2–4 ms 量级。
- 时钟的过载保护、速率窗口、状态读数全部沿用 r3/r4 的语义（见
  [StageC-Benchmark.md](StageC-Benchmark.md) §6.4），没有改。

---

## 3. 可控任务验证（PlayMode，`LifeBackgroundEvolutionTests`，12 条）

两类，刻意分开：

**（一）真线程池上的所有权与身份**（后端直接被驱动）

| # | 测试 | 断言（要点） |
|---|---|---|
| 1 | `WhileComputing_EveryReadDescribesTheAdoptedGenerationOnly` | worker 正在算时，世代、人口、整盘读取**都仍是已接管的那一代**；接管后与**同步参考实现逐格相同**、人口相同 |
| 2 | `CompletedGeneration_IsWrittenByTheWorker_AndStillInvisibleUntilAdopted` | worker 写出新棋盘并发布之后，主线程读到的**仍然是旧的一代**；只有接管能改变它（不依赖时序，用受控任务） |
| 3 | `SecondStepIsRefused_AndAnUnadoptedResultIsNotOverwritten` | 在途时再提交 → 拒绝 +1；待接管时再提交 → 拒绝 +1；已等待的结果逐格未被扰动 |
| 4 | `ReplacedBoardRefusesTheOldResult_AndKeepsItsOwnNumbering` | 换盘后旧结果被拒 +1、新盘保留、流水线继续；新盘的第一代是 1（不是旧盘的下一个号） |
| 5 | `EditThenStep_ContinuesTheDisplayedGenerationNumbering` | 编辑后继续从显示世代往下编号（不回到 0） |
| 6 | `Dispose_RefusesTheResultInFlight_AndDoesNotWait` | 销毁 <50 ms 返回、不阻塞主线程；在途结果既不入队也不可接管 |

**（二）控制器里的操作交叉顺序**（同一个后端，任务由测试持有）

| # | 测试 | 断言（要点） |
|---|---|---|
| 7 | `PauseDuringCompute_FreezesTheDisplay_AndResumeTakesTheResultInOrder` | 暂停时显示**不动**；算完的一代被**留着**；恢复时按顺序接管，且**没有重算**（提交次数不变）、没有被拒 |
| 8 | `RunningClock_DoesNotPileUpWorkBehindAnUnfinishedGeneration` | 时钟一直等，不堆积提交（`RefusedSubmissions == 0`），过载标志为真；放行后只接管那**一代** |
| 9 | `SingleStep_TakesOverTheWaitingGeneration_InsteadOfComputingAnother` | 单步优先接管等待中的一代，不另起一次计算 |
| 10 | `SingleStep_SubmitsExactlyOneGeneration_AndSaysItIsComputing` | 单步只提交一次；界面显示「单步计算中…」；再按一次不会排队第二代 |
| 11 | `ResetDuringCompute_RefusesTheOldGeneration_AndKeepsTheResetBoard` | 重置期间算完的一代上不了屏、被计数拒绝；随后单步从重置后的棋盘正常出代 |
| 12 | `SwitchingBackend_DoesNotLetTheOldCpuResultLand` | 切到 GPU 时 CPU 结果不动 GPU 棋盘；切回来仍是重启后的棋盘，旧结果被拒并计数 |

**证伪（每条只改一处）。**

1. **去掉接管时的版本与世代校验**（`TryAdoptCompletedGeneration` 里的那一个 `if`）：
   12 条里 **3 条失败** —— `ReplacedBoardRefusesTheOldResult_…`（陈旧结果被接管）、
   `ResetDuringCompute_…`（重置被旧代覆盖）、`SwitchingBackend_…`（切换后旧结果落地）。
2. **让控制器在暂停时也接管**（`PumpEvolution` 的 `running || singleStepOutstanding` 改成恒真）：
   12 条里 **2 条失败** —— `PauseDuringCompute_…`（「暂停中的时钟显示了一代它没有接管的棋盘」）
   与 `SingleStep_TakesOverTheWaitingGeneration_…`。

两条都只动一处、都在 `tr-*.xml`（`.gitignore` 内）中留过现场；上面记录的是命令与观察到的输出。

---

## 4. 实机检查（`stage-c-bench-r5.jsonl`，5 条）

条件与阶段 C 相同：900×700 窗口、视口 615×456、`vSyncCount=0`、`targetFrameRate=-1`、
`runInBackground=true` 且**不再还原**（StageC §2.3）；每轮一个进程，外部 `WaitForExit(180000)`，
五轮全部自行退出（12–40 s），**守卫一次都没触发**。

### 4.1 界面：帧不再包含计算

CPU 后端（滑块最大速率 20 代/秒），同尺寸下 r4（同步）与本轮（后台）对比：

| 盘面 | r4 帧中位（同步） | r5 帧中位（后台） | r5 帧均值 | r5 帧最大 | r5 上传中位 |
|---|---|---|---|---|---|
| 256² | 0.265 ms | **0.270 ms** | 0.394 | 4.71 | 0.14 ms |
| 1024² | 0.391 ms | **0.263 ms** | 0.384 | 4.37 | 2.10 ms |
| 2048² | **206.4 ms** | **0.269 ms** | 0.417 | 19.69 | 13.47 ms |
| 4096²（帧时间构建） | **739.2 ms** | **0.267 ms** | 0.415 | **63.08** | 50.38 ms |
| 4096²（发布构建） | 746.2 ms | **0.283 ms** | 0.432 | **72.02** | 56.34 ms |

- 256²/1024² 的同步帧中位本来就小（一步 2.9/41 ms，且被 50 ms 间隔摊开），所以这两行前后接近；
  真正被搬走的是 **2048²/4096² 的那 206/739 ms**。
- **帧最大仍在 19.7 ms（2048²）与 63–72 ms（4096²）**，与整盘上传的中位数量级一致（13.5/50–56 ms）：
  计算走了，**上传没走**。这就是 §2.5 说的那一笔，也是评审要求 5 点名的「仍可能造成卡顿」。
- 上传值来自组件自己的粘性计数器（记录里的 `gridUploadCounterNote` 写明：它只保留最近一次成本，
  所以「帧计数」不是上传次数）；StageC §6.1 的组件测量是另一条路径，两者不互相证明。

### 4.2 四笔成本分开记录（**从不相加**）

| 盘面 | 后台计算（worker，Step） | 结果复制（worker，读盘面） | 提交前交接复制（主线程） | 整盘上传（主线程） |
|---|---|---|---|---|
| 256² | 2.57 ms | 0.29 ms | 0.059 ms | 0.14 ms |
| 1024² | 37.36 ms | 4.57 ms | 0.121 ms | 2.10 ms |
| 2048² | 161.61 ms | 19.82 ms | 0.273 ms | 13.47 ms |
| 4096²（帧时间构建） | 653.60 ms | 78.70 ms | 0.954 ms | 50.38 ms |
| 4096²（发布构建） | 686.41 ms | 80.62 ms | 1.036 ms | 56.34 ms |

- 四列**发生在不同线程、不同时刻**：把它们相加成「一代的成本」正是阶段 A 拒绝过的那种归因。
- 交接复制（把 `display` 拷进 worker 要重建的那块缓冲）**只在棋盘被命令改过之后**发生一次，
  量级 0.06–1.04 ms：它是整盘 memcpy，与上传是两件事。
- 结果复制（worker 用公开的 `LifeSimulation.IsAlive` 逐格读盘面）是**本阶段新增的成本**，
  4096² 上约等于一步的 12%。
- `backgroundComputeMs` 有 40/38/9/2/2 个样本（每接管一代一个），不是每帧采样。

### 4.3 暂停响应（记录里的 `pauseResponse`）

| 盘面 | 暂停时是否有在途计算 | 暂停命令耗时 | 暂停期间显示推进 | 算完的一代被暂存 | 恢复时按顺序 +1 |
|---|---|---|---|---|---|
| 256² | 是 | 0.061 ms | 0 | 是 | 是 |
| 1024² | 是 | 0.012 ms | 0 | 是 | 是 |
| 2048² | 是 | 0.014 ms | 0 | 是 | 是 |
| 4096²（帧时间） | 是 | 0.010 ms | 0 | 是 | 是 |
| 4096²（发布） | 是 | 0.011 ms | 0 | 是 | 是 |

- 「暂停命令耗时」是主线程上那条命令本身，**不是帧时间**；它一直很小，因为暂停本来就只是翻标志。
  **真正变好的是命令之后**：暂停后 0.5 s 观察窗内的帧中位 **0.25–0.26 ms**、最大 **3.3–4.6 ms**
  （本轮五轮如此；窗口里是否含一次接管 + 上传取决于时序，那会到 20 ms 量级）。
- 五轮全部：**在途的一代算完后进了等待槽**（`completionWaitingWhilePaused=true`）、
  暂停期间显示**一代都没推进**（`advancedWhilePaused=0`）、恢复后**恰好 +1**（`advancedByOneOnResume=true`）、
  `refusedGenerations=0`（没有一代被作废）。这是要求 4 在真实 Player 上的证据。

### 4.4 吞吐：没有提升，也没有量级变化

同一条记录里，`evolution.cpuMsPerGeneration` 是本探针**自己**用同步 `CpuLifeBackend` 跑出来的
参考值，`backgroundComputeMs` 是同一进程、同一盘面上 worker 的实际步进成本：

| 盘面 | 同步参考步进 | worker 步进 | 差 |
|---|---|---|---|
| 256² | 2.93 ms | 2.57 ms | −12% |
| 1024² | 40.97 ms | 37.36 ms | −9% |
| 2048² | 161.38 ms | 161.61 ms | +0.1% |
| 4096²（帧时间） | 627.44 ms | 653.60 ms | +4% |
| 4096²（发布） | 624.68 ms | 686.41 ms | +10% |

**只能说这么多**：两者**同量级**，差在 −12%~+10% 之间——而**参考值本身在相邻两轮之间就有 ±10% 的
波动**（256² 的参考步进在两次运行里分别是 2.35 与 2.93 ms），所以本阶段给不出更细的差值，
也不能说「变快了」。规则一步的成本没有量级变化，**吞吐上限因此没有量级变化**。
每代的**新增**成本是结果复制（4096² 上 79–81 ms），以及本来就有、仍留在主线程的整盘上传。

达成速率（同一时刻的完成世代差 ÷ 墙钟）：256² **19.99 代/秒**（请求 20）、1024² **18.99**、
2048² **4.49**、4096² **1.00**（该窗口只有 2 代，粒度很粗）。
1024² 的 −5% 可以对账：每代 37.4 ms 步进 + 4.6 ms 结果复制 + 2.1 ms 上传 ≈ 44 ms，
再加上时钟间隔（50 ms）的相位，形成约 52.6 ms 的节拍。
控制器自报的速率窗口在 4096² 上读作 2.00——这正是 StageC §6.4 写明的量化：
0.5 秒窗口按整代计数，分辨力约 2 代/秒。

---

## 5. 本阶段**不**得出的结论

- **不声称吞吐提升**：§4.4 是「同一量级、没有量级变化」，并附上每代新增的结果复制成本。
- **不声称单步不卡顿**：整盘上传仍在主线程，4096² 上一帧最多 63–72 ms（§4.1）。
  把它也搬走（例如让 worker 直接产出上传格式、或只在可见区域上传）是**下一项独立的决定**，
  本阶段不做，也没有测过它能否成立。
- **不声称 GPU 路径改变**：GPU 后端仍然同步、仍然直接渲染自己的缓冲；本轮把它当参照物
  （同尺寸下 GPU 运行场景的帧中位 0.269–0.307 ms 未变）。
- **不说暂停是「零成本」**：暂停命令很便宜（≤0.06 ms），但**在途的那一代仍会算完**——
  只是算在 worker 上、结果进等待槽。这是设计，不是缺陷，记录里 `stepInFlightAtPause` 每轮都是 true。
- **不把四笔成本相加**，也不跨线程比较它们的大小关系（§4.2）。

---

## 6. 已知限制

| 限制 | 说明 |
|---|---|
| 单机单配置 | 7800X3D / RTX 4070 Ti / D3D12，一种窗口（视口 615×456）；换机器，帧与上传两列都会变 |
| 大尺寸样本少 | CPU 后端场景 2 秒窗口，4096² 只装得下 2 代；每代的成本数字可靠，**速率**在小数上很粗 |
| 暂停响应窗口短 | 0.5 s 观察窗（有在途计算时延长到它落槽），没有做长时间挂机或反复暂停/恢复的耐久测试 |
| 交接复制只有两个尺寸有值 | §4.2 已说明取数方式；2048²/4096² 记 null |
| 结果复制用公开 API | worker 通过 `LifeSimulation.IsAlive` 逐格读盘面（`LifeSimulation` 本阶段不改），成本记在 `backgroundResultCopyMs`；4096² 上约一步的 11% |
| 内存 +2 B/格 | 终端现在持有 4 块盘面缓冲（原同步 CPU 后端 2 块），4096² 约 +33.5 MB（§2.1） |
| 单步变成异步 | 界面按钮语义改为「提交并等待接管」；Phase 期间的测试与文档已同步（多帧等待） |
| 没有做 GPU→CPU 回读优化 | 与阶段 A 一致：GPU 后端的棋盘不与主线程交换 |

---

## 7. 复现

```powershell
# 构建（与阶段 C 相同）
unity build . --target StandaloneWindows64 -o Builds\LifeTerminal-bench.exe `
    --execute-method ConwayGameOfLife.EditorTools.PlayerBuild.BuildBenchmarkWindows64
unity build . --target StandaloneWindows64 -o Builds\LifeTerminal-release.exe `
    --execute-method ConwayGameOfLife.EditorTools.PlayerBuild.BuildReleaseWindows64

# 每轮一个进程，外部超时守卫随进程走
foreach ($size in '256x256','1024x1024','2048x2048','4096x4096') {
    $p = Start-Process Builds\LifeTerminal-bench.exe -PassThru -ArgumentList `
        "-screen-width 900 -screen-height 700 -screen-fullscreen 0 -lifeBoard $size -lifeBench"
    if (-not $p.WaitForExit(180000)) { $p.Kill(); throw "guard fired for $size" }
}

# 测试
unity test . --mode EditMode  --output test-results-editmode.xml
unity test . --mode PlayMode  --output test-results-playmode.xml
```

- `stage-c-bench-r5.jsonl`：本轮记录（5 条 = 帧时间构建 ×4 + 发布构建 ×1）。
  每条带 `recordRound: 5`、`buildGuid`、`dataPath`，帧场景里新增
  `backgroundComputeMs` / `backgroundResultCopyMs` / `backgroundHandoverCopyMs` /
  `controllerEverOverloaded` / `controllerOverloadFrames`，以及一个 `pauseResponse` 块。
- 上一轮（同步）记录 `stage-c-bench-r4.jsonl` 原样保留，§4.1 的对比列引自它。
- 过载标志的读法在本轮变了：后台后端下时钟每帧都会越过间隔并丢弃欠账，标志**每帧翻转**，
  所以记录里既给「窗口内是否出现过」（`controllerEverOverloaded`）也给它出现的帧数，
  末尾单次读数单独记作 `controllerClockOverloadedAtEnd`（StageC §6.2/§6.4 里的同名旧字段名已弃用）。
