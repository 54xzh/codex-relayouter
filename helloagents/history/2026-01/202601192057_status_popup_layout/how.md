# 技术设计: 状态弹窗排版与 /status 输出精简

## 技术方案

### 核心技术
- Bridge Server: ASP.NET Core Minimal API（现有 `/status`）
- WinUI Client: `ContentDialog` + `ScrollViewer` + `TextBlock`

### 实现要点
- 后端 `/status` 输出由 `StatusTextBuilder` 统一生成：
  - 删除 `AGENTS.md` 的输出行，并移除相关解析/查找逻辑，避免无用计算与误导信息。
  - 其他字段保持不变，缺失项仍显示“不可用”。
- WinUI 侧状态弹窗排版：
  - 提高 `ScrollViewer` 的 `MinWidth`，减少长行换行与拥挤。
  - 调整 `TextBlock` 的 `LineHeight` 与 `CharacterSpacing`，提升可读性。
  - 保持 `IsTextSelectionEnabled=true` 与滚动条，确保长内容可复制/查看。

## API 设计
### [GET] /status
- **变更:** 输出字段移除 `AGENTS.md` 行。
- **兼容:** 仍返回纯文本；前端按文本原样展示。

## 安全与性能
- **安全:** 账户信息仅输出摘要，不泄露密钥/Token。
- **性能:** 移除 `AGENTS.md` 相关文件扫描与 JSONL 行解析，减少无谓 I/O。

## 测试与部署
- **测试:** `dotnet build` 分别构建 `codex-bridge-server` 与 `codex-bridge`（Release）。
- **部署:** 无额外部署步骤；随现有 sidecar 与 WinUI 打包流程发布。

