# Tauri Client

## 目的
在不改动现有 Codex 构建前端的前提下，把 Electron 壳层替换为 Tauri，并提供可继续迭代的迁移基线。

## 模块概述
- **职责:** 承载从 Electron 提取的 `webview` 前端，提供 `electronBridge` 兼容接口，转发到 Tauri Rust 主进程。
- **状态:** 🚧开发中
- **最后更新:** 2026-02-10

## 规范

### 需求: 现有前端在 Tauri 中启动
**模块:** Tauri Client
复用 Electron 的前端静态资源，使页面能在 Tauri 窗口直接加载。

#### 场景: 启动开发模式后进入主页面
执行 `npm run tauri:dev`。
- 预期结果: `webview/index.html` 能加载，页面无桥接缺失导致的启动失败。

### 需求: Electron Bridge 兼容
**模块:** Tauri Client
前端继续通过 `window.electronBridge` 通信，不需要改现有构建产物。

#### 场景: 前端发送 ready/fetch/worker 消息
页面调用 `sendMessageFromView` / `sendWorkerMessageFromView`。
- 预期结果: Tauri 侧收到并处理；未实现功能返回明确错误消息。

## API接口
### `send_message_from_view`
**描述:** 接收前端主消息通道请求并按 `type` 分发处理。  
**输入:** `message`（JSON）  
**输出:** `Result<(), String>`

### `send_worker_message_from_view`
**描述:** 接收 worker 请求并回发 `worker-response`。  
**输入:** `workerId`, `message`  
**输出:** `Result<(), String>`

### `get_bridge_meta`
**描述:** 返回前端初始化需要的桥接元信息。  
**输入:** 无  
**输出:** `{ buildFlavor, appVersion, buildNumber, codexAppSessionId }`

## 依赖
- Rust: `tauri`, `reqwest`, `serde`, `uuid`
- 前端: 提取自 Electron 包的静态资源

## 变更历史
- [202602102009_tauri_frontend_migration](../../history/2026-02/202602102009_tauri_frontend_migration/) - 新增 Tauri 壳层与 Electron Bridge 兼容迁移基线
