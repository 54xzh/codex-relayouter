# WinUI Client

## 目的
提供 Windows 端 GUI，展示会话与流式输出，并提供工作区、diff、设置等交互。

## 模块概述
- **职责:** UI 渲染、用户输入、与 Bridge Server 通信、diff 展示与应用控制
- **状态:** 开发中
- **最后更新:** 2026-01-19

## 规范

### XAML 约定
- 字体：容器（如 `StackPanel`）不支持 `FontFamily`，也不要在容器上使用 `TextElement.FontFamily`；需要统一字体时请在文本控件（如 `TextBlock`）上设置 `FontFamily`，或在 `Resources` 中对 `TextBlock`/`Control` 定义 Style。
- Flyout 菜单：`MenuFlyoutItem` 的字体通常不会从触发按钮内容继承；需要统一中文字体时请在 `MenuFlyoutItem` 上显式设置 `FontFamily`（或通过资源字典/Style 统一配置）。

### 窗口约定
- 启动时窗口默认在当前屏幕工作区居中。
- 启动时窗口初始大小会同步作为最小尺寸，避免用户缩到过小导致 UI 元素被遮挡。

### 需求: GUI 核心交互
**模块:** WinUI Client

#### 场景: 聊天与流式输出
以增量事件实时渲染回复，支持取消与重试。
Chat 页支持图片输入：可选择本地图片并随消息发送；消息列表会展示图片缩略图，且在会话历史回放时可解码显示 session 中的 base64 图片（data URL）。
Chat 输入框支持粘贴图片：当剪贴板包含图片（Bitmap/文件/`data:image/...;base64,...`）时，粘贴会自动将图片加入待发送预览列表。
兼容性：为避免 `image/bmp` 导致 Codex 拒绝，剪贴板图片与 BMP 文件会自动转为 PNG 再发送。
快捷键：Enter 发送；Shift+Enter 换行。
Chat 页支持配置 `model`、`approvalPolicy`（权限模式）与 `effort`（思考深度），并在需要时弹出审批对话框（允许/拒绝/取消任务）。
其中 `model` 与 `effort` 会自动从 `~/.codex/config.toml` 读取（键：`model`、`model_reasoning_effort`），并在 Chat 页/设置页修改后写回（debounce）。
Chat 页工作区按钮的描述文本显示 `cwd` 的目录名（basename）；点击后菜单提供“在资源管理器中打开”、“重新选择（FolderPicker）”，并展示最近使用的 5 条 `cwd`（完整路径）以便快速切换。
当工作区不在 Git 仓库目录内导致旧链路拒绝运行时，可在 Chat 页启用“跳过 Git 检查”（默认开启，保留兼容）。
Chat 页可展示运行追踪信息（Trace）：包括思考摘要与执行命令，并按时间顺序展示；运行中默认展开 Trace，且最新一条思考摘要会自动展开；当 Trace/输出增量更新且列表处于底部时，会自动保持滚动到最底部；开始输出回复（正文）后自动折叠 Trace；最终回答显示在 trace 之后（可展开查看）；执行命令条目点击命令块即可展开查看输出（不再单独显示“输出”二级折叠）；Trace 内命令/输出统一使用非衬线字体，且成功状态默认折叠（不显示 `completed` / `exitCode=0`）；命令条目视觉与思考摘要保持一致（标题字体与上下内边距一致，避免双层效果），命令文本最多显示三行，超出截断为省略号；多行内容使用更大的上下留白与固定行高提升可读性。

#### 场景: 一键启动（自动拉起后端）
启动 WinUI 时自动拉起本机 Bridge Server（sidecar），随机端口并进行健康检查；Chat 页默认自动连接，无需用户手动填写 WS 地址。

#### 场景: 打包/部署后仍可自动拉起
在 MSIX 调试部署与发布场景下，Bridge Server 会随应用一起被部署到安装目录的 `bridge-server/` 子目录，确保运行时仍可自动启动。

#### 场景: 连接 Bridge Server（WS）
默认自动连接 `ws://127.0.0.1:<port>/ws`（由 WinUI 自动拉起后端并分配端口）；也允许手动输入 WS 地址以连接外部/远程 Bridge Server。

#### 场景: 设置页（连接与高级选项）
设置页用于编辑连接与运行参数（WS 地址、Token、WorkingDirectory、Sandbox/ApprovalPolicy/Effort 等）。
注意：页面初始化期间会触发控件的 Changed 事件，需要在 UI 初始化完成后再写回 ConnectionService，避免空引用导致崩溃。
补充：设置页修改 `Model` / `Effort` 会与 `~/.codex/config.toml` 同步（写回为 best-effort）。

#### 场景: 会话管理
支持列出/创建/选择会话：创建会话需填写 `cwd`（工作区）；列表标题优先使用“首条 user 消息”截断（已过滤环境/指令上下文；若提取失败则回退显示 `sessionId`）；点击会话进入聊天页后自动加载该会话的历史消息（仅显示 user/assistant 的真实对话），并在聊天发送时绑定 `sessionId` 以便 resume。
点击会话进入聊天页后，会自动定位到消息列表最底部，确保直接看到最新一条消息。
选择已有会话后，会自动使用该会话的 `cwd` 作为 Chat 页 `workingDirectory`（同步到 `ConnectionService.WorkingDirectory`），避免手动重复选择工作目录。

