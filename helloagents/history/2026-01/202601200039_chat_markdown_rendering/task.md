# 任务清单: Chat 支持 Markdown 渲染

目录: `helloagents/plan/202601200039_chat_markdown_rendering/`

---

## 1. WinUI Chat 消息渲染
- [√] 1.1 引入 Markdown 渲染控件依赖（CommunityToolkit MarkdownTextBlock）
- [√] 1.2 ChatPage：assistant 消息使用 MarkdownTextBlock 渲染（流式阶段保持纯文本，完成后切换）
- [√] 1.3 链接点击：仅允许 http/https 外链打开

## 2. 文档同步
- [√] 2.1 更新 `helloagents/wiki/modules/winui-client.md`（记录 Markdown 渲染与依赖）
- [√] 2.2 更新 `helloagents/CHANGELOG.md`

## 3. 回归验证
- [√] 3.1 构建验证：`dotnet build codex-bridge/codex-bridge.csproj -p:Platform=x64`
