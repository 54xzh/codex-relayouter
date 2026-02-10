# codex-relayouter

> Codex CLI 的桌面 GUI 壳 + 本机 Bridge Server，并预留 Android 远程接口实现多端同步。

---

## 1. 项目概述

### 目标与背景
将 `codex` CLI 的交互能力以 GUI 方式提供，并通过本机后端服务统一会话与事件流，支持 Windows/Android 同步查看与控制同一会话。

### 范围
- **范围内:** 聊天与流式输出、会话管理、工作区选择、diff 展示、设置页、权限模式/模型/思考深度切换、可选远程访问
- **范围外（MVP 不做）:** 多用户体系、云端托管与跨设备离线同步、生产级公网暴露默认开启

### 干系人
- **负责人:** 本仓库维护者

---

## 2. 模块索引

| 模块名称 | 职责 | 状态 | 文档 |
|---------|------|------|------|
| WinUI Client | Windows GUI（聊天/会话/diff/设置） | 开发中 | [modules/winui-client.md](modules/winui-client.md) |
| Tauri Client | Electron 前端迁移壳层（兼容桥接） | 开发中 | [modules/tauri-client.md](modules/tauri-client.md) |
| Android Client | Android 远程入口（配对/会话/聊天） | 开发中 | [modules/android-client.md](modules/android-client.md) |
| Bridge Server | 统一会话/事件、驱动 Codex CLI、远程接口 | 开发中 | [modules/bridge-server.md](modules/bridge-server.md) |
| Protocol | 前后端与多端同步协议（JSON/WS） | 开发中 | [modules/protocol.md](modules/protocol.md) |

---

## 3. 快速链接
- [技术约定](../project.md)
- [架构设计](arch.md)
- [API 手册](api.md)
- [数据模型](data.md)
- [变更历史](../history/index.md)
