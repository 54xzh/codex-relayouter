# Tauri Client（Codex 前端迁移版）

这个目录是把本机 Electron 安装包中的 `webview` 前端迁到 Tauri 壳层后的工程。

## 目录说明
- `webview/`: 从 Electron `app.asar` 提取的前端静态资源
- `webview/tauri-bridge.js`: Electron Bridge 兼容层
- `src-tauri/`: Tauri Rust 主进程

## 本地运行
1. 进入目录：`cd tauri-client`
2. 安装依赖：`npm install`
3. 启动开发模式：`npm run tauri:dev`

## 当前状态
- 已完成：页面加载、`window.electronBridge` 兼容、基础消息通道、基础 fetch/open-in-browser。
- 未完成：完整 Worker 能力、完整 MCP/Terminal/通知等 Electron 主进程能力。

## 说明
首版目标是“可运行迁移基线”，优先保证前端在 Tauri 中启动并可逐步替换能力。
