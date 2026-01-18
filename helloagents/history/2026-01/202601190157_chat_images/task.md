# 任务清单: 聊天图片上传与会话回放显示

目录: `helloagents/plan/202601190157_chat_images/`

---

## 1. Bridge Server（协议与转发）
- [√] 1.1 扩展 `codex-bridge-server/Bridge/CodexRunRequest.cs` 支持 images（data URL 列表），并在 `CodexAppServerRunner` 组装 `turn/start.input` 的 `type=image` 条目，验证 why.md#需求-聊天上传图片-场景-用户选择图片并发送
- [√] 1.2 扩展 `codex-bridge-server/Bridge/WebSocketHub.cs` 解析 `chat.send.images` 并在 `chat.message` 中回传 images；允许 prompt 为空但 images 非空，验证 why.md#需求-聊天上传图片-场景-用户选择图片并发送

## 2. Bridge Server（会话回放解析）
- [√] 2.1 扩展 `codex-bridge-server/Bridge/CodexSessionMessage.cs` 增加 `images` 字段，并在 `CodexSessionStore` 从 JSONL 提取 `input_image/output_image`（data URL），验证 why.md#需求-会话回放显示-base64-图片-场景-加载历史消息并渲染图片

## 3. WinUI Client（上传与渲染）
- [√] 3.1 在 `codex-bridge/Pages/ChatPage.xaml(.cs)` 增加图片选择按钮与 PendingImages 管理，发送时携带 images，验证 why.md#需求-聊天上传图片-场景-用户选择图片并发送
- [√] 3.2 扩展 `codex-bridge/ViewModels/ChatMessageViewModel.cs` 支持图片集合，并在 `codex-bridge/Pages/ChatPage.xaml` 的消息模板中渲染图片缩略图，验证 why.md#需求-会话回放显示-base64-图片-场景-加载历史消息并渲染图片
- [√] 3.3 扩展 `codex-bridge/Models/SessionMessage.cs` 与历史加载逻辑，展示 session API 返回的 images，验证 why.md#需求-会话回放显示-base64-图片-场景-加载历史消息并渲染图片

## 4. 安全检查
- [√] 4.1 执行安全检查（输入校验、base64 解码容错、消息大小控制）

## 5. 文档更新
- [√] 5.1 更新 `helloagents/wiki/modules/protocol.md`、`helloagents/wiki/modules/bridge-server.md`、`helloagents/wiki/modules/winui-client.md` 反映 images 支持

## 6. 测试
- [√] 6.1 `dotnet build` 验证 client/server 编译通过
