# API 手册

## 概述

Bridge Server 对外提供两类接口：
- **HTTP API:** 管理类（健康检查、工作区、会话列表等）
- **WebSocket:** 全双工命令与事件（聊天发送、流式输出、多端同步）

## 认证方式

默认仅允许 `127.0.0.1` 访问；开启局域网访问后：
- 远程设备需先配对获取 **设备令牌**（deviceToken），并在 HTTP/WS 请求中携带 `Authorization: Bearer <deviceToken>`。
- 管理类接口（设备列表/撤销、会话创建/删除等）建议保持仅回环可用。

兼容性：如显式配置 `Bridge:Security:BearerToken`，服务端仍会将其作为“全局令牌”接受（用于调试/旧客户端），但推荐使用设备令牌以支持逐设备撤销。

---

## 接口列表

### System

#### [GET] /api/v1/health
**描述:** 健康检查

#### [GET] /status
**描述:** 上下文用量摘要（文本；包含 5h限额/周限额/上下文用量；缺失项显示“不可用”）

### Workspaces

#### [GET] /api/v1/workspaces
**描述:** 获取可用工作区列表（最近使用/固定）（规划）

#### [POST] /api/v1/workspaces/select
**描述:** 选择当前工作区（影响后续 Codex 运行目录）（规划）

### Sessions

#### [GET] /api/v1/sessions
**描述:** 列出会话（已实现）

**说明（当前实现）:**
- 数据来源：`%USERPROFILE%\\.codex\\sessions`（读取首行 `session_meta`，并尝试从后续 JSONL 中提取“首条 user 消息”作为标题）
- 标题提取：会对 user 消息做清洗（过滤环境/指令上下文），并在检测到 `## My request for Codex:` 包装时仅取该段内容
- 支持 `?limit=`（默认 30）

#### [POST] /api/v1/sessions
**描述:** 创建会话（已实现）

**说明（当前实现）:**
- 权限：仅回环可用（避免远程任意 cwd 写入/创建会话）
- 写入：在 `~/.codex/sessions/YYYY/MM/DD/` 下创建最小 `session_meta` JSONL 文件
- 请求体（必填）：`{ "cwd": "C:\\path" }`（需为存在且可访问的目录）
- 兼容性：会写入 `session_meta.payload.cli_version`（Codex resume 必填）

#### [GET] /api/v1/sessions/{sessionId}/messages
**描述:** 获取会话历史消息（已实现）

**说明（当前实现）:**
- 数据来源：`%USERPROFILE%\\.codex\\sessions`（JSONL）
- 解析规则：筛选 `type=response_item` 且 `payload.type=message` 的消息
- 过滤规则：仅返回 `role=user|assistant`；user 文本会过滤环境/指令上下文，并在检测到 `## My request for Codex:` 时仅保留该段内容
- 图片回放：当 `payload.content` 内包含 `input_image/output_image`（data URL）时，返回 `images`（字符串数组），供前端解码渲染
- 回放增强：assistant 消息可包含可选字段 `trace`（按时间顺序），用于展示该次回复中的思考/命令等过程信息
- 支持 `?limit=`（默认 200，最大 2000，返回末尾 N 条）

**trace 结构（可选）:**
- reasoning：从 `event_msg.payload.type=agent_reasoning` 与 `response_item.payload.type=reasoning` 提取
- command：从 `response_item.payload.type=function_call/function_call_output` 提取（如 `shell_command`）

```json
{
  "role": "assistant",
  "text": "...",
  "images": ["data:image/png;base64,..."],
  "trace": [
    { "kind": "reasoning", "title": "Evaluating ...", "text": "..." },
    { "kind": "command", "tool": "shell_command", "command": "rg -n ...", "status": "completed", "exitCode": 0, "output": "..." }
  ]
}
```

补充：启用服务端自动翻译并命中缓存时，reasoning 的 `title/text` 可能为译文；原始 `~/.codex/sessions/*.jsonl` 不会被修改

