# codex-bridge

> Codex CLI 的桌面 GUI 壳 + 本机 Bridge Server。当前以 Windows（WinUI 3）为主，并预留 Android/远程接口用于多端同步查看与控制同一会话。

---

## 项目目标

- 将 `codex` CLI 的对话与事件流能力以 GUI 方式提供（流式输出、会话管理、diff/设置等）
- 通过本机 Bridge Server 统一会话、运行与事件广播，为后续多端接入提供稳定协议层

---

## 功能概览（开发中）

- WinUI Client：聊天与流式输出、会话列表/创建/回放、工作区切换、运行取消、审批弹窗、图片输入
- Bridge Server：HTTP/WS 接口、驱动本机 `codex app-server`（JSON-RPC）、会话存储与回放、统一鉴权策略
- 默认安全策略：仅允许回环访问；远程访问默认关闭

---

## 目录结构

- [`codex-bridge/`](codex-bridge/)：Windows 客户端（.NET 8 / WinUI 3）
- [`codex-bridge-server/`](codex-bridge-server/)：Bridge Server（.NET 8 / ASP.NET Core）
- [`helloagents/`](helloagents/)：项目知识库（SSOT：概述/架构/API/数据/模块文档/变更历史）

---

## 快速开始（开发）

### 前置条件

- Windows 10 1809（Build 17763）及以上 / Windows 11
- Visual Studio 2022（建议安装：.NET 桌面开发、Windows 10/11 SDK）
- .NET 8 SDK
- 已安装 `codex` CLI（命令行可运行 `codex --version`）

### 运行（推荐：WinUI 自动拉起后端）

1. 使用 Visual Studio 打开 `codex-bridge.slnx`
2. 启动项目选择 `codex-bridge`（建议 x64）
3. F5 启动：应用会从输出目录的 `bridge-server/` 中拉起后端并自动连接（随机端口 + `/api/v1/health` 探测）

### 独立启动后端（可选）

```powershell
dotnet run --project .\codex-bridge-server\codex-bridge-server.csproj -- --urls http://127.0.0.1:5000
```

然后在 WinUI 设置页将 WS 地址设置为 `ws://127.0.0.1:5000/ws`。

---

## 配置与安全

- Bridge Server 配置：[`codex-bridge-server/appsettings.json`](codex-bridge-server/appsettings.json)
  - `Bridge:Security:RemoteEnabled` 默认 `false`（仅允许回环）
  - 若开启远程访问：务必设置 `Bridge:Security:BearerToken`，并谨慎配置监听地址与网络边界
- Codex 模型/思考深度：WinUI 会读取/写回 `~/.codex/config.toml`（键：`model`、`model_reasoning_effort`）

---

## 更多文档（SSOT）

- 项目概述：[`helloagents/wiki/overview.md`](helloagents/wiki/overview.md)
- 架构设计：[`helloagents/wiki/arch.md`](helloagents/wiki/arch.md)
- API 手册：[`helloagents/wiki/api.md`](helloagents/wiki/api.md)
- 数据模型：[`helloagents/wiki/data.md`](helloagents/wiki/data.md)
- 模块文档：[`helloagents/wiki/modules/`](helloagents/wiki/modules/)
- 变更历史：[`helloagents/CHANGELOG.md`](helloagents/CHANGELOG.md)、[`helloagents/history/index.md`](helloagents/history/index.md)
