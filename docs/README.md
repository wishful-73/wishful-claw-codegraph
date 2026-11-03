# docs

本仓的设计文档。目前只有一份，讲的是**它怎么接回主仓**。

| 文档 | 讲什么 |
|---|---|
| [mcp-integration.md](mcp-integration.md) | 接入主仓的插件体系：为什么走 MCP、协议层怎么补、主仓那边删什么改什么 |

## 一句话背景

CodeGraph 原先是主仓 `src/runtime/WishfulClaw.CodeGraph/` 下的一个 worker 模块，
被 `WorkerModuleCatalog` 静态挂载。现在搬出来独立成进程，**接入方式也一并换掉**：
不再走主仓自建的 worker 协议，改成主仓早就有的**扩展插件 + MCP stdio** 体系。

这件事的动机不是「换个壳」，是**不要重复造已有的机制**。主仓有 `Extension` 清单、
有 `McpManager`、有 stdio 子进程通道和生命周期管理 —— CodeGraph 只要符合要求就能装进去。