#### [GET] /api/v1/sessions/{sessionId}/settings
**描述:** 获取会话最新的 `sandbox/approvalPolicy` 设置（已实现）

**说明（当前实现）:**
- 数据来源：`%USERPROFILE%\\.codex\\sessions`（JSONL）
- 提取方式：从会话文件末尾（tail）best-effort 扫描并提取 `approval_policy` / `sandbox_mode`（也兼容 `approvalPolicy` / `sandbox`）

**响应:**
```json
{ "sandbox": "workspace-write", "approvalPolicy": "never" }
```

#### [GET] /api/v1/sessions/{sessionId}/plan
**描述:** 获取会话最新计划（turn plan）（已实现）

**说明（当前实现）:**
- 数据来源：Bridge Server 内存缓存（由 `codex app-server` 的 `turn/plan/updated` 推送更新）
- 用途：前端进入会话/重连时回填计划展示
- 未命中缓存：返回 404

**响应:**
```json
{
  "sessionId": "thread_xxx",
  "turnId": "turn_xxx",
  "explanation": "可选说明",
  "updatedAt": "2026-01-21T09:42:00Z",
  "plan": [
    { "step": "…", "status": "pending|inProgress|completed" }
  ]
}
```

#### [POST] /api/v1/sessions/{sessionId}/archive
**描述:** 归档/隐藏会话（规划）

---

### Connections

#### [POST] /api/v1/connections/pairings
**描述:** 创建短时配对邀请码（仅回环可用）

**响应:**
```json
{ "pairingCode": "...", "expiresAt": "..." }
```

#### [POST] /api/v1/connections/pairings/claim
**描述:** 设备扫码/输入邀请码后发起配对请求（远程允许；不需要 Bearer）

**请求:**
```json
{ "pairingCode": "...", "deviceName": "...", "platform": "android", "deviceModel": "...", "appVersion": "..." }
```

**响应:**
```json
{ "requestId": "...", "pollAfterMs": 800, "expiresAt": "..." }
```

#### [GET] /api/v1/connections/pairings/{requestId}
**描述:** 轮询配对结果（远程允许；不需要 Bearer）

**响应:**
```json
{ "status": "pending|approved|declined|expired|remoteDisabled", "deviceId": "...", "deviceToken": "...", "tokenDelivered": false }
```

#### [POST] /api/v1/connections/pairings/{requestId}/respond
**描述:** 本机确认/拒绝配对请求（仅回环可用）

**请求:**
```json
{ "decision": "approve|decline" }
```

#### [GET] /api/v1/connections/devices
**描述:** 获取已配对设备列表（仅回环可用）

#### [DELETE] /api/v1/connections/devices/{deviceId}
**描述:** 撤销设备（仅回环可用；撤销后立即断开并失效）

---

## WebSocket

#### [WS] /ws
**描述:** 全双工命令与事件通道，用于聊天与多端同步。

**消息基本结构（建议）:**
```json
{
  "protocolVersion": 1,
  "type": "command|event|response",
  "id": "uuid(仅command/response)",
  "name": "chat.send|run.event|session.updated|...",
  "ts": "ISO-8601",
  "data": {}
}
```

**鉴权说明（当前实现）:**
- 默认仅允许 `127.0.0.1` 回环访问（不需要令牌）
- 当 `Bridge:Security:RemoteEnabled=true` 时：
  - 远程设备需带 `Authorization: Bearer <deviceToken>`
  - 本机回环依然无需令牌

