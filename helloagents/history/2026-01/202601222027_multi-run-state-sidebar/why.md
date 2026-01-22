# 变更提案: 多任务并行 + 会话状态保持 + 侧边栏状态指示

## 背景
当前存在两类体验问题：
- Bridge Server 侧对 `chat.send` 做了“全局单任务”限制，导致无法同时执行多个任务。
- WinUI 客户端的流式输出/计划/命令过程主要保存在页面实例内，切到其他页面再回到对话时，会出现“正文为空但仍在更新”的观感问题。

## 目标
- 支持多个会话并行执行任务（推荐并行粒度：以会话为单位，单会话内仍串行）。
- 离开对话或切换页面后，返回仍能看到运行中的累计输出与状态（不中断、不中丢）。
- 侧边栏会话项右侧展示状态：
  - 运行中：不确定进度指示器（ProgressRing）
  - 完成且不在该对话：绿点
  - 异常：黄点

## 范围
### 范围内
- Bridge Server：WebSocket 并行 run、取消/审批路由、并发广播安全。
- WinUI Client：会话级状态缓存、事件路由、ChatPage 显示逻辑、侧边栏指示器。
- 文档：协议与数据模型同步更新。

### 范围外
- Android 客户端 UI 适配（如需同步另开方案包）。
- “进度百分比/ETA” 等确定性进度（本需求仅需不确定进度指示器）。
- 跨应用重启的运行中状态持久化（本阶段仅保证切换页面/对话不丢）。

## 方案与关键决策
### 1) 并行模型（推荐）
- 并行粒度：允许不同 `sessionId(threadId)` 并行运行；同一 `sessionId` 同时仅允许一个 run（避免同一会话文件并发写入）。
- 可选：全局并行上限（例如 2~4），超出时返回 `run.rejected`（防止同时拉起过多 `codex app-server` 进程）。

### 2) 协议调整（最小必要）
- `run.cancel` 增加目标：`{ runId?: string, sessionId?: string }`（至少一个）。
- 事件路由所需的 session 归属：
  - 依赖已存在的 `turn.started { runId, threadId, turnId }` 建立 `runId -> sessionId(threadId)` 映射。
  - （建议）在 `run.started/run.completed/run.failed/run.canceled` 里补充 `threadId`，降低客户端缓冲/推断成本。

### 3) 服务器端实现要点
- 运行跟踪：
  - `ConcurrentDictionary<string, RunContext>`：`runId -> { cts, clientId, sessionId?, startedAt }`
  - `ConcurrentDictionary<string, string>`：`sessionId -> activeRunId`（同会话串行闸门）
- 取消：
  - 通过 `runId/sessionId` 找到对应 CTS 执行 Cancel。
- 审批：
  - 用 `ConcurrentDictionary<(runId, requestId), TaskCompletionSource<Decision>>` 支持多 run 并发审批。
- 广播并发安全：
  - 为每个 WebSocket 连接增加发送锁（SemaphoreSlim/Channel），确保并发 Broadcast 不会对同一 socket 并发 `SendAsync`。

### 4) WinUI 客户端状态保持与事件路由
- 新增全局 Store（App 单例）：
  - `SessionStore`：`sessionId -> 会话状态（Messages、TurnPlan、RunState、BadgeState）`
  - `RunStore`：`runId -> { sessionId?, status }`
  - `PendingEvents`：`runId -> 暂存事件`（当 sessionId 未确定时）
- ConnectionService 仅订阅一次 `EnvelopeReceived`，由 Store 统一分发；页面仅绑定 Store 数据，避免页面重建丢状态。
- ChatPage：
  - 根据当前选中 sessionId 显示对应 Messages/TurnPlan。
  - 发送/取消按钮仅影响当前 session 的 active run。
  - 切换页面/对话时不清空 Store。
- MainWindow：
  - 避免重复 `ContentFrame.Navigate(typeof(ChatPage))` 导致多实例订阅与状态断裂。
  - 侧边栏项从 Store 读取 `BadgeState` 并实时更新右侧指示器。

### 5) 侧边栏状态指示规则
- 优先级：运行中（ProgressRing） > 异常黄点 > 完成绿点 > 无。
- 触发：
  - `Running`：session 有 active run。
  - `GreenDot`：run 在该 session 完成时，当前不在该 session（或不在 ChatPage）。
  - `YellowDot`：run 在该 session 失败/取消/被拒绝时（建议同样仅在“非当前对话”时打点，并在进入该对话后清除）。
- 清除：
  - 用户进入该 session（ChatPage + sessionId 匹配）后清除绿/黄点；运行中状态随 run 生命周期自动变化。

## 验收标准
- 可在会话 A 运行中切换到会话 B 并发送新任务，两者均能正常流式输出且互不串台。
- 离开 ChatPage 或切换会话再返回，仍能看到运行中任务已累计的输出（不会回到空白）。
- 侧边栏：运行中显示 ProgressRing；完成且不在该会话显示绿点；异常显示黄点；进入会话后对应点位清除。

## 未决问题（需确认）
- 是否允许“同一会话”并行多个 run（默认不允许，建议串行）。
- 黄点是否要求在当前会话内也显示（默认仅在非当前会话时提示）。
- 是否需要全局并行上限（默认建议有上限）。
