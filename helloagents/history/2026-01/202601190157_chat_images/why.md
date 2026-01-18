# 变更提案: 聊天图片上传与会话回放显示

## 需求背景
当前聊天仅支持纯文本输入/展示；当会话中包含 base64 图片（如 `input_image.image_url` 的 data URL）时，前端无法解码显示，历史回放也会丢失图片信息。

## 变更内容
1. 在聊天协议中支持携带图片（data URL），并在服务端转发给 Codex app-server。
2. 在会话历史 API 中解析并返回图片字段，避免图片在回放时丢失。
3. WinUI 客户端支持选择图片、发送到聊天，并在消息列表中渲染图片缩略图。

## 影响范围
- **模块:** Bridge Server / WinUI Client / Protocol
- **文件:** `codex-bridge-server/Bridge/*`、`codex-bridge/Pages/ChatPage.*`、`codex-bridge/ViewModels/*`、`codex-bridge/Models/*`
- **API:** `GET /api/v1/sessions/{sessionId}/messages` 返回结构扩展（新增 images）
- **数据:** 读取 `~/.codex/sessions/*.jsonl` 时解析 `input_image`/`output_image`（data URL）

## 核心场景

### 需求: 聊天上传图片
**模块:** WinUI Client / Bridge Server / Protocol

#### 场景: 用户选择图片并发送
- 用户在 Chat 页点击“图片”按钮选择本地图片（支持常见格式）。
- 发送时将图片以 data URL（`data:image/...;base64,...`）随同 prompt 一起发送至服务端，并写入 Codex 会话。
- UI 在用户消息中展示图片缩略图。

### 需求: 会话回放显示 base64 图片
**模块:** Bridge Server / WinUI Client

#### 场景: 加载历史消息并渲染图片
- 客户端加载 `/api/v1/sessions/{id}/messages`。
- 服务端解析消息 `content` 内的 `input_image/image_url` 并返回 `images`。
- 客户端解码 data URL 并在消息中渲染图片。

## 风险评估
- **风险:** base64 图片可能较大，解码与渲染造成内存/卡顿。
- **缓解:** 解析与渲染做容错（无效 data URL 跳过），UI 以缩略图展示并限制最大显示尺寸；服务端对单条消息图片数量做上限保护。

