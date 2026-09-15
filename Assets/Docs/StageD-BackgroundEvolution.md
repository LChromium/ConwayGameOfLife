# 阶段 D：CPU 演化移到后台

**目标（评审原话）**：**计算期间界面仍能响应；不承诺算法吞吐提升。**

**范围**：只动 CPU 演化路径。GPU 后端保持同步；**不**追加 Burst、GPU 独立噪声或密度总览；
CPU 规则参考实现 [`LifeSimulation`](../Scripts/LifeSimulation.cs) **一行未改**。
本轮只新增两个文件（`ILifeAsyncBackend`、`LifeAsyncCpuBackend`）与一个 PlayMode fixture。

> **本阶段不是算法优化。** 同一份 `LifeSimulation`、同样的格数、同样的规则、同样的每代成本
> （§4.4 逐尺寸对照：worker 与同进程的同步参考**同量级**，差在 −12%~+10%，
> 而参考值自身在相邻两轮之间就有 ±10% 的波动）。变的只有**哪条线程被阻塞**。

---

## 0. 评审第九轮之后的收尾（本文件已包含）

| # | 评审指出 | 处理 |
|---|---|---|
| 1 | **[P2] 边界切换使旧结果失效后，没有恢复 worker 的正确起点**（worker 会从自己的旧世代继续算，于是「算一代、拒一代」，显示不再前进） | `WrapEdges` setter 与**任何一次拒绝**都置 `resyncRequired`，下一次提交**从显示棋盘重建**；新增两条边界切换测试（在途 / 待接管），断言拒绝后的一代与新边界下的同步参考**逐格相同**，并断言两种边界在该盘面上结果不同（否则测试是空的）。证伪三：去掉重建后恰好这两条失败，报错正是「拒绝循环」的形状 |
| 2 | **[P1] 异常路径没有复用请求身份，错误状态也无法正常恢复** | 失败与成功**同一套身份规则**：只有 `version == sessionVersion && !disposed` 的失败被记录，旧失败只回收缓冲；失败一律置 `resyncRequired`（模拟可能已经推进）；控制器收到当前失败即**停止自动提交**、显示停在最后一个完整世代、读数「演算失败」；只有显式动作（运行 / 单步 / 换盘）清除失败并安全重建；新增删除/清除路径（`ClearFailure`）。新增三条故障注入测试 |
| 3 | **[P2]「上传就是最差帧来源」归因过强** | §4.1/§5 改为：上传是**已测得的显著阻塞来源**（50–56 ms），与最差帧（63–72 ms）同量级、时间上相容，但**本轮没有证明它解释了最差帧的全部耗时**；并写明 §4.2 那 0.95 ms 是**重建前复制**，不是每代接管成本（接管交换引用） |
| 4 | **文档口径与旧微基准** | 开头的「相差 ≤4%」改为「同量级，−12%~+10%」；删掉「交接复制只有两个尺寸有值」等过时限制；**删除**已失去原用途的 `MicroBenchmark_StepCostInsideTheEditorRuntime`（在后台 CPU 上它测的是提交而不是步进），并把引用它的历史文档标为「历史记录、不再可复现」（TechnicalAnalysis §5.3、StageArchive §3） |

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

### 2.6 失败：与成功同一套身份规则

worker 抛异常时（注入的失败、OOM、任何 `Compute` 内的异常）：

| 处理 | 为什么 |
|---|---|
| **先回收**：`computing = false`、结果缓冲归还、**`resyncRequired = true`** | 异常可能发生在模拟**已经推进之后**，worker 的状态可能已经领先于显示；重试必须从显示棋盘重建 |
| **只有当前会话的失败才记录**：`version == sessionVersion && !disposed` | 与成功结果同一条身份规则：为已经被换掉的棋盘抛出的失败只回收资源，**不污染当前会话** |
| 控制器：当前失败 → **停止自动提交**（`Stop()`）、显示保持**最后一个完整世代**、读数显示「演算失败」+ tooltip 给出原因 | 失败的流水线不能再自己往下提交；界面也不该装作只是慢 |
| 恢复只能由**显式动作**触发：按下「运行」、按「单步」、或换盘（载入/清空/编辑/切后端） | 后端在这三类动作里清掉失败；换盘另在 `MoveBoardLocked` 里清 |
| 下一次提交**必然重建**（`resyncRequired` 已置位），并且 `LifeStepOutcome.BoardLoadMilliseconds > 0` 可作证据 | 重试不能从「可能已经超前的 worker 状态」继续 |
| **不会自愈** | 失败一直显示到有人动手；一次失败不会被时间或下一帧悄悄抹掉 |

测试用的故障注入不是新框架：`LifeAsyncCpuBackend` 的规则步进与调度器一样是构造参数
（`Action<LifeSimulation> stepRule`，默认 `LifeSimulation.Step`，与 `LifeSeedingSession`
注入生成器的做法一致），所以「先推进再抛异常」这种最难的形状可以精确构造。

---

## 3. 可控任务验证（PlayMode，`LifeBackgroundEvolutionTests`，17 条）

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

**（三）规则改变与失败：拒绝之后必须能恢复**（受控任务 + 注入的规则步进）

