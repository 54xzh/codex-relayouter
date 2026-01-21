# WinUI Client

## 目的
提供 Windows 端 GUI，展示会话与流式输出，并提供工作区、diff、设置等交互。

## 模块概述
- **职责:** UI 渲染、用户输入、与 Bridge Server 通信、diff 展示与应用控制
- **状态:** 开发中
- **最后更新:** 2026-01-21

## 规范

### XAML 约定
- 字体：容器（如 `StackPanel`）不支持 `FontFamily`，也不要在容器上使用 `TextElement.FontFamily`；需要统一字体时请在文本控件（如 `TextBlock`）上设置 `FontFamily`，或在 `Resources` 中对 `TextBlock`/`Control` 定义 Style。
- Flyout 菜单：`MenuFlyoutItem` 的字体通常不会从触发按钮内容继承；需要统一中文字体时请在 `MenuFlyoutItem` 上显式设置 `FontFamily`（或通过资源字典/Style 统一配置）。
- 简体字形：为避免系统为繁体语言/区域时回退到繁体字形字体，可在根容器设置 `Language="zh-Hans"`，让字体回退始终选择简体字形。

### 窗口约定
- 启动时窗口默认在当前屏幕工作区居中。
- 启动时窗口会按工作区计算初始大小并居中，但不限制最小尺寸（允许用户自由缩小窗口）。

### 需求: GUI 核心交互
**模块:** WinUI Client

