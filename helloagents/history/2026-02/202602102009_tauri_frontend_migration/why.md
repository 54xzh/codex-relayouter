# 变更提案: Codex Electron 前端迁移到 Tauri 壳层

## 需求背景
当前本机安装的 Codex 客户端使用 Electron 打包。为降低运行体积、后续统一桌面端技术路线，并让现有前端可以在 `tauri-client` 目录独立演进，需要把 Electron 包内前端迁移到 Tauri 容器中运行。

## 变更内容
1. 从 `C:\Users\54xzh\AppData\Local\Codex\app-1.0.3\resources\app.asar` 提取并复用现有 `src/webview` 前端静态资源。
2. 在工作区 `tauri-client` 新建可运行的 Tauri 工程骨架（Rust + 配置 + npm 脚本）。
3. 增加 Electron 兼容桥接层（`window.electronBridge`）并把消息通道接到 Tauri command/event。
4. 在 Rust 侧实现最小主进程消息处理（ready、persisted atom、shared object、fetch、worker stub、open-in-browser 等）。

## 影响范围
- **模块:** `tauri-client`
- **文件:** `tauri-client/webview/*`、`tauri-client/src-tauri/*`、`tauri-client/package.json`
- **API:** Tauri invoke 命令与事件通道（兼容 Electron 消息结构）
- **数据:** 本地进程内状态（persisted atom/shared object），不新增磁盘持久化

## 核心场景

### 需求: 现有前端在 Tauri 中启动
**模块:** `tauri-client/webview`, `tauri-client/src-tauri`
复用 Electron 中已构建好的 Web 前端资源，在 Tauri 打开的窗口中直接加载。

#### 场景: 启动 Tauri 后进入 Codex 页面
执行 `npm run tauri:dev` 后打开主窗口并看到 Codex 前端 UI。
- 预期结果: 页面可加载，静态资源路径正确，无 Electron 环境报错导致白屏。

### 需求: Electron Bridge 兼容
**模块:** `tauri-client/webview/tauri-bridge.js`, `tauri-client/src-tauri/src/lib.rs`
前端继续使用 `window.electronBridge`，底层改由 Tauri 命令和事件处理。

#### 场景: 前端发送 ready/fetch/worker 请求
前端调用 `electronBridge.sendMessageFromView` 和 `sendWorkerMessageFromView`。
- 预期结果: Tauri 端收到请求并返回兼容格式的消息；未实现能力返回明确错误而不是无响应。

### 需求: 基础开发可运行
**模块:** `tauri-client` 根目录脚手架
提供最小运行命令和说明，便于后续继续补齐主进程能力。

#### 场景: 新成员进入 `tauri-client` 本地运行
安装依赖并执行开发命令。
- 预期结果: 能启动、能看页面、能看到桥接日志与已实现行为。

## 风险评估
- **风险:** 现有前端高度依赖 Electron 主进程协议，Tauri 初版只实现了最小子集，部分功能不可用。
- **缓解:** 未实现能力统一返回明确错误事件；桥接层保留同名通道，后续可逐步补齐不破坏前端调用点。
