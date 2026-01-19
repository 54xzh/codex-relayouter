# Changelog

本文件记录项目所有重要变更。
格式基于 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.0.0/),
版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 新增
- 新增 `codex-bridge-server`（Bridge Server）骨架：`/api/v1/health`、`/ws`、调用 `codex exec --json` 的流式转发
- 新增 WinUI 端导航与 Chat 页面骨架：连接 `/ws`、发送 `chat.send`、展示 `codex.line`、支持 `run.cancel`
- 新增 WinUI 启动自动拉起 Bridge Server（随机端口 + health 探测）并默认自动连接（无需手动配置）
- 新增会话管理（MVP）：`/api/v1/sessions` 列表/创建，WinUI 会话页，`chat.send(sessionId)` resume 与 `session.created` 事件
- 新增会话体验增强：会话列表标题取首条 user 消息（截断约 50 字）；Chat 页自动加载会话历史（`/api/v1/sessions/{sessionId}/messages`）
- 新增运行追踪事件：Bridge Server 解析 `command_execution` / `reasoning` 并广播 `run.command` / `run.reasoning`，WinUI Chat 页以可展开区块展示“执行的命令”和“思考摘要”
- 新增会话回放 trace：`/api/v1/sessions/{sessionId}/messages` 的 assistant 消息可附带 `trace`（思考摘要/命令），并在 WinUI Chat 页按时间顺序展示
- 新增运行链路（app-server）：Bridge Server 改用 `codex app-server`（JSON-RPC）以支持审批请求与 delta 流式事件；WinUI Chat 页增加 `approvalPolicy/effort` 选择与审批弹窗
- 新增 WinUI：`model` / `model_reasoning_effort` 自动读取 `~/.codex/config.toml`，并在 Chat 页/设置页修改后写回
- 新增 WinUI：Chat 页工作区按钮显示目录名（basename），菜单支持资源管理器打开/重新选择，并展示最近 5 条 cwd（完整路径）以便快速切换
- 新增 WinUI：选择已有会话后自动使用该会话的 `cwd` 作为 workingDirectory
- 新增图片能力：Chat 页支持选择并发送图片（`chat.send(images)`），Bridge Server 转发到 app-server 并在会话回放接口返回 `images`（data URL），WinUI 可解码显示 session 中的 base64 图片
- 新增聊天输入增强：输入框支持粘贴图片；Enter 发送、Shift+Enter 换行
- 新增上下文用量展示：Chat 页右下角按钮显示上下文用量百分比（无数据为 `-%`），点击后以 Flyout 菜单展示后端连接状态 + `/status` 摘要（5h/周限额进度条；重置时间 `MM-dd HH:mm`；缺失项显示“不可用”）
- 新增仓库入口文档 `README.md`：项目简介、快速开始、配置与安全、文档索引（指向 `helloagents/wiki`）

### 变更
- 调整 WinUI 启动窗口体验：增大初始大小并居中；移除最小尺寸限制（允许缩小窗口）
- 优化 WinUI Chat 页执行命令展示：命令块点击展开输出，移除独立的“输出”折叠块
- 优化 WinUI Chat 页 Trace 交互：运行中默认展开“执行过程”，最新思考摘要自动展开；开始输出正文后自动折叠“执行过程”
- 优化 WinUI Chat 页 Trace 样式：命令/输出统一非衬线字体；成功状态默认折叠（不显示 `completed` / `exitCode=0`）
- 优化 WinUI Chat 页 Trace 容器视觉：移除浅灰背景，改用透明背景 + 描边；多行内容时增加 padding 并放宽输出最大高度
- 优化 WinUI Chat 页 Trace 命令条目：视觉与思考摘要一致以避免双层容器；命令多行时最多显示 3 行并截断
- 对齐 WinUI Chat 页 Trace 字体与间距：命令执行与思考摘要标题/内容的字体与上下边距一致
- 优化上下文用量菜单排版：Flyout 加宽并调整行距/字间距，限额改为进度条可视化

### 修复
- 修复 MSIX 调试部署/打包场景下未包含 `bridge-server/` 导致自动启动失败
- 修复 WinUI 启动后 Chat 页未自动连接（需进入设置页才触发）：Chat 页加载时 EnsureStarted 并自动连接
- 修复会话历史回放包含 developer/环境/指令上下文：仅展示 user/assistant 的真实对话，并支持从 `## My request for Codex:` 提取真实用户消息
- 修复点击进入会话后未自动定位到对话底部：会话历史加载完成后自动滚动到最后一条消息
- 修复 WinUI Chat 页执行过程（Trace）增量更新时，处于列表底部不会自动跟随滚动的问题
- 修复 WinUI Chat 页输出正文后 Trace 未自动折叠的问题
- 修复新建会话缺少 `cwd` 导致 `codex exec resume` 报错：创建时强制写入 `cwd`，并在 resume 时自动补写缺失值（使用 workingDirectory）
- 修复会话 `session_meta` 缺少 Codex 必填字段 `cli_version` 导致 resume 失败：创建时写入并在必要时自动补写
- 修复 `codex exec resume` 场景下传递 `--sandbox workspace-write` 报参数解析错误（exitCode=2）：将 `resume` 子命令放到 exec options 之后传参
- 修复部分 Windows 环境下 Bridge Server 无法找到 `codex`（PATH 差异）：自动解析常见安装位置（npm/cargo/WindowsApps），并在设置 workingDirectory 时做存在性校验
- 修复 Codex 退出码非 0 仍显示“完成”且无提示：后端将非 0 视为失败并透出错误信息；Chat 页支持勾选“跳过 Git 检查”以在非 Git 目录运行
- 修复 Windows 下通过 stdin 向 Codex 传入中文/非 ASCII prompt 时可能报错：`input is not valid UTF-8`
- 修复 Chat 页流式输出不刷新：将消息 `Text` 绑定改为 OneWay；并默认开启“跳过 Git 检查”
- 修复 Chat 页显示 Codex `--json` 控制事件（thread/turn/...）：后端解析并仅广播真实助手消息
- 修复 Chat 页“执行的命令/思考摘要”区块不显示：为 `x:Bind` 增加 `Mode=OneWay` 以支持运行时更新，并在运行开始时提示“思考中…”
- 修复 workspace-write 模式无法请求权限：修正 app-server(JSON-RPC) 消息分类逻辑，避免将带 `method` 的 `requestApproval` server request 误判为 response 丢弃，从而确保可正常转发 `approval.requested` 并回传 `approval.respond` 的用户决定
- 修复图片发送失败提示不明确/部分格式不兼容：WinUI 将剪贴板/BMP 图片转为 PNG（避免 `image/bmp` 导致失败），并在 `turn.status=failed` 时透出 `turn.error.message`