#### 场景: 聊天与流式输出
以增量事件实时渲染回复，支持取消与重试。
Chat 页顶部提供“待办/计划”面板：实时展示服务端推送的 `run.plan.updated`（pending/inProgress/completed），并在进入会话时通过 `GET /api/v1/sessions/{sessionId}/plan` 回填最新计划（无缓存则隐藏）。
Chat 页支持图片输入：可选择本地图片并随消息发送；消息列表会展示图片缩略图，且在会话历史回放时可解码显示 session 中的 base64 图片（data URL）。
Chat 输入框支持粘贴图片：当剪贴板包含图片（Bitmap/文件/`data:image/...;base64,...`）时，粘贴会自动将图片加入待发送预览列表。
兼容性：为避免 `image/bmp` 导致 Codex 拒绝，剪贴板图片与 BMP 文件会自动转为 PNG 再发送。
快捷键：Enter 发送；Shift+Enter 换行。
Chat 回复正文支持 Markdown 渲染（使用 `CommunityToolkit.WinUI.UI.Controls.Markdown` 的 `MarkdownTextBlock`；支持代码块/列表/链接）；代码块使用浅色背景 + 黑字 + 描边 + 圆角以提升对比度；行内代码使用浅底圆角（无轮廓描边），其中可打开的文件路径行内代码使用浅蓝背景 + 蓝色文字（无轮廓描边）；流式阶段先以纯文本渲染，完成后切换为 Markdown；链接点击仅允许打开 http/https 外链。
实现细节：为解决 `InlineUIContainer` 的基线对齐问题，行内代码的 `Border` 会做轻量下移（约 4px），以尽量与行内普通文字对齐；在无序/有序列表中渲染时会临时移除 `ParagraphMargin.Top`，并在“列表项以行内代码开头”的场景同步下移 bullet，避免列表项出现垂直“漂移”。
已知限制：当行内代码位于 Markdown 链接文本内部（例如 [`SomeCode`](https://example.com)）时会回退为普通文本渲染（Toolkit 限制），因此不会显示行内代码背景/圆角，也不会启用“点击打开文件”。
为便于在对话中快速跳转到工程文件：当 Markdown 行内代码内容可解析为文件路径（支持绝对路径、相对路径、`a/`/`b/` diff 前缀；支持常见括号/引号包裹与尾随标点清理；相对路径优先基于当前工作区 `cwd`/会话 `cwd`，必要时回退到 Git 仓库根目录）且对应文件/目录存在时，UI 会仅显示文件名（basename），并支持点击打开该文件（目录则在资源管理器中打开，悬停显示手型光标）；若行内代码仅为文件名（如 `ChatPage.xaml.cs`），在工作区/仓库内唯一匹配时也可点击打开；完整路径会保留在悬浮提示中；点击打开行为会对短时间内的重复触发做去重（避免资源管理器打开两次）；当渲染时无法解析但内容“看起来像路径”时，仍会显示为文件名样式，点击时会再次尝试解析并打开，但会排除明显的 HTTP 请求行（例如 `GET /api/...`、`POST /...`）。
为提升可读性与快速识别文件类型：当行内代码被识别为文件路径时，会在文件名前增加类型图标，并区分 Markdown（`.md/.markdown/.mdx`）、代码文件（多语言扩展名集合）、文件夹、其他/通用文件。
为提升兼容性，客户端会对 Markdown 做轻量规范化：当检测到列表行存在 1-3 个前导空格（例如 `  - item`）时会去缩进，并在“标签行(:/：) 后紧跟列表”时自动补空行；当检测到只包含 `─` 的分隔线（如 `────`）且后续紧跟文本行时，会自动补一个空行避免被合并；并将普通段落的单换行按“硬换行”处理（尽量做到 `\n` 就换行，避开 fenced code block 与缩进代码块）；另外会将行内出现的 fence marker（```/~~~）转义为字面量，避免 `MarkdownTextBlock` 在同一段落里出现多个 ``` 时发生贪婪匹配导致整段误判为代码。
Chat 页右下角提供“上下文用量”入口：以文本标签形式显示上下文用量百分比（无数据为 `-%`，右侧带圆形进度条可视化）；点击后以 Flyout 菜单展示后端连接状态 + `/status` 摘要（5h/周限额以进度条可视化，重置时间格式 `MM-dd HH:mm`；限额不可用时自动隐藏，并随内容自动收缩卡片大小；其他缺失项显示“不可用”）。
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

#### 场景: 连接页（局域网配对与设备管理）
在侧边栏底部提供“连接”入口，用于管理局域网设备：
- 可开启/关闭“允许局域网连接”（开启/关闭会重启后端以切换监听地址，并自动断连/重连 WS）
- 生成配对二维码与 `pairingCode`（二维码内容包含 baseUrl + pairingCode；设备端扫码/输入后仍需本机确认）
- 展示已配对设备列表（在线/离线、最近活动），并支持逐设备撤销（撤销后立即断开并失效）
- 当远程设备发起配对请求时，会弹出确认对话框（允许/拒绝）
注意：页面初始化期间会触发控件的 Changed 事件，需要在 UI 初始化完成后再写回 ConnectionService，避免空引用导致崩溃。
补充：设置页修改 `Model` / `Effort` 会与 `~/.codex/config.toml` 同步（写回为 best-effort）。

#### 场景: 会话管理
支持列出/创建/选择会话：创建会话需填写 `cwd`（工作区）；列表标题优先使用“首条 user 消息”截断（已过滤环境/指令上下文；若提取失败则回退显示 `sessionId`）；点击会话进入聊天页后自动加载该会话的历史消息（仅显示 user/assistant 的真实对话），并在聊天发送时绑定 `sessionId` 以便 resume。
会话历史回放会保留无正文但包含 Trace/图片的消息，避免重启后记录丢失。
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
- [202601191959_chat_status_button](../../history/2026-01/202601191959_chat_status_button/) - WinUI：Chat 页右下角“状态”按钮，弹窗展示 `/status`
- [202601192021_status_command_output](../../history/2026-01/202601192021_status_command_output/) - WinUI：状态弹窗展示“/status 指令输出”（含限额等字段）
- [202601192057_status_popup_layout](../../history/2026-01/202601192057_status_popup_layout/) - WinUI：状态弹窗加宽并调整行距/字间距；/status 移除 AGENTS 行
- [202601192202_context_usage_status](../../history/2026-01/202601192202_context_usage_status/) - WinUI：按钮显示上下文用量百分比（无数据为 `-%`），弹窗仅展示连接状态/5h/周/上下文用量
- [202601192243_context_usage_flyout](../../history/2026-01/202601192243_context_usage_flyout/) - WinUI：上下文用量菜单 Flyout（限额进度条/重置时间）
- [202601192345_remove_window_min_size](../../history/2026-01/202601192345_remove_window_min_size/) - WinUI：移除主窗口最小尺寸限制（允许缩小）
- [202601200021_fix_incomplete_reply_history](../../history/2026-01/202601200021_fix_incomplete_reply_history/) - 修复：无正文/中断回复的会话回放不再丢失（trace-only 消息保留）
- [202601200039_chat_markdown_rendering](../../history/2026-01/202601200039_chat_markdown_rendering/) - WinUI：Chat 回复支持 Markdown 渲染
- [202601200110_markdown_codeblock_style](../../history/2026-01/202601200110_markdown_codeblock_style/) - WinUI：Markdown 代码块样式优化（浅背景/黑字/圆角）
- [202601200237_fix_markdown_list_rendering](../../history/2026-01/202601200237_fix_markdown_list_rendering/) - 修复：Markdown 无序列表渲染兼容（缩进列表/标签行后列表）
- [202601201835_inline_code_open_file](../../history/2026-01/202601201835_inline_code_open_file/) - WinUI：Markdown 行内代码文件路径显示文件名并支持点击打开
- [202601202120_markdown_code_ui](../../history/2026-01/202601202120_markdown_code_ui/) - WinUI：Markdown 代码样式与文件交互（浅蓝文件高亮/圆角、无描边、对齐优化）
- [202601202330_markdown_inline_code_baseline_fix](../../history/2026-01/202601202330_markdown_inline_code_baseline_fix/) - WinUI：Markdown 行内代码对齐与可点击稳定性修复
- [202601202354_markdown_inline_code_list_baseline_tune](../../history/2026-01/202601202354_markdown_inline_code_list_baseline_tune/) - WinUI：Markdown 行内代码/列表对齐微调
- [202601210008_markdown_inline_code_list_offset_fix](../../history/2026-01/202601210008_markdown_inline_code_list_offset_fix/) - 修复：Markdown 无序列表行内代码垂直对齐偏移
- [202601211848_fix_inline_code_path_click](../../history/2026-01/202601211848_fix_inline_code_path_click/) - 修复：行内代码路径误判与重复打开
- [202601211742_turn_plan_todo](../../history/2026-01/202601211742_turn_plan_todo/) - WinUI：Chat 页新增“待办/计划”面板（实时 plan + 会话回填）
- [202601220035_connections_pairing](../../history/2026-01/202601220035_connections_pairing/) - WinUI：连接页（局域网配对/设备列表/撤销/确认弹窗）
