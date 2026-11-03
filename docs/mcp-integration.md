# 接入主仓：走现成的插件体系，不造子进程管理器

> 状态：设计定稿，协议层未实现。
> 结论一句话：**CodeGraph 改成标准 MCP server，主仓用现成的 `Extension` + `McpManager` 装它。**
> 主仓不需要新写子进程管理器、协议转发器、生命周期管理 —— 这些它全都有。

---

## 一、为什么不是「主仓写子进程管理器」

最初设想的接入方式是主仓新写一层：拉起 `codegraph.exe`、按 method 转发、把进度事件转成推送。
查过之后不成立 —— 主仓早就有一整套，而且形态恰好就是 stdio 子进程。

| 环节 | 主仓现成能力 | 位置 |
|---|---|---|
| 声明 | `ExtensionManifest.mcpServers?: Record<string, ExtensionMcpServerDefinition>` | `src/shared/extension-types.ts` |
| 声明字段 | `transport?: 'stdio' \| 'sse' \| 'streamable-http'`、`command`、`args`、`env`、`cwd`、`url`、`headers`、`description` | 同上 |
| 注入 | `syncExtensionResources()` 把 manifest 里的 `mcpServers` 注册进 `McpManager` | `src/main/ipc/extension-plugin-sync.ts:238` |
| 连接 | `connectServer` / `disconnectServer` / `reconnectServer` / `disconnectAll` | `src/main/mcp/mcp-manager.ts` |
| 调用 | `callTool` / `readResource` / `getPrompt` / `refreshCapabilities` | `src/main/mcp/mcp-manager.ts` |
| 传输 | `connect` / `refreshCapabilities` / `callTool` / `readResource` / `getPrompt`，含连接超时 | `src/main/mcp/mcp-client.ts` |
| 形状 | `McpTool { name, description?, inputSchema }`、`McpServerInfo { config, status, tools, resources, prompts }` | `src/main/mcp/mcp-types.ts` |

`command + args + env + cwd` 全都支持，进程拉起和退出由 MCP 客户端自己管。
再写一套就是重复实现。

---

## 二、真正的差距：说错协议

CodeGraph 现在说的是**自定义 JSON-RPC**，主仓的 MCP 客户端说**标准 MCP**。两者握手就错开：

```
CodeGraph 现在的主循环（src/WishfulClaw.CodeGraph/Host/CodeGraphServer.cs）
  request   {"id":1,"method":"codegraph/explore","params":{...}}
  response  {"id":1,"result":...}
  error     {"id":1,"error":{"code":-32601,"message":"..."}}
  event     {"event":"codegraph/index-progress","params":{...}}     ← 无 id，reader 靠这个区分
```

MCP 客户端连上后的顺序是：

```
client → {"method":"initialize","params":{protocolVersion, capabilities, clientInfo}}
client → {"method":"notifications/initialized"}
client → {"method":"tools/list"}
client → {"method":"tools/call","params":{"name":"...","arguments":{...}}}
```

现在把 `initialize` 打到 `_handlers` 里查不到任何东西，回 `-32601`，握手失败，
server 状态直接 `error`，工具表空。**不是插件体系不够用，是它不符合要求。**

---

## 三、适配层设计（全部在本仓，主仓零代码）

`CodeGraphServer` 已经是一张 `name → handler` 表（`_handlers`，四种 `Register` 重载归一到同一形状，
`GetRegisteredMethods()` 能列出全部方法名）。补一层协议翻译即可，不用重写任何方法体。

### 3.1 协议方法映射

| MCP 方法 | 处理 | 说明 |
|---|---|---|
| `initialize` | 返回 `protocolVersion` / `capabilities: { tools: { listChanged: false } }` / `serverInfo: { name: "codegraph", version }` | 不需要 sampling、resources、prompts 能力 |
| `notifications/initialized` | 无响应 | 通知类，不回包 |
| `tools/list` | 复用 `CodeGraphToolHandler.ToolsList(...)` → `CodeGraphToolDefs.ListFor(...)` | 见第四节，**不另设计过滤策略** |
| `tools/call` | `{name, arguments}` → 短名映射回 `codegraph/<short>` → 查 `_handlers` → 结果转 `{content:[{type:"text",text}]}` | `isError` 取自 `CodeGraphToolResult.IsError` |
| `ping` | 返回空对象 | 有些客户端会探活 |

