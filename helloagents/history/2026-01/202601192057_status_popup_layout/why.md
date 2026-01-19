# 变更提案: 状态弹窗排版与 /status 输出精简

## 需求背景
当前 Chat 页“状态”弹窗已能展示 `/status` 的多行文本，但仍存在两点体验与信息噪音问题：
1. `/status` 输出包含 `AGENTS.md` 行，用户希望移除该项。
2. 弹窗宽度与排版偏紧，长行易换行/拥挤，需要加宽并调整字间距/行间距以提升可读性。

## 变更内容
1. `/status` 文本输出移除 `AGENTS.md` 行，仅保留：模型、目录、批准策略、沙盒、账户、session id、5h 限额、周限额（缺失项显示“不可用”）。
2. WinUI 状态弹窗加宽，并对文本显示调整字间距与行高，保证多行信息更易读。

## 影响范围
- **模块:**
  - Bridge Server（`/status` 输出）
  - WinUI Client（状态弹窗排版）
- **文件:**
  - `codex-bridge-server/Bridge/StatusTextBuilder.cs`
  - `codex-bridge/Pages/ChatPage.xaml.cs`
  - `helloagents/wiki/api.md`
  - `helloagents/wiki/modules/bridge-server.md`
  - `helloagents/wiki/modules/winui-client.md`
  - `helloagents/CHANGELOG.md`

## 核心场景

### 需求: 状态查询输出精简
**模块:** Bridge Server

#### 场景: 用户点击“状态”
弹窗展示 `/status` 输出文本，不包含 `AGENTS.md` 行；若某字段无法获取，显示“不可用”。

### 需求: 状态弹窗可读性提升
**模块:** WinUI Client

#### 场景: 长路径/多字段显示
弹窗更宽，文本行距与字间距更舒适，仍支持滚动与文本选择。

## 风险评估
- **风险:** 字体/排版属性在不同系统字体渲染下效果略有差异。
- **缓解:** 使用保守的字间距值；保留滚动条与等宽字体，避免信息丢失。

