# Bridge Server

## 目的
作为本机状态中心与后端服务：统一会话/事件流，驱动本机 `codex` CLI，并对外提供远程接口。

## 模块概述
- **职责:** 会话与运行管理、工作区管理、Codex 进程驱动（app-server JSON-RPC + JSONL 回放解析）、事件广播、鉴权与远程开关
- **状态:** 开发中
- **最后更新:** 2026-01-19

## 规范

### 需求: 多端同步
**模块:** Bridge Server

#### 场景: 两端同时在线
WinUI/Android 同时连接时，服务端广播同一会话的消息与运行事件，保证最终一致显示。

### 需求: 继承 Codex CLI 权限
**模块:** Bridge Server

#### 场景: 本机同用户执行
所有对文件/命令的实际权限由运行 Bridge Server 的 Windows 用户与 `codex` CLI 的 sandbox 配置决定；远程访问默认关闭。

#### 场景: 作为 WinUI Sidecar 自动启动
Bridge Server 可随 WinUI 构建产物一同分发到 `bridge-server/` 目录，由 WinUI 自动启动并通过 `/api/v1/health` 探测就绪后再建立 WS 连接。

#### 场景: MSIX 调试部署/发布
Bridge Server 需要被包含在应用包的 `bridge-server/` 子目录中；WinUI 构建时会将后端输出同步到 MSIX 布局目录以保证部署后可执行。

## API接口
### [GET] /api/v1/health
**描述:** 健康检查

### [GET] /status
**描述:** 上下文用量摘要（文本；包含 5h限额/周限额/上下文用量；缺失项显示“不可用”；限额重置时间格式 `MM-dd HH:mm`）

### [GET] /api/v1/sessions
**描述:** 列出会话（读取 `~/.codex/sessions` 的 `session_meta` 元数据，并提取“首条有效 user 消息”作为标题；会跳过注入上下文）

### [POST] /api/v1/sessions
**描述:** 创建会话（写入最小 `session_meta` JSONL 文件；`cwd` 必填且需存在，并写入 Codex 需要的 `cli_version`）

### [GET] /api/v1/sessions/{sessionId}/messages
**描述:** 获取会话历史消息（解析 JSONL 的 message 记录，用于前端回放；过滤 developer/system/环境上下文，仅保留 user/assistant 的真实对话；并可附带 trace 回放；当 message.content 中包含 `input_image/output_image` 时会返回 `images`（data URL）供前端解码显示）
补充：兼容 `event_msg.agent_message` 作为 assistant 正文兜底；当会话末尾仅有 trace 且无正文输出时，会刷出占位 assistant 消息（文本为 `（未输出正文）`）以避免历史丢失。

### [GET] /api/v1/sessions/{sessionId}/plan
**描述:** 获取会话最新计划（turn plan）。数据来自 Bridge Server 内存缓存（由 `turn/plan/updated` 推送更新），用于前端进入会话/重连时回填；无缓存时返回 404。

### [WS] /ws
**描述:** 命令与事件通道（聊天发送/流式输出/多端同步）

**已实现（MVP 骨架）:**
- command: `chat.send`（支持 `prompt`/`images`/`sessionId(resume)`/`workingDirectory`/`model`/`sandbox`/`approvalPolicy`/`effort`/`skipGitRepoCheck`）
- command: `run.cancel`
- command: `approval.respond`
- event: `bridge.connected` / `session.created` / `chat.message` / `run.started` / `run.plan.updated` / `run.command` / `run.reasoning` / `run.completed` / `run.canceled` / `run.failed` / `run.rejected`
- event: `approval.requested` / `chat.message.delta` / `run.command.outputDelta` / `run.reasoning.delta`
- event: `device.pairing.requested` / `device.presence.updated`（连接/配对）

**说明:**
- 运行链路改为 `codex app-server`（JSON-RPC over stdio）：支持审批 request/response 与 delta 流式事件
- `commandExecution` / `reasoning` / `agentMessage` 会被映射为 `run.command` / `run.reasoning` / `chat.message`，并额外广播 delta 事件用于前端实时渲染
- 会话回放时，服务端会从 `~/.codex/sessions` 中解析 `agent_reasoning` 与工具调用（如 `shell_command`），并以 `trace` 字段附加到对应的 assistant 消息；同时解析 `input_image/output_image` 并返回 `images`（data URL）

## 配置
- `Bridge:Security:RemoteEnabled`：是否开启远程访问（默认 false，仅允许回环）
- `Bridge:Security:BearerToken`：全局令牌（可选，仅用于兼容旧客户端/调试；推荐使用“设备令牌”）
- `Bridge:Codex:Executable`：codex 可执行文件名/路径（默认 `codex`）
  - Windows 下为兼容 MSIX/sidecar 环境的 PATH 差异，Bridge Server 会尝试在常见目录中自动定位（如 `%USERPROFILE%\\AppData\\Roaming\\npm\\codex.cmd`、`%USERPROFILE%\\.cargo\\bin\\codex.exe`、WindowsApps 与 PATH）
  - 如仍失败，可将该值配置为 `codex.cmd/codex.exe` 的绝对路径