工具名映射是纯字符串操作，`CodeGraphToolDefs.ShortName()` 已经有反方向的实现：

```
codegraph_explore   →   codegraph/explore
codegraph_callers   →   codegraph/callers
```

### 3.2 事件：进度怎么出去

`index` / `sync` 是长任务，跑的时候会推 `codegraph/index-progress` 和 `codegraph/index-complete`。
现在是无 id 的裸事件，MCP 里对等的形状是 server → client 的 **notification**：

```json
{"jsonrpc":"2.0","method":"notifications/codegraph/index-progress","params":{...}}
```

保留原来的事件名和参数形状，只套一层 JSON-RPC 通知信封。
主仓那边订阅方式要跟着改（见第五节第 3 条），这是本次接入**唯一需要主仓配合协议细节**的地方。

### 3.3 错误约定保留

现有约定是「错误 RESOLVES 不 REJECT」，成功/失败建模在结果 DTO 里
（`Success` / `IsError` / `ErrorKind`）。MCP 的 `tools/call` 正好也分两层：

- 协议层错误 → JSON-RPC `error`（比如方法名不存在）
- 业务失败 → `result.isError: true` + 文本内容

现有 DTO 的语义直接落进 `isError`，方法体一行不改。

---

## 四、工具面：不新设计，复用现成的

`CodeGraphToolDefs.cs` 里已经有三段式 surface 变换，**不需要我们另外做过滤。**

| 机制 | 行为 | 位置 |
|---|---|---|
| `DefaultSurface` | 默认只列 `codegraph_explore` 一个 | `CodeGraphToolDefs.cs:28` |
| `CODEGRAPH_MCP_TOOLS` allowlist | 环境变量，逗号分隔，设了就**整体替换**默认面 | `CodeGraphToolDefs.cs:226` |
| tiny-repo gating | 索引文件数 < 500 时只列 `explore` / `search` / `node` | `CodeGraphToolDefs.cs:34-41` |
| project 感知 | 无默认项目时，每个 schema 克隆成 `projectPath` 必填 | `CodeGraphToolDefs.cs:176` |

工具定义共 **8 个**（`AllTools`，顺序对应 analysis/04 §3.1）：

```
codegraph_explore   codegraph_search    codegraph_node      codegraph_callers
codegraph_callees   codegraph_impact    codegraph_files     codegraph_status
```

> 设计过程中一度打算「只把 11 个 agent 工具放进 `tools/list`，渲染端那 7 个不列」。
> 查源码后否决 —— 上游本来就是 MCP 形态，默认面只有一个 `explore`，
> 剩下 7 个「完全可执行但不列出」。现成机制已经比那个方案更细，不要覆盖它。

**关键点：默认面之外的 14 个方法照样能 `tools/call`**，只是不进 `tools/list`。
渲染端要的结构化数据走这条路，不占 agent 的工具表。

---

## 五、22 个方法分类（依据 `CodeGraphModule.cs` 源码分区注释）

| 分区 | 方法 | 给谁 |
|---|---|---|
| 探针 | `db-smoke` | 内部自检（SQLite / FTS5 往返） |
| 生命周期 / 索引 | `index`、`sync` | agent 触发，流式推事件 |
| 工具面（agent 逐字消费的文本） | `explore`、`search`、`node`、`callers`、`callees`、`impact`、`files`、`status` | agent，**即上面 8 个工具定义** |
| 结构化 JSON（图表 / 画布） | `index-status`、`stats`、`analytics`、`prompt-context`、`query-neighbors`、`files-tree`、`file-symbols` | 渲染端可视化 |
| 设置页管理 | `list-projects`、`remove-project` | 插件设置 UI |
| 工具面元数据 | `tools-list`、`instructions` | playbook / 工具定义 |

