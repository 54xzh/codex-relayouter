# API 手册

## 概述

Bridge Server 对外提供两类接口：
- **HTTP API:** 管理类（健康检查、工作区、会话列表等）
- **WebSocket:** 全双工命令与事件（聊天发送、流式输出、多端同步）

## 认证方式

默认仅允许 `127.0.0.1` 访问；开启远程访问时使用 Bearer Token（本地生成与保存，前端首次配对输入）。

---

## 接口列表

### System

#### [GET] /api/v1/health
**描述:** 健康检查

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

#### [POST] /api/v1/sessions/{sessionId}/archive
**描述:** 归档/隐藏会话（规划）

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
- 当 `Bridge:Security:RemoteEnabled=true` 时，必须带 `Authorization: Bearer <token>`

**已实现消息（MVP 骨架）:**
- command `chat.send`：`{ "prompt": "string(optional)", "images": ["data:image/...;base64,..."] (optional), "sessionId": "uuid(optional)", "workingDirectory": "C:\\path(optional)", "model": "o3(optional)", "sandbox": "workspace-write(optional)", "approvalPolicy": "on-request(optional)", "effort": "high(optional)", "skipGitRepoCheck": false }`
  - 说明：`prompt` 允许为空，但 `prompt/images` 至少其一需要存在
- command `run.cancel`：`{}`
- command `approval.respond`：`{ "runId": "...", "requestId": "...", "decision": "accept|acceptForSession|decline|cancel" }`
- 说明：当前运行链路基于 `codex app-server`（JSON-RPC），支持审批请求与流式 delta；`skipGitRepoCheck` 保留用于兼容旧链路/未来回退
- event `bridge.connected`：`{ "clientId": "..." }`
- event `session.created`：`{ "runId": "...", "sessionId": "..." }`
- event `chat.message`：`{ "runId": "...", "role": "user|assistant", "text": "string", "images": ["data:image/...;base64,..."] (optional), "clientId": "..." }`
- event `chat.message.delta`：`{ "runId": "...", "itemId": "item_3", "delta": "..." }`
- event `approval.requested`：`{ "runId": "...", "requestId": "...", "kind": "commandExecution|fileChange", "threadId": "...", "turnId": "...", "itemId": "...", "reason": "..." }`
- event `run.started`：`{ "runId": "...", "clientId": "..." }`
- event `run.command`：`{ "runId": "...", "itemId": "item_0", "command": "...", "status": "inProgress|completed|failed|declined", "exitCode": 0, "output": "..." }`
- event `run.command.outputDelta`：`{ "runId": "...", "itemId": "item_0", "delta": "..." }`
- event `run.reasoning`：`{ "runId": "...", "itemId": "item_1", "text": "..." }`
- event `run.reasoning.delta`：`{ "runId": "...", "itemId": "item_1_summary_0", "textDelta": "..." }`
- event `run.completed`：`{ "runId": "...", "exitCode": 0 }`
- event `run.canceled`：`{ "runId": "..." }`
- event `run.failed`：`{ "runId": "...", "exitCode": 1(optional)", "message": "..." }`
- event `run.rejected`：`{ "reason": "..." }`
