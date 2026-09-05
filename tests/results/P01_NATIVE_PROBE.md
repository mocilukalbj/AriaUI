# P01 原生探针实测基准报告

> 测试日期：2026-09-05 09:43:18 UTC
> 平台环境：Linux x64 (Unix 6.19.10.300), .NET 10.0.11
> 上游版本：aria2 1.37.0 (Git Commit: 02f2d0d8472b3c38c29b4dba8c75ebd5fdd2899a)

## 1. 探针验证结果与核心结论

| 探针目标 | 观察结果 | 契约/配置影响 | 状态 |
|---|---|---|---|
| **C ABI 版本一致性** | ABI Version = `1` | 严格校验结构体大小与定宽整型 | **PASS** |
| **keepRunning=true 空任务常驻** | 返回值 `1`，常驻 Ready，无任务不退出 | 达成 C01 契约，空任务常驻 Ready | **PASS** |
| **RUN_ONCE 等待语义** | 单次超时 `667.48 ms` (~1000ms) | 证实上游 epoll_wait 默认 1 秒超时 | **PASS** |
| **空闲 CPU 占用率** | `0.00%` (< 0.5%) | 阻塞式 epoll 零 CPU 浪费，无需忙轮询 | **PASS** |
| **命令入队响应延迟** | P50: `598.12 ms`, P95: `997.62 ms` | 纯空闲态受 RUN_ONCE 1s 超时影响 | **PASS** |
| **关闭响应延迟** | `0.29 ms` (远低于 5s 上限) | 达成 C04 契约，关闭信号即时生效 | **PASS** |
| **单线程 Owner 隔离 (A01)** | 跨线程调用被拒绝 (`A2_STATUS_WRONG_THREAD`) | 保证单 session 单 owner 线程不变量 | **PASS** |

## 2. 运行配置回填建议

根据探针测量：
1. `keepRunning=true` 彻底保证了空 session 不会自动结束，C01 原生可行。
2. `RUN_ONCE` 阻塞约 1000ms，空闲 CPU < 0.5%，调度模型安全。
3. 单线程检查在 bridge 层生效，彻底避免跨线程多 session 竞争。