**已实现消息（MVP 骨架）:**
- command `chat.send`：`{ "prompt": "string(optional)", "images": ["data:image/...;base64,..."] (optional), "sessionId": "uuid(optional)", "workingDirectory": "C:\\path(optional)", "model": "o3(optional)", "sandbox": "workspace-write(optional)", "approvalPolicy": "on-request(optional)", "effort": "high(optional)", "skipGitRepoCheck": false }`
  - 说明：`prompt` 允许为空，但 `prompt/images` 至少其一需要存在
  - 默认值：当 `sandbox/approvalPolicy` 未提供或为 `null` 时，服务端会优先从该 `sessionId` 的会话文件提取最新值；新会话则从 `~/.codex/config.toml` 读取（键：`sandbox_mode`、`approval_policy`）
- 并行：允许跨 `sessionId` 并行；同一 `sessionId` 同时仅允许一个运行中的任务（超出会返回 `run.rejected`）
- command `run.cancel`：`{ "runId": "string(optional)", "sessionId": "string(optional)" }`
  - 说明：`runId/sessionId` 至少其一需要存在；仅提供 `sessionId` 时取消该会话当前 active run
- command `approval.respond`：`{ "runId": "...", "requestId": "...", "decision": "accept|acceptForSession|decline|cancel" }`
- 说明：当前运行链路基于 `codex app-server`（JSON-RPC），支持审批请求与流式 delta；`skipGitRepoCheck` 保留用于兼容旧链路/未来回退
- event `bridge.connected`：`{ "clientId": "..." }`
- event `session.created`：`{ "runId": "...", "sessionId": "..." }`
- event `chat.message`：`{ "runId": "...", "sessionId": "thread_xxx(optional)", "role": "user|assistant", "text": "string", "images": ["data:image/...;base64,..."] (optional), "clientId": "..." }`
- event `chat.message.delta`：`{ "runId": "...", "itemId": "item_3", "delta": "..." }`
- event `approval.requested`：`{ "runId": "...", "requestId": "...", "kind": "commandExecution|fileChange", "threadId": "...", "turnId": "...", "itemId": "...", "reason": "..." }`
- event `run.started`：`{ "runId": "...", "sessionId": "thread_xxx(optional)", "clientId": "..." }`
- event `run.plan.updated`：`{ "runId": "...", "threadId": "...", "turnId": "...", "explanation": "...", "plan": [{ "step": "...", "status": "pending|inProgress|completed" }], "updatedAt": "..." }`
- event `run.command`：`{ "runId": "...", "itemId": "item_0", "command": "...", "status": "inProgress|completed|failed|declined", "exitCode": 0, "output": "..." }`
- event `run.command.outputDelta`：`{ "runId": "...", "itemId": "item_0", "delta": "..." }`
- event `run.reasoning`：`{ "runId": "...", "itemId": "item_1", "text": "...", "translated": true, "translationLocale": "zh-CN" }`
  - 说明：当启用服务端自动翻译时，服务端可能会对同一 `itemId` 先广播原文、后广播译文（用于覆盖替换）；命中缓存时也可能直接广播译文
- event `run.reasoning.delta`：`{ "runId": "...", "itemId": "item_1_summary_0", "textDelta": "..." }`
- event `diff.updated`：`{ "runId": "...", "threadId": "...(optional)", "files": [{ "path": "...", "diff": "...", "added": 1, "removed": 1 }] }`
- event `diff.summary`：`{ "runId": "...", "threadId": "...(optional)", "files": [{ "path": "...", "added": 1, "removed": 1 }], "totalAdded": 1, "totalRemoved": 1 }`
- event `run.cancel.requested`：`{ "clientId": "...", "runId": "...", "sessionId": "thread_xxx(optional)" }`
- event `run.completed`：`{ "runId": "...", "sessionId": "thread_xxx(optional)", "exitCode": 0 }`
- event `run.canceled`：`{ "runId": "...", "sessionId": "thread_xxx(optional)" }`
- event `run.failed`：`{ "runId": "...", "sessionId": "thread_xxx(optional)", "message": "..." }`
- event `run.rejected`：`{ "reason": "...", "clientId": "...(optional)", "sessionId": "thread_xxx(optional)" }`