- `Bridge:Codex:SkipGitRepoCheck`：是否跳过仓库检查（默认 false）
- 为避免 Windows 默认代码页导致 `stdin not valid UTF-8`，Bridge Server 与 `codex` 子进程交互统一使用 UTF-8（stdin/stdout/stderr）
- 为兼容 `codex exec resume` 的会话解析，Bridge Server 会在创建会话时写入 `session_meta.payload.cli_version`，并在必要时自动补写缺失字段（含清理 UTF-8 BOM）

## 连接与配对（局域网）
- 远程访问默认关闭；当 `Bridge:Security:RemoteEnabled=true` 且监听地址允许局域网访问时，远程设备可通过“配对邀请码 + 本机确认”完成授权。
- 设备令牌（deviceToken）为每台设备独立签发，仅首次配对下发；服务端仅持久化哈希，并支持逐设备撤销。
- 已配对设备存储（默认）：`%LOCALAPPDATA%\\codex-bridge\\paired-devices.json`

## 变更历史
- [202601172220_codex_gui_shell](../../history/2026-01/202601172220_codex_gui_shell/) - Bridge Server 骨架（health/ws/codex runner）
- [202601172341_winui_ws_chat](../../history/2026-01/202601172341_winui_ws_chat/) - 补齐 `chat.message` 与 `model/sandbox` 参数支持
- [202601180007_autostart_backend](../../history/2026-01/202601180007_autostart_backend/) - WinUI sidecar 部署与自动拉起（免手动启动）
- [202601180040_fix_sidecar_packaging](../../history/2026-01/202601180040_fix_sidecar_packaging/) - 修复：MSIX 部署/打包包含后端 Sidecar（避免自动连接失败）
- [202601180102_session_management](../../history/2026-01/202601180102_session_management/) - 会话管理：`/api/v1/sessions` + `chat.send(sessionId)` + `session.created`
- [202601180141_session_history_title](../../history/2026-01/202601180141_session_history_title/) - 会话体验：历史消息读取接口 + 会话标题（首条 user 消息截断）
- [202601180203_session_message_filter](../../history/2026-01/202601180203_session_message_filter/) - 会话体验：历史/标题过滤注入上下文，仅显示真实对话
- [202601180234_fix_codex_path](../../history/2026-01/202601180234_fix_codex_path/) - 修复：Windows PATH 差异导致找不到 codex（自动定位 npm/cargo 等安装路径）
- [202601180258_fix_run_no_reply](../../history/2026-01/202601180258_fix_run_no_reply/) - 修复：退出码非 0 不再误报完成；支持跳过 Git 检查在非 Git 目录运行
- [202601180330_fix_utf8_stdin](../../history/2026-01/202601180330_fix_utf8_stdin/) - 修复：Windows 下 stdin/stdout/stderr 统一 UTF-8，避免 prompt 编码错误
- [202601180440_fix_session_cwd](../../history/2026-01/202601180440_fix_session_cwd/) - 修复：新建会话补齐 cwd，避免 resume 报错；兼容旧会话自动补写缺失 cwd
- [202601180610_fix_cli_version_required](../../history/2026-01/202601180610_fix_cli_version_required/) - 修复：会话补齐 `cli_version`（Codex resume 必填）
- [202601180700_filter_codex_json_events](../../history/2026-01/202601180700_filter_codex_json_events/) - 修复：过滤 codex `--json` 控制事件，前端仅展示真实助手消息
- [202601181348_trace_thinking](../../history/2026-01/202601181348_trace_thinking/) - 运行追踪：映射并广播 `run.command` / `run.reasoning`
- [202601181551_trace_timeline](../../history/2026-01/202601181551_trace_timeline/) - Trace 时间线：会话回放解析 `agent_reasoning` 与工具调用并按时间序展示
- [202601220035_connections_pairing](../../history/2026-01/202601220035_connections_pairing/) - 连接：设备配对/设备令牌/撤销与在线状态
- [202601181735_app_server_approvals](../../history/2026-01/202601181735_app_server_approvals/) - 运行链路：切换 `codex app-server` 以支持审批请求与 delta 流式事件
- [202601190157_chat_images](../../history/2026-01/202601190157_chat_images/) - 图片输入与回放：`chat.send(images)` + `/sessions/{id}/messages.images`
- [202601191959_chat_status_button](../../history/2026-01/202601191959_chat_status_button/) - 状态查询：新增 `GET /status`；WinUI 提供“状态”按钮弹窗展示
- [202601192021_status_command_output](../../history/2026-01/202601192021_status_command_output/) - 状态查询：/status 指令输出增强（含限额等）
- [202601192057_status_popup_layout](../../history/2026-01/202601192057_status_popup_layout/) - 状态查询：/status 移除 AGENTS 行，状态弹窗排版优化
- [202601192202_context_usage_status](../../history/2026-01/202601192202_context_usage_status/) - 状态查询：/status 精简为限额与上下文用量
- [202601192243_context_usage_flyout](../../history/2026-01/202601192243_context_usage_flyout/) - 状态查询：限额重置时间格式调整，WinUI 侧改为 Flyout + 进度条
- [202601200021_fix_incomplete_reply_history](../../history/2026-01/202601200021_fix_incomplete_reply_history/) - 修复：无正文/中断回复的会话回放不再丢失（agent_message 兜底 + trace 末尾刷出占位 assistant）
- [202601211742_turn_plan_todo](../../history/2026-01/202601211742_turn_plan_todo/) - 待办计划：`run.plan.updated` + `GET /api/v1/sessions/{sessionId}/plan`

## 依赖
- Codex CLI
- Protocol
