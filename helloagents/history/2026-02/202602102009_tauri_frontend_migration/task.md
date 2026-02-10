# 任务清单: Codex Electron 前端迁移到 Tauri 壳层

目录: `helloagents/plan/202602102009_tauri_frontend_migration/`

---

## 1. Tauri 工程骨架
- [√] 1.1 在 `tauri-client/package.json` 与 `tauri-client/src-tauri/tauri.conf.json` 建立可运行的 Tauri 项目配置，验证 why.md#需求-现有前端在-tauri-中启动-场景-启动-tauri-后进入-codex-页面
- [√] 1.2 在 `tauri-client/src-tauri/src/lib.rs` 实现 Tauri 启动入口与 command 注册，验证 why.md#需求-electron-bridge-兼容-场景-前端发送-readyfetchworker-请求

## 2. 前端迁移与桥接
- [√] 2.1 复制 Electron `webview` 静态资源到 `tauri-client/webview`，验证 why.md#需求-现有前端在-tauri-中启动-场景-启动-tauri-后进入-codex-页面
- [√] 2.2 在 `tauri-client/webview/tauri-bridge.js` 实现 `window.electronBridge` 兼容层，并在 `tauri-client/webview/index.html` 注入，验证 why.md#需求-electron-bridge-兼容-场景-前端发送-readyfetchworker-请求

## 3. 主进程最小能力
- [√] 3.1 在 `tauri-client/src-tauri/src/lib.rs` 实现 `ready/persisted-atom/shared-object` 消息处理，验证 why.md#需求-electron-bridge-兼容-场景-前端发送-readyfetchworker-请求
- [√] 3.2 在 `tauri-client/src-tauri/src/lib.rs` 实现 `fetch/fetch-stream-error` 回传结构与 `open-in-browser`，验证 why.md#需求-electron-bridge-兼容-场景-前端发送-readyfetchworker-请求
- [√] 3.3 在 `tauri-client/src-tauri/src/lib.rs` 实现 worker stub 响应，验证 why.md#需求-electron-bridge-兼容-场景-前端发送-readyfetchworker-请求

## 4. 安全检查
- [√] 4.1 执行安全检查（外部 URL 只允许 http/https，未实现能力返回明确错误）

## 5. 文档更新
- [√] 5.1 更新 `tauri-client/README.md` 说明运行方式与已知限制
- [√] 5.2 更新 `helloagents/wiki/modules/` 与 `helloagents/CHANGELOG.md`

## 6. 测试
- [√] 6.1 运行 `cargo check --manifest-path tauri-client/src-tauri/Cargo.toml`
- [√] 6.2 运行 `npm run tauri:dev -- --help` 验证 CLI 与项目集成
