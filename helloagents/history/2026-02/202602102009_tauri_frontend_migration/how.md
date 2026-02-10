# 技术设计: Codex Electron 前端迁移到 Tauri 壳层

## 技术方案
### 核心技术
- Tauri 2（Rust 主进程）
- 现有 Codex Web 静态资源（从 Electron `app.asar` 提取）
- 兼容桥接脚本（浏览器侧把 Electron Bridge 调用转为 Tauri invoke/event）

### 实现要点
- 保留 `window.electronBridge` API 形态，避免改动前端打包产物。
- Rust 侧复用 Electron 消息结构（`type` 字段驱动）处理最小可运行路径。
- fetch 走 Rust `reqwest`，结果按 Electron 现有 `fetch-response` 结构回传。
- worker 请求先提供可预期失败响应（`worker-response.error`），保证前端不会无限等待。

## 架构设计
```mermaid
flowchart LR
  Webview[Codex Webview\n(webview/index.html)] --> Bridge[tauri-bridge.js]
  Bridge -->|invoke| TauriCmd[Tauri Commands]
  TauriCmd --> Host[Tauri Rust Host]
  Host -->|emit codex_desktop:message-for-view| Bridge
  Host -->|emit codex_desktop:worker:*:for-view| Bridge
  Bridge -->|MessageEvent| Webview
```

## 架构决策 ADR
### ADR-001: 迁移阶段优先“协议兼容”而非“前端重构”
**上下文:** 现有前端是构建产物，直接改动成本高且回归面大。  
**决策:** 通过兼容 `electronBridge` 与消息协议实现迁移首版。  
**理由:** 最快建立可运行基线，后续可逐步替换内部能力。  
**替代方案:** 直接反编译并重构前端源码 → 拒绝原因: 成本高、风险大。  
**影响:** 首版功能不完整，但后续补齐路径清晰。

## API设计
### Tauri Command: `send_message_from_view`
- **请求:** `message: serde_json::Value`
- **响应:** `Result<(), String>`
- **说明:** 处理 `ready/fetch/persisted-atom/shared-object/open-in-browser` 等消息并回发事件。

### Tauri Command: `send_worker_message_from_view`
- **请求:** `workerId: string`, `message: object`
- **响应:** `Result<(), String>`
- **说明:** 回发 `worker-response`，当前为兼容 stub。

### Tauri Command: `show_context_menu`
- **请求:** `items: object`
- **响应:** `{ id: string | null }`

### Tauri Command: `trigger_sentry_test_error`
- **请求:** 无
- **响应:** `Result<(), String>`

### Tauri Command: `get_bridge_meta`
- **请求:** 无
- **响应:** `{ buildFlavor, appVersion, buildNumber, codexAppSessionId }`

## 安全与性能
- **安全:** `open-in-browser` 仅允许 `http/https` 协议。
- **性能:** 不做重打包，直接加载静态资源；消息处理走异步，避免阻塞 UI 线程。

## 测试与部署
- **测试:**
  - `cargo check --manifest-path tauri-client/src-tauri/Cargo.toml`
  - `npm run tauri:dev` 启动验证页面和桥接链路
- **部署:** 后续可用 `npm run tauri:build` 产出桌面安装包。
