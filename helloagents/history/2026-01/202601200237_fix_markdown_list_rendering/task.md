# 任务清单：修复 Markdown 无序列表渲染

> 类型: 修复  
> 模式: 轻量迭代  
> 创建时间: 2026-01-20 02:37  
> 目标模块: WinUI Client  

- [√] 复现并确认：带缩进的无序列表（如 `  - item`）无法被正确渲染为列表
- [√] 实现：对聊天消息的 Markdown 文本做轻量规范化（列表缩进/必要空行），避免影响代码块内容
- [√] 验证：`dotnet build`（必要时包含 `dotnet test`）
- [√] 文档：同步更新 WinUI Client 模块说明与 `helloagents/CHANGELOG.md`