合计 1 + 2 + 8 + 7 + 2 + 2 = 22，与 `GetRegisteredMethods()` 一致。

> 第 4 组里的 `prompt-context`、`query-neighbors`、`file-symbols` 语义上更像 agent 用的，
> 源码注释把它们归在「app visualization UI」。分类以注释为准；
> 真要放开给 agent，用 `CODEGRAPH_MCP_TOOLS` 加名字即可，不用改代码。

---

## 六、主仓侧的改动清单

### 删

| 项 | 位置 |
|---|---|
| CodeGraph 全部源码 | `src/runtime/WishfulClaw.CodeGraph/`（199 个 `.cs`） |
| 解决方案条目 | `src/runtime/WishfulClaw.sln` |
| Worker 工程引用 + `new CodeGraphModule()` | `src/runtime/WishfulClaw.Worker/`（`WorkerModuleCatalog.cs:52`） |
| 原生库解压引导 | `CodeGraphNativeLibraryResolver.Install()` |

### 加

- `extension.json`：声明 `mcpServers.codegraph`

```json
{
  "mcpServers": {
    "codegraph": {
      "transport": "stdio",
      "command": "<插件根>/bin/codegraph.exe",
      "args": [],
      "env": { "CODEGRAPH_GRAMMARS_DIR": "<dataDir>/plugins/codegraph/grammars" },
      "description": "代码图谱：索引与代码智能查询"
    }
  }
}
```

- 语言包资产沿用现有目录机制：`plugin-asset-catalog.ts` 的 `codegraph/grammars-win-x64.zip`
  （已发到 `wishful-claw-codegraph` 仓 `grammars-v1` release，4,830,638 B，18 个 dll）

### 改

1. `src/main/ipc/codegraph-handlers.ts` 的 20+ 个频道：
   `nativeWorker.request('codegraph/xxx', ...)` → `mcpManager.callTool(serverId, 'codegraph_xxx', ...)`
2. 事件订阅：`worker.onEvent('codegraph/index-progress', ...)` → MCP server notification
3. 设置页 / 图表数据源同样换成 `callTool`，返回形状不变（同一批 handler）

---

## 七、待定项

| # | 问题 | 影响 |
|---|---|---|
| 1 | `mcp-client.ts` 是否把 server → client 的 notification 透出成事件 | 索引进度条依赖它，没有就得在客户端加通道 |
| 2 | `disconnectAll` 时机 vs 索引长任务 | 索引跑到一半被 disconnect 会截断，需要确认退出流程 |
| 3 | `codegraph.exe` 分发形态 | 现在 `bin/Debug/net11.0/codegraph.exe` 只有 127 KB，是 framework-dependent；发布要定 self-contained 还是 AOT |
| 4 | 语言包路径传递 | 现在默认 `{dataDir}/plugins/codegraph/grammars`，走 MCP 后靠 `env` 传给子进程 |

---

## 八、迁移顺序

1. **本仓**：补 MCP 协议层（`initialize` / `tools/list` / `tools/call` / notification）
2. **本仓**：用标准 MCP 客户端自测 —— `initialize` → `tools/list` 应返回 1 个（默认面）
3. **主仓**：加 `extension.json` 手工装一遍，确认工具出现在 agent 工具表
4. **主仓**：切 `codegraph-handlers.ts` 的频道 + 事件订阅
5. **主仓**：删旧项目与 Worker 挂载
6. 门禁四件套 + 真机验收

## 九、验收判据

- `tools/list` 默认返回 **1 个**（`codegraph_explore`）；强制 `CODEGRAPH_MCP_TOOLS=explore,search,node` 返回 3 个
- `tools/call codegraph_explore` 返回文本正文，不是空 content
- 方法名不存在时回 JSON-RPC `error`，不是崩进程
- 索引进度事件能到渲染端，进度条走通
- 设置页图表（`index-status` / `stats` / `analytics`）数据与迁移前一致
- 未装语言包时优雅降级，不崩（S-303 真机验收项）
