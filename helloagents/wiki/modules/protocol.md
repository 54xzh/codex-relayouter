# Protocol

## 目的
定义前后端以及多端（Windows/Android）之间的同步协议，保证流式输出与会话状态一致。

## 模块概述
- **职责:** 消息结构、版本化策略、事件类型定义、错误模型
- **状态:** 开发中
- **最后更新:** 2026-01-25

## 规范

### 需求: 事件驱动同步
**模块:** Protocol

#### 场景: 流式输出转发
服务端将 Codex 的 JSONL 事件映射为稳定的协议事件（必要时可透传原始事件以便调试）。
当前映射示例：
- `thread.started(thread_id)` → `session.created(sessionId)`
- `turn/plan/updated(threadId/turnId/plan/explanation)` → `run.plan.updated(threadId/turnId/plan/explanation/updatedAt)`
- `item.completed(item.type=agent_message, item.text)` → `chat.message(role=assistant, text)`
- `item.started|item.completed(item.type=command_execution)` → `run.command(itemId/command/status/exitCode/output)`
- `item.completed(item.type=reasoning, item.text)` → `run.reasoning(itemId/text)`

补充：`run.plan.updated.plan[].status` 取值与 `codex app-server` 对齐：`pending` / `inProgress` / `completed`。
补充：当启用服务端自动翻译时，`run.reasoning` 可能会对同一 `itemId` 重复广播用于覆盖替换，并附带可选字段 `translated=true` 与 `translationLocale=zh-CN`。

#### 场景: 消息封装（Envelope）
统一使用 JSON 消息封装，字段采用 camelCase（与 `System.Text.Json` Web 默认一致）：

```json
{
  "protocolVersion": 1,
  "type": "command|event|response",
  "id": "uuid(仅command/response)",
  "name": "chat.send|codex.line|run.completed|...",
  "ts": "ISO-8601",
  "data": {}
}
```

#### 场景: 会话绑定（resume）
`chat.send` 支持携带 `sessionId` 以续聊历史会话；服务端在首次运行时可通过 `session.created` 事件告知客户端本次运行对应的 `sessionId`。
当工作区不在 Git 仓库目录内时，`chat.send` 可携带 `skipGitRepoCheck`（等价于 Codex CLI `--skip-git-repo-check`）。

#### 场景: 多任务并行与取消（run）
- 并行模型：允许跨 `sessionId` 并行；同一 `sessionId` 同时仅允许一个运行中的任务，超出会返回 `run.rejected`
- `run.cancel`：`{ "runId": "string(optional)", "sessionId": "string(optional)" }`（至少一个）；仅提供 `sessionId` 时取消该会话当前 active run
- 事件路由：为便于多端归属与 UI 路由，服务端会在可确定时为 run 相关事件补齐 `sessionId`
  - 已包含/补齐的典型事件：`chat.message` / `chat.message.delta` / `run.command` / `run.command.outputDelta` / `run.reasoning` / `run.reasoning.delta` / `diff.updated` / `run.plan.updated` / `run.started/run.completed/run.canceled/run.failed` / `run.rejected`
  - 约定：当 `sessionId` 缺失且无法从上下文推断时，事件可能仍不含 `sessionId`（客户端需回退到 `runId -> sessionId` 映射）

#### 场景: 多端重连与运行快照（active runs）
为解决“客户端在 run.started 之后才连入 WS 导致无法得知当前哪些会话正在运行”的问题，服务端在 WS 握手后会下发运行快照：
- event `run.active.snapshot`
  - `activeRuns[]`: `{ sessionId, runId }` 列表（同一 `sessionId` 同时仅一个 `runId`）
  - 用途：会话列表页展示多会话 Running 指示器；进入正在运行的会话时可立即接上增量事件路由

#### 场景: 多模态输入（图片）
`chat.send` 支持携带 `images`（data URL 数组），用于将图片作为输入发送给模型；服务端会将其映射到 `codex app-server` 的 `turn/start.input`（`type=image` 且 `url=dataUrl`）。
历史回放接口 `/api/v1/sessions/{sessionId}/messages` 会解析 `input_image/output_image` 并返回 `images` 字段，前端可解码并渲染。

#### 场景: 设备配对与在线状态（连接）
为支持局域网 Android 设备接入，服务端新增“配对邀请码 + 本机确认”的连接流程，并补充设备在线状态事件：
- event `device.pairing.requested`：远程设备发起配对后，本机客户端收到请求并弹窗确认
  - `requestId` / `deviceName` / `platform` / `deviceModel` / `appVersion` / `clientIp` / `expiresAt`
- event `device.presence.updated`：设备 WebSocket 连接/断开导致在线状态变化（用于本机“连接”页展示）
  - `deviceId` / `online` / `lastSeenAt`

## 变更历史
- [202601172220_codex_gui_shell](../../history/2026-01/202601172220_codex_gui_shell/) - WS 协议 Envelope 与事件命名（MVP 骨架）
- [202601172341_winui_ws_chat](../../history/2026-01/202601172341_winui_ws_chat/) - 增补 `chat.message` 与 `chat.send(model/sandbox)` 说明
- [202601180102_session_management](../../history/2026-01/202601180102_session_management/) - 增补 `chat.send(sessionId)` 与 `session.created` 事件
- [202601180258_fix_run_no_reply](../../history/2026-01/202601180258_fix_run_no_reply/) - 增补 `chat.send(skipGitRepoCheck)` 与失败语义（exitCode 非 0 视为失败）
- [202601180700_filter_codex_json_events](../../history/2026-01/202601180700_filter_codex_json_events/) - 将 codex `--json` 控制事件映射为协议事件，避免 UI 显示原始 JSON
- [202601181348_trace_thinking](../../history/2026-01/202601181348_trace_thinking/) - 增补 `run.command` / `run.reasoning`（执行命令/思考摘要）
- [202601190157_chat_images](../../history/2026-01/202601190157_chat_images/) - 增补 `chat.send/images` 与会话回放 `images`（data URL）
- [202601211742_turn_plan_todo](../../history/2026-01/202601211742_turn_plan_todo/) - 增补 `run.plan.updated`（turn plan）与会话计划回填
- [202601220035_connections_pairing](../../history/2026-01/202601220035_connections_pairing/) - 增补 `device.pairing.requested` / `device.presence.updated` 与设备配对流程
