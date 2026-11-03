# wishful-claw-codegraph

WishfulClaw 的代码图谱模块。从主仓 [`wishful-claw`](https://github.com/wishful-73/wishful-claw) 拆出，作为**可选插件**独立演进。

## 为什么拆

主仓的 `src/runtime/WishfulClaw.CodeGraph` 背着 **42.5 MB tree-sitter** 原生库，是安装包 108 MB 的主要来源之一。而大部分用户不写代码，代码图谱对他们没有价值。

拆成可选插件后：不装就不下载，装了按需拉取。对应主仓需求 **S-303**。

## 当前状态

**代码已迁入，可独立编译、可独立运行。** 主仓的 `src/runtime/WishfulClaw.CodeGraph/` 尚未删除 —— 主仓侧的接入改造（改走子进程）还没做，两边会并存一段时间。

本仓**不引用主仓的任何程序集**。这不是口号，是设计约束：`Host/HostContracts.cs` 自带那 5 个契约类型，`Support/CodeGraphJson.cs` 自带 JSON 读取工具，`CodeGraphDataDir.cs` 内联了数据目录名。往这里加主仓依赖之前，先想清楚为什么。

产物是一个独立进程：

```
codegraph.exe  <── stdio，newline-delimited JSON-RPC ──  宿主
```

```jsonc
// 请求
{"id": 1, "method": "codegraph/explore", "params": {"query": "..."}}
// 响应
{"id": 1, "result": {...}}
// 错误
{"id": 1, "error": {"code": -32601, "message": "Method not found: ..."}}
// 进度事件（长任务执行中推送，无 id）
{"event": "codegraph/index-progress", "params": {...}}
```

方法名与主仓原来的 worker RPC 完全一致（`codegraph/index`、`codegraph/explore`、`codegraph/status` …），所以协议层没有重新设计。

## 本仓当前的分发内容

代码本体还没来，但仓库已经在用了：**语言包**（tree-sitter 语法库，18 种语言 + 核心运行时）从本仓的 release 资产分发。

- Release：[`grammars-v1`](https://github.com/wishful-73/wishful-claw-codegraph/releases/tag/grammars-v1)（纯资产发布，与主程序版本解耦）
- 资产：`codegraph-grammars-win-x64.zip` —— 4,830,638 字节（原始 dll 42.54 MB，压缩率约 9:1）
- 直链：`https://github.com/wishful-73/wishful-claw-codegraph/releases/download/grammars-v1/codegraph-grammars-win-x64.zip`
- 生成脚本：主仓 `scripts/pack-codegraph-grammars.mjs`

WishfulClaw 客户端在「设置 → 应用与插件 → 代码图谱」点「下载语言包」时按需拉取，不随安装包发布。

两条硬约束：

1. **本仓必须保持 public** —— 私有仓的 release 资产匿名请求一律 404，客户端直接拿不到。
2. **语言包变更时才发 `grammars-v2`**，不要跟主程序版本走。

## 目录结构

`src/WishfulClaw.CodeGraph/`，**200 个源码文件**（199 个 `.cs` + 1 个 `.csproj`，约 5.3 万行）：

| 目录 | 职责 |
|---|---|
| `Host/` | 自带契约 + stdio JSON-RPC 宿主（**本仓独有，不来自主仓**） |
| `Context/` | 上下文构建 |
| `Extraction/` | 语法提取（tree-sitter 胶水） |
| `Graph/` | 图上操作 |
| `Mcp/` | MCP 服务端 |
| `Resolution/` | 符号解析 |
| `Scanning/` | 文件扫描 |
| `Search/` | 检索 |
| `Serialization/` | 序列化 |
| `Storage/` | 持久化（SQLite） |
| `Support/` | 杂项 |

顶层另有 `CodeGraphEngine` / `CodeGraphModule` / `CodeGraphNodeIdFactory` 等 13 个类型。

## 断依赖时处理的三件事

### 1. 两条 `ProjectReference` 已删掉

原 csproj 直接引用主仓的 `WishfulClaw.Contracts` 与 `WishfulClaw.Core`。实测真实用量收敛得很干净，于是：

- 5 个契约类型抄进 `Host/HostContracts.cs`（形状逐字兼容，20+ 个 handler 零改动）
- `JsonHelpers`（169 行）抄进 `Support/CodeGraphJson.cs`，改名 `CodeGraphJson` 避免与主仓同名类型二义
- `WishfulClawPaths.DataDirName` 内联成字面量 `".wishful-claw"`

断完 `dotnet build` 0 警告 0 错误 —— 这条已经过了。

### 2. `Storage/` 是独立库，最容易漏

15 个文件，不是语法树的一部分，剥离时容易被当成普通目录跟着搬。它有自己的 SQLite 连接工厂与 schema。

### 3. 工具调用已从进程内变跨进程

原来 `CodeGraphModule` 挂在主仓 Worker catalog 里，调用是进程内的。现在是独立进程 + stdio JSON-RPC，每个请求多一次管道往返。查询延迟会上升 —— 要么做批量接口，要么接受这个代价。**这是长期成本，不是一次性改动。**

## 与主仓的关系（接入未完成）

主仓那边还有三件没做，做完才算真正插件化：

1. 删掉 `src/runtime/WishfulClaw.CodeGraph/` —— 现在两边并存
2. 删掉 `WishfulClaw.Worker.csproj` 的 `ProjectReference`、`WorkerModuleCatalog.cs` 的 `new CodeGraphModule()`、`Program.cs` 的 `CodeGraphNativeLibraryResolver.Install()`
3. 加一个子进程管理器：拉起 `codegraph.exe`、按 method 转发、把进度事件转成渲染端推送

主仓那 20+ 个 `codegraph/*` 调用点（agent 工具、渲染端图表、设置页）改走子进程之后，本仓才真正被消费。

不用 submodule：版本会锁死在一个 commit 上、克隆要带 `--recursive`、CI 里 submodule 更新失败排查起来很烦。用 release 产物 + 版本号引用才对得上。

## 第三方来源

`src/runtime/WishfulClaw.CodeGraph` 是从 [`AIDotNet/CodeGraph`](https://github.com/AIDotNet/CodeGraph)（`OpenCowork.CodeGraph.Core`）vendor 进来的，保留了 global-namespace + all-internal 的原始约定。

原生解析依赖 `TreeSitter.DotNet` 1.3.0 及各语言 grammar，**各自许可证随迁移一并保留**。

## 许可证

**待定 —— LICENSE 尚未确定**（选型待决）。

本仓目前公开，但**只含骨架与语言包资源，代码本体尚未迁入**：`WishfulClaw.CodeGraph` 的源码仍留在主仓 `src/runtime/` 下原位，可执行代码一行都不在这里。

正式开放源码前，需要：

1. 定下 LICENSE（MIT / Apache-2.0 / 不开源，待决）；
2. **逐项审核第三方许可** —— 从 `AIDotNet/CodeGraph` vendor 进来的代码，以及 `TreeSitter.DotNet` 与各语言 grammar 的许可证条款，迁移时随代码一并保留，不能因为换了个仓库就以为可以重签。

在此之前，本仓的 release 资产可供 WishfulClaw 客户端下载使用，但**不构成对代码的任何授权**。
