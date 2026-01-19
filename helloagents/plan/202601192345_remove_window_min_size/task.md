# 任务清单: 去除窗口最小尺寸限制

目录: `helloagents/plan/202601192345_remove_window_min_size/`

---

## 1. WinUI 窗口行为
- [ ] 1.1 移除 WinUI 3 主窗口最小尺寸限制（不再拦截 WM_GETMINMAXINFO）
- [ ] 1.2 保持启动窗口初始大小与居中逻辑不变

## 2. 文档更新
- [ ] 2.1 更新 `helloagents/wiki/modules/winui-client.md`：窗口约定不再设置最小尺寸，并补充变更历史条目
- [ ] 2.2 更新 `helloagents/CHANGELOG.md`：记录本次窗口体验调整
- [ ] 2.3 更新 `helloagents/history/index.md`：记录本次变更索引

## 3. 构建验证
- [ ] 3.1 执行 `dotnet build` 验证编译通过
  > 备注: 由于 MSIX 打包限制，构建需指定平台，例如 `dotnet build -p:Platform=x64`（AnyCPU 可能失败）。
