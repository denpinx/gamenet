# 多人游戏网络架构设计

![ci](https://github.com/denpinx/gamenet/actions/workflows/ci.yml/badge.svg)

> **关于实现方式：本项目由 vibe coding 完成。**
>
> 需求、技术选型、设计取舍和验收标准由人给出；文档、协议、实现、测试、基准和示例代码由 AI（DeepSeek）按对话迭代生成。代码经过 251 个测试、覆盖率门槛和多种网络条件的基准验证，但**没有经过生产环境审计**——上生产前请自行做代码审查、安全评估和压测。把它当作一个可运行、可讨论的参考实现，而不是可以直接出货的中间件。

## 从哪里开始

**第一次接触，先看教程：[08-使用教程.md](08-使用教程.md)** —— 从零跑通「连接 → RPC → 订阅集合 → 收到复制」，包含最小服务端/客户端、属性速查和排错表。

推荐路线：

1. 读 [08-使用教程.md](08-使用教程.md)，跑最小示例：
   ```bash
   dotnet run --project src/GameNet.Demo.Minimal -c Release
   ```
2. 看 [07-示例-井字棋.md](07-示例-井字棋.md)，理解注册、匹配、对局和集合过滤；
3. 跑 `GameNet.Demo.Scenarios` 看订阅、噪音集合、增量更新；
4. 需要设计背景时再看 02–06。

## 这是什么

纯 C# 双端、引擎无关、**全自研协议栈**的多人游戏网络架构参考实现。

不依赖 MemoryPack、Kestrel、KCP、Mirror/FishNet 之类的现成方案：单 UDP 端口、多逻辑通道、自建可靠层、位图差量快照、源生成编解码、代码优先的 SQLite/MySQL 持久化，外加一套可运行的基准与示例。

## 能力

| 能力 | 说明 |
| --- | --- |
| 传输 | 单 UDP 端口，三通道：状态（可靠、默认乱序）、RPC（可靠有序）、输入（不可靠原始） |
| 可靠层 | seq + 累计 ACK + SACK、RTT/RTO、快速重传、NACK、拥塞窗口、分片重组、`Skip` 放弃、可选状态冗余（FEC） |
| 状态复制 | 类型化集合 `[Entity]` 任意字段；固定九字段 `EntityState`；字段位图差量、Chunk 分块、非原子应用、每实体 tick 防旧包、墓碑防复活、Resync |
| 兴趣管理 | 按会话 `Filter`（圆形 AOI 只是默认可替换的 Filter）、多集合、按名字订阅/退订、集合注册表下发 |
| 断线重连 | 会话 token、宽限期挂起、订阅/游戏状态保留、恢复后全量重发；`ReconnectAsync` 带指数退避 |
| Tick 同步 | 服务端每 N tick 广播权威 tick，客户端 `TickClock` 校正漂移，可配容差 |
| 带宽限速 | 每会话令牌桶（字节/秒），超限跳过本帧且不推进基线 |
| RPC | 方法 ID + 源生成编解码的类型化调用；`[RpcGroup]`/`[Rpc]` 的名字化动态调用（支持 `[Packet]` 参数/返回、`Session` 注入）；构造注入、单实例/每连接生命周期 |
| 持久化 | 代码优先建表、嵌套拆表、外键与级联、部分更新、Blob+压缩、自动补列迁移、`PersistableSet` tick 自动保存 |
| 源生成 | `[Packet]`/`[Entity]` → `Write/Read/DiffMask/WriteDelta/ApplyDelta/SchemaHash/IWireCodec/IEntityCodec` |
| 工具 | 多进程基准（10/50 客户端、1k–20k 实体、丢包/延迟/抖动/重复/FEC）、区块同步模拟、3 个可运行示例 |

## 快速开始

```bash
pwsh -File scripts/run-all.ps1              # build + test + benchmark
pwsh -File scripts/run-all.ps1 -SkipBench   # 只 build + test

dotnet test tests/GameNet.Tests/GameNet.Tests.csproj          # 251 passed
dotnet run --project src/GameNet.Demo.Minimal -c Release              # 最小示例：一个集合 + 一个 RPC 组
dotnet run --project src/GameNet.Demo.TicTacToe -c Release -- --bot   # 井字棋：注册 + 匹配 + 对局
dotnet run --project src/GameNet.Demo.Scenarios -c Release -- all     # 聊天室订阅 / 噪音集合 / 增量更新
dotnet run --project src/GameNet.Benchmarks -c Release -- --quick
```

规模与网络条件：

```bash
dotnet run --project src/GameNet.Benchmarks -c Release -- --clients 50 --objects 1000 --aoi 1
dotnet run --project src/GameNet.Benchmarks -c Release -- --chunks
dotnet run --project src/GameNet.Benchmarks -c Release -- --objects 1000 --loss 0.05 --measure 6
dotnet run --project src/GameNet.Benchmarks -c Release -- --objects 1000 --latency 80 --jitter 20 --loss 0.01
dotnet run --project src/GameNet.Benchmarks -c Release -- --objects 1000 --latency 80 --jitter 20 --loss 0.01 --redundancy 2
```

## 文档

| 文件 | 内容 |
| --- | --- |
| **[08-使用教程.md](08-使用教程.md)** | **先看这个：从零接入、最小实现、RPC、订阅、持久化、排错** |
| [02-类架构调研.md](02-类架构调研.md) | 业界术语映射、参考家族、逐条对照、结论 |
| [03-持久化设计.md](03-持久化设计.md) | tick 驱动 + 单条 Load/Save + 按类型建表 + Blob 模式 |
| [04-技术栈选型.md](04-技术栈选型.md) | 单 UDP 多通道、可靠层、序列化、差量、RPC |
| [05-源生成设计.md](05-源生成设计.md) | 生成清单、属性设计、工程结构、诊断 |
| [06-实现说明.md](06-实现说明.md) | **实现状态、测试、覆盖率、基准结果、已知缺口** |
| [07-示例-井字棋.md](07-示例-井字棋.md) | 完整产品闭环示例：注册 + 匹配 + 对局 |

## 基准摘要（10 客户端，30 Hz，UDP 回环）

| 物体数 | tick avg | tick p99 | 单客户端下行 | 合计 |
| ---: | ---: | ---: | ---: | ---: |
| 1 000（AOI 开） | 0.83 ms | 4.82 ms | 12.7 KB/s | 1.16 Mbit/s |
| 20 000（AOI 开） | 2.11 ms | 6.35 ms | 14.2 KB/s | 1.30 Mbit/s |
| 1 000（AOI 关） | 2.16 ms | 5.13 ms | 124.0 KB/s | 10.45 Mbit/s |

兴趣过滤让带宽与场景规模解耦（20k 物体仍约 1.3 Mbit/s）；弱网（80 ms 单向 + 抖动 + 1% 丢包）下客户端感知延迟 p50 94 ms、p99 266 ms，状态通道 2× 冗余可把 p99 降到 125 ms；快照为非原子应用，坏块只影响对应实体。完整数据：[bench-20261022-213723.md](results/bench-20261022-213723.md)。

## 项目结构

```
src/GameNet.Core          协议、序列化、客户端世界、服务端 tick、RPC、集合
src/GameNet.Transport     单 UDP 多通道、可靠层、RpcClient、区块客户端
src/GameNet.Persistence   代码优先 SQLite/MySQL schema、CRUD、Blob、自动迁移
src/GameNet.Generators    Roslyn 增量生成器（[Packet] → 编解码）
src/GameNet.Benchmarks    多进程基准（服务端/客户端子进程）
src/GameNet.Demo.*        最小示例、井字棋、场景（聊天/噪音/增量）
tests/GameNet.Tests       251 个单元/集成/故障注入/协议兼容测试
scripts/run-all.ps1       build + test（含覆盖率门槛）+ benchmark
```

## 项目定位

**alpha 级框架内核**：协议、传输、持久化、基准都已跑通并有测试；离「可直接上线」还差鉴权/TLS、自动重连退避、对局持久化、空间索引、带宽限速和跨机压测（重连 token/会话恢复已支持）。代码约 6 500 行，251 个测试。

横向对比：远未达到 FishNet / Colyseus 的成熟度（多年迭代、文档、社区），但已经超过「玩具 demo」——它能承载一个完整产品闭环（见井字棋示例），并且每个性能数字都有可复现的基准。

## 已知边界

- 服务端与客户端已拆成独立进程（同机回环）；可注入丢包/延迟/抖动/重复包，但没有跨机 NAT、带宽限速、TLS/鉴权。
- MySQL 只生成 DDL，未连真实服务器；源生成覆盖编解码，不覆盖建表。
- 传输层握手无 cookie 防放大、无 TLS；cwnd 策略保守。
- 动态 RPC 用反射调用；参数/返回值支持基本类型和 `[Packet]` 类型，但 `[Rpc]` 方法还不支持注入 `Session`。\n- 源生成不支持继承/多态：基类字段不会被序列化，派生类型标了特性会报 `GEN006`。
- 详细清单见 [06-实现说明.md](06-实现说明.md) 第八节。
