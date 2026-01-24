# 任务清单：自动翻译 Trace（思考过程）与摘要

- [√] Bridge Server：新增 `Bridge:Translation` 配置（Enabled/BaseUrl/ApiKey/TargetLocale/Model(必填)/MaxRequestsPerSecond(默认1)/MaxConcurrency(默认2)…）并接入 DI
- [√] Bridge Server：实现翻译缓存存储（`%LOCALAPPDATA%\\codex-relayouter\\translations.json`），按 `locale + sourceHash` 去重
- [√] Bridge Server：实现翻译客户端（OpenAI 兼容 Chat Completions），支持超时/最大并发/每秒速率限制（目标语言固定 `zh-CN`）
- [√] Bridge Server：运行中事件接入——仅在 `run.reasoning`（完成态）触发翻译，翻译完成后复用 `run.reasoning` 事件做覆盖广播
- [√] Bridge Server：历史回放接入——`/api/v1/sessions/{id}/messages` 在缓存命中时返回译文
- [√] WinUI：设置页增加自动翻译配置入口（开关/BaseUrl/ApiKey/Model/每秒最大请求数/最大并发），应用后重启本机后端以生效
- [ ] （可选）管理接口/命令：运行时开关与配置更新（避免改 `appsettings.json` + 重启）
- [√] 测试：覆盖“不翻译 delta / 缓存命中去重 / 不写 session JSONL / WS 覆盖广播”关键路径
- [√] 文档：更新 `helloagents/wiki/api.md`、`helloagents/wiki/modules/protocol.md`、`helloagents/wiki/modules/bridge-server.md` 与 CHANGELOG
