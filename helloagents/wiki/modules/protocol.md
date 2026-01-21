# Protocol

## 目的
定义前后端以及多端（Windows/Android）之间的同步协议，保证流式输出与会话状态一致。

## 模块概述
- **职责:** 消息结构、版本化策略、事件类型定义、错误模型
- **状态:** 开发中
- **最后更新:** 2026-01-19

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
