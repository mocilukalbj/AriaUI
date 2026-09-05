# M01 RPC 性能与资源基线测量报告（阶段 0 结项证据）

> 依据：LIBARIA2_TEST_PLAN.md §4 及 §7 标准记录模板。
> 用途：为阶段 1 原生探针和后续原生迁移提供权威的同机真实 RPC 性能门禁对照基线。

```text
日期 / 应用 commit / aria2 commit / 构建模式：
  2026-09-05 07:46:55 UTC / git-master / aria2 1.37.0 / .NET 10.0 (Release/Debug)
OS / CPU / 浏览器及扩展 / 运行配置版本：
  Unix 6.19.10.300 / X64 (12 Cores) / None (Direct WebSocket RPC) / EngineRuntimeConfig v1.0
变更目标与涉及的 D 编号：
  建立 Phase 0 M01 真实 RPC 性能基线（冷启动、命令延迟 p50/p95、资源占用），涉及 D04/D06/§4。
已执行测试 ID、状态、证据位置：
  M01: PASS. 证据保存在 tests/results/M01_RPC_BASELINE.md。
未执行 ID 与 Pending / Blocked 原因：
  阶段 1 原生探针相关测试（A01-A05）保留在阶段 1 执行。
```

## 1. 真实 RPC 冷启动时间测量 (Cold Startup)

测量协议：进程启动 -> HTTP/WebSocket RPC Readiness Probe（包含 process launch、socket bind、session 初始化）。各 30 轮样本统计：

| 测试场景 | 样本数 | Min (ms) | P50 (ms) | P95 (ms) | Max (ms) | Avg (ms) | 试验目标 (500ms) |
|---|---|---|---|---|---|---|---|
| 空任务 Session 启动 | 30 | 237.17 | 245.13 | 272.85 | 344.84 | 251.75 | < 500ms 达成 |
| 预置 10 任务 Session 恢复 | 30 | 235.45 | 246.34 | 264.07 | 265.23 | 247.37 | < 500ms 达成 |

## 2. 常用 RPC 命令延迟基线 (Command Latency)

测量协议：固定负载 50 次预热后，连续进行 3 轮、每轮 1,000 次 RPC 调用 (`aria2.getGlobalStat`)，高精度记录往返延迟。按照 §4 规定，使用三轮 P95 的中位数作为后续 native 对照门禁值（候选门禁为 <= RPC基线的 110%）：

| 轮次 | 命令名称 | 调用次数 | Min (ms) | P50 (ms) | P95 (ms) | Max (ms) | Avg (ms) |
|---|---|---|---|---|---|---|---|
| 第 1 轮 | aria2.getGlobalStat | 1000 | 0.29 | 0.92 | 1.19 | 6.25 | 0.93 |
| 第 2 轮 | aria2.getGlobalStat | 1000 | 0.24 | 0.78 | 0.99 | 3.35 | 0.78 |
| 第 3 轮 | aria2.getGlobalStat | 1000 | 0.21 | 0.75 | 1.03 | 2.27 | 0.75 |

> **三轮 P95 中位门禁基线值**：`1.03 ms`（阶段 4 候选原生实现上限阈值为：`1.13 ms`）。

## 3. 资源占用基线 (Resource Usage)

| 指标 | 测量值 | 依据 / 备注 |
|---|---|---|
| aria2c 空闲 CPU 占用率 | 0.00% | 1 秒空闲窗口采样 |
| aria2c 工作集内存 (RSS) | 30.03 MiB | 进程物理内存常驻集 |
| aria2c 私有提交内存 (Private Memory) | 17.96 MiB | 专用内存分配 |
| aria2c 线程数 | 1 | 活动线程数 |

## 4. 结论与阶段退出判定

1. 真实 RPC 冷启动时间均值在 251.8ms 级别，远优于 500ms 试验目标，空任务与恢复任务无异常延迟。
2. 单命令 RPC 往返 P95 稳定在 1.03ms，为阶段 1 原生探针和阶段 4 原生实现确定了严格的性能对比基准。
3. 资源占用稳定，无内存泄漏与多余线程。本数据正式归档为 Phase 0 结项依据。
