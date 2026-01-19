# 任务清单: 聊天输入粘贴图片与快捷发送

目录: `helloagents/plan/202601191324_chat_paste_images_shortcuts/`

---

## 1. WinUI Client（输入增强）
- [√] 1.1 支持在 Chat 输入框粘贴图片：从剪贴板读取 Bitmap/文件/`data:image/...;base64,...`，转换为 data URL 并加入待发送图片预览
- [√] 1.2 Enter 发送、Shift+Enter 换行：确认 `PromptTextBox_KeyDown` 行为与提示文案一致

## 2. 文档更新
- [√] 2.1 更新 `helloagents/wiki/modules/winui-client.md`：补充粘贴图片与快捷键说明
- [√] 2.2 更新 `helloagents/CHANGELOG.md`：记录本次输入体验增强

## 3. 构建验证
- [√] 3.1 `dotnet build`：验证 WinUI Client / Bridge Server 编译通过

## 4. 历史迁移
- [√] 4.1 迁移方案包至 `helloagents/history/2026-01/` 并更新 `helloagents/history/index.md`
