# wishful-claw-codegraph

WishfulClaw 的代码图谱模块。从主仓 [`wishful-claw`](https://github.com/wishful-73/wishful-claw) 拆出，作为**可选插件**独立演进。

## 为什么拆

主仓的 `src/runtime/WishfulClaw.CodeGraph` 背着 **42.5 MB tree-sitter** 原生库，是安装包 108 MB 的主要来源之一。而大部分用户不写代码，代码图谱对他们没有价值。

拆成可选插件后：不装就不下载，装了按需拉取。对应主仓需求 **S-303**。

## 当前状态

**骨架，代码尚未迁入。** 主仓的 `WishfulClaw.CodeGraph` 仍在原位、仍参与构建。

迁移是主仓下一个迭代的活。本仓库先立骨架，把边界和约束写清楚 —— 免得搬的时候边搬边想。

## 要迁什么

主仓 `src/runtime/WishfulClaw.CodeGraph/`，**196 个源码文件**（195 个 `.cs` + 1 个 `.csproj`，约 5.3 万行）：

| 目录 | 职责 |
|---|---|
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

## 迁移前必须解决的三件事

### 1. 两条 `ProjectReference` 要断掉

当前 csproj 直接引用主仓：

```xml
<ProjectReference Include="..\WishfulClaw.Contracts\WishfulClaw.Contracts.csproj" />
<ProjectReference Include="..\WishfulClaw.Core\WishfulClaw.Core.csproj" />
```

实测外部耦合只有 **7 个文件 / 19 行**，但这 19 行不能靠"复制一份 Contracts"糊过去 —— 要么抽成独立的协议包，要么改成 IPC 边界。

### 2. `Storage/` 是独立库，最容易漏

15 个文件，不是语法树的一部分，剥离时容易被当成普通目录跟着搬。它有自己的 SQLite 连接工厂与 schema。

### 3. 工具调用从进程内变跨进程

现在 `CodeGraphModule` 像内置模块一样挂在 Worker catalog 里，调用是进程内的。变成插件后要走 IPC，查询延迟会上升 —— 要么做批量接口，要么接受这个代价。

## 怎么被主仓消费

按主仓 S-303 定的口径：**可选插件，主仓按需下载**。具体的插件清单格式与下载路径，迁移时再定。

不用 submodule：版本会锁死在一个 commit 上、克隆要带 `--recursive`、CI 里 submodule 更新失败排查起来很烦。这个模块的改动频率跟主仓不同步，用版本号引用才对得上。

## 第三方来源

`src/runtime/WishfulClaw.CodeGraph` 是从 [`AIDotNet/CodeGraph`](https://github.com/AIDotNet/CodeGraph)（`OpenCowork.CodeGraph.Core`）vendor 进来的，保留了 global-namespace + all-internal 的原始约定。

原生解析依赖 `TreeSitter.DotNet` 1.3.0 及各语言 grammar，**各自许可证随迁移一并保留**。

## 许可证

内部组件，暂不对外发布。
