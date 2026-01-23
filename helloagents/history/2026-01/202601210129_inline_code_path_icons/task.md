# 任务清单: 行内代码文件路径图标（轻量迭代）

目录: `helloagents/history/2026-01/202601210129_inline_code_path_icons/`

---

## 1. 行内文件路径图标
- [√] 1.1 在 `codex-relayouter/Markdown/FilePathMarkdownRenderer.cs` 中为文件路径行内代码添加前置图标，并区分 Markdown/代码文件/文件夹/通用

## 2. 文档同步
- [√] 2.1 更新 `helloagents/wiki/modules/winui-client.md`（补充行内文件路径图标说明）
- [√] 2.2 更新 `helloagents/CHANGELOG.md`（新增项记录）

## 3. 质量验证
- [√] 3.1 执行 `dotnet build codex-relayouter.slnx -c Debug`
- [√] 3.2 执行 `dotnet test codex-relayouter-server.Tests/codex-relayouter-server.Tests.csproj -c Debug`

## 4. 方案包归档
- [√] 4.1 将方案包迁移至 `helloagents/history/2026-01/` 并更新 `helloagents/history/index.md`