| # | 测试 | 断言（要点） |
|---|---|---|
| 13 | `BoundaryChangeWhileComputing_RefusesTheOldResult_AndRebuildsUnderTheNewRule` | 在途时改边界 → 旧结果被拒 +1、显示不动；下一代**从显示棋盘重建**（`BoardLoadMilliseconds > 0`）、编号为 1、与**新边界下的同步参考逐格相同**；并断言两种边界在该盘面上结果不同（否则测试是空的） |
| 14 | `BoundaryChangeWithAWaitingResult_RefusesIt_AndRebuildsUnderTheNewRule` | 同上，但结果**已经算完在等待**时改边界：同样拒绝、重建、逐格一致 |
| 15 | `Failure_StopsTheClockAndKeepsTheLastCompleteGeneration` | 注入「先推进再抛异常」：失败被报告、时钟停止、显示保持最后完整世代、读数「演算失败」；20 帧内**不再有任何自动提交** |
| 16 | `RetryAfterAFailure_RebuildsFromTheDisplayBoard_AndKeepsTheNumbering` | 按「运行」重试 → 失败被清除、读数恢复正常、**下一代从显示棋盘重建**（不是从失败任务已经超前的棋盘），与参考逐格一致 |
| 17 | `StaleFailure_DoesNotPolluteTheReplacedBoard` | 换盘之后才落地的失败**不记录**、不发布、不阻塞流水线；下一次提交正常出代并与参考逐格一致 |

**证伪（每条只改一处，全部实测）。**

1. **去掉接管时的版本与世代校验**（`TryAdoptCompletedGeneration` 里的那一个 `if`）：
   17 条里 **5 条失败** —— 两条边界切换，以及 `ReplacedBoardRefusesTheOldResult_…`、
   `ResetDuringCompute_…`、`SwitchingBackend_…`（陈旧结果被接管）。
2. **让控制器在暂停时也接管**（`PumpEvolution` 的 `running || singleStepOutstanding` 改成恒真）：
   17 条里 **2 条失败** —— `PauseDuringCompute_…`（「暂停中的时钟显示了一代它没有接管的棋盘」）
   与 `SingleStep_TakesOverTheWaitingGeneration_…`。
3. **拒绝之后不重建**（去掉 `WrapEdges` setter 与拒绝分支里的 `resyncRequired = true`）：
   **恰好两条边界切换测试失败**，报错为「the generation after the rebuild should be adopted」——
   正是评审指出的形状：worker 从自己那个（旧规则下、已经领先的）世代继续算，
   每个结果都因为「不是显示世代的下一代」被拒，**显示再也不前进**。

三条都只动一处、都在 `tr-*.xml`（`.gitignore` 内）中留过现场；上面记录的是命令与观察到的输出。

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
- **主线程的整盘复制与上传仍是已测得的显著阻塞来源**（中位 13.5 ms / 50–56 ms），
  也是本阶段之后**下一项优化对象**的候选。**本轮还没有证明它解释了最差帧的全部耗时**：
  上传值来自逐帧重复读取的粘性字段，没有与 63–72 ms 那一帧逐项关联；
  帧最大 19.7/63–72 ms 与它同量级，只能说「量级一致、时间上相容」，不能说「就是它」。
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
- **「交接复制」不是每代接管成本，也不与「接管零拷贝」矛盾**：接管本身只交换两个缓冲的引用
  （§2.1），这一列是**重建之前**把 `display` 拷给 worker 的那一次 memcpy（0.06–1.04 ms，
  只在棋盘被命令改过、或边界改变、或一次拒绝之后发生）。每代都会发生的是**结果复制**（第 2 列）
  与**整盘上传**（第 4 列），不是这一列。
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
- **不声称整盘上传就是最差帧的来源**：上传是**已测得的显著阻塞来源**（50–56 ms），
  与最差帧（63–72 ms）同量级、时间上相容，但本轮没有把两者逐项关联，
  因此**没有证明它解释了最差帧的全部耗时**（§4.1）。
- **不声称单步不卡顿**：整盘上传与复制仍在主线程，4096² 上一帧最多 63–72 ms（§4.1）。
  把它也搬走（例如只准备并上传可见区域、或让 worker 直接产出上传格式）是**下一项独立的决定**，
  本阶段不做，也没有测过它能否成立——Unity 图形资源操作不能直接套一层 `Task.Run`。
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
| 上传与最差帧未逐项关联 | §4.1：上传值来自粘性字段的逐帧重复读取，只能说同量级、时间上相容 |
| 结果复制用公开 API | worker 通过 `LifeSimulation.IsAlive` 逐格读盘面（`LifeSimulation` 本阶段不改），成本记在 `backgroundResultCopyMs`；4096² 上约一步的 12% |
| 内存 +2 B/格 | 终端现在持有 4 块盘面缓冲（原同步 CPU 后端 2 块），4096² 约 +33.5 MB（§2.1） |
| 单步变成异步 | 界面按钮语义改为「提交并等待接管」；Phase 期间的测试与文档已同步（多帧等待） |
| 失败恢复需要显式动作 | 当前会话的失败会停止自动提交并保持显示最后一个完整世代；恢复要按「运行」「单步」或换盘（§2.6），不会自愈 |
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
