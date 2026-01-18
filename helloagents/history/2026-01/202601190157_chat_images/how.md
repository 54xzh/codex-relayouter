# 技术设计: 聊天图片上传与会话回放显示

## 技术方案

### 核心技术
- **WinUI 3:** `FileOpenPicker` 选择图片、`BitmapImage.SetSourceAsync` 解码渲染
- **Bridge Server:** 扩展 WS `chat.send/chat.message` 数据结构；解析 `~/.codex/sessions` JSONL 的 `input_image/output_image`
- **Codex app-server:** `turn/start` 的 `input` 支持 `type=image` 且 `url` 为 data URL

### 实现要点
1. **协议扩展**
   - `chat.send` 新增 `images: ["data:image/...;base64,..."]`，允许 prompt 为空但 images 非空。
   - `chat.message` 新增 `images` 字段（data URL 字符串数组）用于 UI 展示（含用户消息与历史回放）。

2. **写入 Codex app-server**
   - `CodexRunRequest` 扩展 `Images`（data URL 列表）。
   - `turn/start` 的 `input` 由 `[{type:text,text:...}]` 扩展为 `[{type:text,...}, {type:image,url:dataUrl}, ...]`。

3. **会话回放解析**
   - 从 `response_item.payload.type=message` 的 `payload.content` 中提取：
     - `text`（过滤 `<image>`/`</image>` 标记）
     - `image_url`（string，形如 `data:image/png;base64,...`）→ `images[]`
   - 兼容 `input_image` 与可能的 `output_image` 变体（均按 `image_url` 字段处理）。

4. **WinUI 渲染**
   - `ChatMessageViewModel` 增加 `Images` 集合。
   - 将 data URL 解码为 `BitmapImage` 并绑定到 `Image.Source`。
   - 输入区维护 `PendingImages`，发送后清空。

## 安全与性能
- **输入校验:** 仅处理 `data:image/*;base64,` data URL；base64 解码失败则忽略该图片。
- **资源控制:** 限制单条消息最大图片数量（例如 4 张）；缩略图显示限制宽高，避免大图撑爆布局。
- **敏感信息:** 不落盘持久化图片；仅在内存中解码用于 UI 显示（会话文件由 Codex CLI 管理）。

## 测试与部署
- `dotnet build`（client/server）确保编译通过。
- 手动验证：
  - 选择图片发送后，用户消息出现缩略图。
  - 重新进入会话/切换会话后，历史消息仍能显示图片。