## 依赖
- Bridge Server
- Protocol

## 变更历史
- [202601172341_winui_ws_chat](../../history/2026-01/202601172341_winui_ws_chat/) - WinUI 导航 + Chat 页（WS 连接/发送/流式渲染/取消）
- [202601180007_autostart_backend](../../history/2026-01/202601180007_autostart_backend/) - WinUI：自动拉起后端并默认自动连接（免手动配置）
- [202601180040_fix_sidecar_packaging](../../history/2026-01/202601180040_fix_sidecar_packaging/) - 修复：MSIX 部署/打包包含后端 Sidecar（避免自动连接失败）
- [202601180102_session_management](../../history/2026-01/202601180102_session_management/) - WinUI：会话页（列表/创建/选择）+ Chat 绑定 sessionId（resume）
- [202601180141_session_history_title](../../history/2026-01/202601180141_session_history_title/) - WinUI：会话列表标题（首条 user 消息截断）+ Chat 自动加载会话历史
- [202601180203_session_message_filter](../../history/2026-01/202601180203_session_message_filter/) - WinUI：会话回放仅显示真实对话（过滤注入上下文）
- [202601180258_fix_run_no_reply](../../history/2026-01/202601180258_fix_run_no_reply/) - WinUI：失败可见（含 exitCode）+ 跳过 Git 检查开关
- [202601180330_fix_utf8_stdin](../../history/2026-01/202601180330_fix_utf8_stdin/) - WinUI：会话列表 title 为空时回退显示 sessionId
- [202601180440_fix_session_cwd](../../history/2026-01/202601180440_fix_session_cwd/) - WinUI：新建会话要求填写 cwd（工作区）
- [202601180520_default_skip_git_stream_fix](../../history/2026-01/202601180520_default_skip_git_stream_fix/) - WinUI：Chat 页默认跳过 Git 检查；修复流式消息不刷新
- [202601180700_filter_codex_json_events](../../history/2026-01/202601180700_filter_codex_json_events/) - WinUI：assistant 消息不再显示 codex JSON 事件（由后端映射），并更新 run 占位消息
- [202601181348_trace_thinking](../../history/2026-01/202601181348_trace_thinking/) - WinUI：Chat 页展示“执行的命令/思考摘要”（可展开）
- [202601181551_trace_timeline](../../history/2026-01/202601181551_trace_timeline/) - WinUI：Trace 时间线（思考/命令/回答按时间顺序）
- [202601181735_app_server_approvals](../../history/2026-01/202601181735_app_server_approvals/) - WinUI：审批弹窗 + delta 流式渲染（assistant/命令/思考摘要）
- [202601190157_chat_images](../../history/2026-01/202601190157_chat_images/) - WinUI：Chat 页图片选择/预览/发送 + 会话回放图片解码显示
- [202601190247_window_init_size_center](../../history/2026-01/202601190247_window_init_size_center/) - WinUI：增大启动窗口初始大小，并将初始大小作为最小尺寸；启动时屏幕居中
- [202601191127_exec_command_output_expander](../../history/2026-01/202601191127_exec_command_output_expander/) - WinUI：执行命令输出改为命令块可展开（移除“输出”折叠）
- [202601191135_trace_style_sans_and_status_fold](../../history/2026-01/202601191135_trace_style_sans_and_status_fold/) - WinUI：Trace 命令/输出统一非衬线字体；成功状态标签默认折叠
- [202601191159_trace_no_gray_more_height](../../history/2026-01/202601191159_trace_no_gray_more_height/) - WinUI：Trace 容器去浅灰背景（透明 + 描边）；多行内容更舒适高度
- [202601191206_trace_command_no_double_and_3lines](../../history/2026-01/202601191206_trace_command_no_double_and_3lines/) - WinUI：命令条目与思考摘要一致以避免双层容器；命令最多三行截断
- [202601191212_trace_command_reasoning_visual_align](../../history/2026-01/202601191212_trace_command_reasoning_visual_align/) - WinUI：命令执行与思考摘要条目字体与上下边距对齐
- [202601191231_trace_auto_expand](../../history/2026-01/202601191231_trace_auto_expand/) - WinUI：Trace 执行中默认展开，最新思考摘要自动展开；完成后自动折叠
- [202601191252_trace_autoscroll_bottom](../../history/2026-01/202601191252_trace_autoscroll_bottom/) - WinUI：Trace/输出增量更新时，列表在底部自动跟随滚动
- [202601191305_trace_auto_collapse_fix](../../history/2026-01/202601191305_trace_auto_collapse_fix/) - WinUI：修复输出正文后 Trace 未自动折叠
- [202601191324_chat_paste_images_shortcuts](../../history/2026-01/202601191324_chat_paste_images_shortcuts/) - WinUI：Chat 输入框粘贴图片 + Enter 发送/Shift+Enter 换行
- [202601191827_chat_image_failure_fix](../../history/2026-01/202601191827_chat_image_failure_fix/) - 修复：BMP/剪贴板图片发送导致 codex 执行失败 + 失败原因透出
