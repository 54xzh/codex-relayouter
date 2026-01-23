# 轻量迭代：设置页查看后端日志

> **状态**：已完成  
> **范围**：WinUI 设置页 + 后端进程启动

## 任务清单

- [√] WinUI 启动本机 Bridge Server 时重定向 stdout/stderr 并写入本地日志文件（含简单轮转）
- [√] WinUI 设置页新增“查看后端日志”入口：弹窗展示日志尾部（支持刷新/复制/打开日志文件夹）
- [√] 抽取日志尾部读取工具 `LogTailReader`（共用库）并补齐单元测试
- [√] 更新知识库：补充 WinUI/Bridge Server 文档与变更记录

## 验证
- `dotnet test codex-relayouter-common.Tests/codex-relayouter-common.Tests.csproj`
- `dotnet build codex-relayouter-server/codex-relayouter-server.csproj -c Release`
- `dotnet build codex-relayouter/codex-relayouter.csproj -c Release -p:Platform=x64`

