# XBRD 工具契约（冻结 v1）

本文件是本工具三个并行写作者（A 包与集成 / B Surface / C Service Unit）之间的**唯一接口来源**。
任何字段改名都必须先改本文件，再改代码。

## 1. 身份

| 项 | 值 |
|---|---|
| toolId | `xbrd` |
| packageId / moduleId | `xbrd` |
| title | `XBRD 屏幕` |
| category | `设备与面板` |
| type | `dotnet-surface`（**必须**，混合 web+dotnet route 的唯一可行组合） |
| primaryRouteId | `panel` |
| version | `0.1.0` |

`type` 不能改成 `web-surface`：`ShellWorkspaceController.ExternalTools.cs:128` 只在
`descriptor.ToolType == "dotnet-surface"` 时加载 dotnet route，否则第二个 Tab 会落到占位页。

## 2. Routes（两个 Tab）

| routeId | title | surface.kind | 目标 |
|---|---|---|---|
| `panel` | 面板 | `web` | `${settings.publisherUrl}/panel-app/`（`openExternal: true`，`allowedOrigins: ["${settings.publisherUrl}"]`） |
| `sources` | 来源与配额 | `dotnet` | `surface/Xbrd.Surface.dll`，类型 `Xbrd.Surface.XbrdSurfaceFactory` |

**面板 Tab 复用手机端配额 UI（2026-09-22 用户裁定；托管形态 (i) 路由器静态托管）。** 手机端
`apps/quota-universal`（Expo/RN + react-native-web）渲染的就是同一份 `xbrd.panel.v1`，本质相同就该复用；
D 把它的 web 导出部署到路由器 `/panel-app/`（`GET http://ow.lixinrui000.cn:8080/panel-app/` → 200
`text/html`，与 `/panel.json` 同源），MPT 侧**零新增服务**。此前结论「路由器没有 HTML 管理页、
面板 Tab 只能看原始 JSON」**已作废**（`GET /` 的 499 字节 stub 只是默认页）。
备选形态 (ii)「MPT 本地 `http://127.0.0.1:19224/` + `xbrd.panel.service`」的参数与完整改动清单保留在
第 9 节第 11 条，**本次未采用**。

`settings.panelUrl`（默认 `.../panel.json`）语义相应变为**原始数据入口**：用于「Open externally /
原始数据」与 sources Tab 的「面板数据预览」诊断，**不再是面板 Tab 的 source**。

**2026-09 修正（A 包与集成）**：route 的 `source` 与 `allowedOrigins` 用 `${settings.*}` token。
原因：这些 key 是冻结的设置 key，写死字面量时用户在设置里改地址对面板 Tab 无效（设置项变成死配置）。
`ToolRegistry` 在注册期展开 route 的 `surface.source`，Shell 在创建 web surface 时再展开
`allowedOrigins`（`ExternalTools.cs:289-306`）；两处共用同一个 settings 值文件。

代码依据：
- `src/MyPowerTools.Runtime/ToolRegistry.cs:217` 在**注册期**就用模块根的 `settings.json` 展开
  `runtime.endpoint` 的 `${settings.*}`；route 的 `surface.source` 同一函数里展开（`ToolRegistry.cs:165`）。
  展开失败会让整个模块加载失败，所以 `build.ps1` 校验「tool.json 里出现的每个 `${settings.x}`
  都在值文件里有非空值」。
- `ShellWorkspaceController.ExternalTools.cs:309-334`：settings 值文件缺失/读不到时，Shell 抛
  `Required tool setting '<x>' is missing. Open Settings and configure it first.`。因此 CLI 能拿到
  HTTP 200 就证明 `../settings.json` 这条 values 路径确实被解析到了。
- `ui/tool.json` 里的 settings 路径写成 `../settings.json` / `../settings.schema.json` 能工作，
  只是因为 `ToolRegistry.ResolveToolValue`（`ToolRegistry.cs:265-278`）只做
  `Path.GetFullPath(Path.Combine(toolDirectory, value))`：**当前实现没有越界（escape）检查**，
  不是文档认可的通用写法，别照抄到别的工具。

注意 `module.json` 的 http entrypoint `baseUrl` 无法使用 token（模块清单没有设置展开机制），
改发布器地址时必须同时改那里，见第 9 节。

`surface/Xbrd.Surface.dll` 是相对 **tool.json 所在目录**（`ui/`）的路径：dev overlay 会把
`*.Surface.csproj` 的产物复制到 `ui/surface/`（见 `update-windows-dev.ps1:662`）。

## 3. Settings（key 冻结）

| key | 类型 | 默认 | 说明 |
|---|---|---|---|
| `publisherUrl` | string | `http://ow.lixinrui000.cn:8080` | 路由器发布器 |
| `panelUrl` | string | `http://ow.lixinrui000.cn:8080/panel.json` | 设备**原始** panel JSON 入口（Open externally / 原始数据 / Surface 诊断）；**不再是面板 Tab 的 source**（§9.11） |
| `xbrdRepoRoot` | string | `C:\Users\lixinrui\repo\esp32-screen` | 采集脚本所在 checkout |
| `connectionTimeoutMs` | integer | `5000` | 探测超时 |
| `autoRefresh` | boolean | `true` | surface 自动刷新 |
| `enableMemSource` | boolean | `true` | 是否启用 `xbrd.mem.service` |
| `memIntervalSeconds` | integer | `300` | MEM 发布间隔 |
| `enableCodexSource` | boolean | `true` | 是否启用 `xbrd.codex-quota.service` |
| `codexIntervalSeconds` | integer | `300` | Codex 发布间隔 |

Secret（只此一个，预留）：`routerToken`。当前路由器发布 API **无鉴权**，因此 v1 允许为空；
非空时所有请求附带 `Authorization: Bearer <token>`。

### 设置文件位置（2026-09 修正，A）

| 文件 | 位置 | 作用 |
|---|---|---|
| `settings.json`（值） | 模块根 `current-integration/modules/xbrd/settings.json` | `${settings.*}` 展开来源；Shell 的 settings 读写路径 |
| `settings.schema.json` | 模块根 | 设置 key/类型/默认值/`x-mpt-secret` |
| `ui/settings.json` | `ui/` | **kind=`settings` 的 UI surface 声明**，不是值文件 |

`ui/tool.json` 用 `../settings.json` / `../settings.schema.json` 引用（相对 tool.json 所在目录）。
原因：`module.json` 的 `uiSurfaces` 里的每个路径都由 `schemas/ui-surface.schema.json` 严格校验，
把设置值放在 `ui/settings.json` 会让 `mpt validate` 失败（t0 骨架即如此）。

## 4. Commands（id 冻结）与「命令在哪执行」

**执行机制（已核实 `ExternalTools.cs:345-389`）**：外部 SDK 工具的命令只有三条路径——
`.open-external` 后缀 → 开浏览器；`runtime.endpoint` 是 HTTP(S) 且命令带 `path` → Shell 直接
发 HTTP 到 `endpoint + path`；其余情况交给 Runner 的 `ExecuteRuntimeCommandAsync`，而本工具
`runtime.transport` 不是模块运行时，**走不通**。

因此 v1 只声明两条命令，且必须能走 HTTP：

```json
"runtime": { "transport": "remote-http", "endpoint": "${settings.publisherUrl}", "timeoutMs": 8000 }
```

| id | title | 方法/路径 | 说明 |
|---|---|---|---|
| `xbrd.health` | 检查面板与来源健康 | `GET /health` | 路由器发布器健康 |
| `xbrd.sources.reload` | 重新读取来源清单 | `GET /api/v1/sources` | 来源清单 |

**2026-09 修正（A）：同一组 id 同时出现在两处，覆盖两条可达路径。**
`runtime.transport = remote-http` 只会被 `CommandIndex.ToExternalToolCommand` 当作
`tool.runtime`（它只把 `loopback-http` 识别为 HTTP），而本工具没有模块运行时，因此命令面板路径
必须在 `commands.index.json` 里显式声明 `execution.type = "http.request"`：

| 路径 | 来源 | 执行者 |
|---|---|---|
| 工具页 / 内嵌面板 WebBridge `command.invoke` | `ui/tool.json` 的 commands | Shell `InvokeExternalToolCommandAsync`：`runtime.endpoint + path` 直发 HTTP |
| 命令面板 / `mpt run` | `commands.index.json` 的 commands | Runner `MptHostRuntime.HttpRequestCommandAsync`：模块 http entrypoint `baseUrl + path` |

两处 id 相同：`CommandIndex.AddOrReplace` 让静态索引覆盖同 id 的外部工具命令，索引里不会出现重复。
`commands.index.json` 另含两条 navigation 命令：`xbrd.open.panel` → route `panel`，
`xbrd.open.sources` → route `sources`。

### 可达性真相（2026-09-22，V 实测 + A 复验修正）

V 用 CLI 实测得到「`mpt run` 找不到 xbrd、`module list` 无 xbrd、`state\modules\xbrd` 不存在」。
A 复验后确认：**前两项是 modules root 布局差异，不是执行机制问题**（A 在仓库 root 下原样复现了 V 的
三条输出，在开发版安装布局下 `mpt run xbrd.health` 成功）；第三项是预期行为。三条路径不要混为一谈：

| 路径 | 状态 | 证据 |
|---|---|---|
| 工具页 / WebBridge `command.invoke` / `ui/tool.json` commands | **可用** | 上表第 1 行；A 用 `--surface-activation` 打开两个 Tab 并截图（sources Tab 渲染 9 行来源 + 面板数据预览卡） |
| `commands.index.json` 静态索引 + 包级 `validate contracts` | **可用** | `validate contracts` → `commands=7`；在**装了本包的 modules root**（开发版 `%LOCALAPPDATA%\Programs\MyPowerTools`）下 `mpt run xbrd.health` → `succeeded: HTTP 200 {"ok":true,...,"source_count":9,...}` exit 0 |
| `mpt run` / `mpt module list` 在**仓库 `F:\repo\MyPowerTools\modules`** 下 | **不可用，但原因是布局** | 该目录里没有 xbrd 包（`build-all-tools.ps1 -ToolId xbrd` 从未把包 materialize 到那里）：`run xbrd.health` → `failed: Command 'xbrd.health' was not found.` exit 1，`module list` 无 xbrd（A 原样复现 V 的输出；V 未记录所用 modules root）。开发版布局下 `module list` 显示 `xbrd enabled stopped`（对比 screenshot/screenease 的 `enabled indexed`） |
| Shell 命令面板里是否出现/可执行 xbrd 命令 | **UNCERTAIN（未实测）** | 调色板读 Runner 命令索引，而该索引在开发版布局下**包含**静态索引命令（`mpt run` 已证）；Shell UI 是否展示、点击后走哪条执行路径需实测，不得据此断言 |
| `state\modules\xbrd` 目录 | **不存在（符合预期）** | `CreateModuleContext`（`MptHostRuntime.cs:1588-1590`）只为有**运行时入口**（inproc-dotnet / jsonrpc-stdio / grpc-ipc / package-runtime）的模块创建；`kind: http` facade 不算运行时入口 |

结论：本工具的命令走「Shell 直连 HTTP + 静态索引」路径，**不依赖模块运行时**。因此不要把
`state\modules\<id>` 或 transport `indexed` 当成这类命令的必要条件；反过来，也不要因为在仓库
`modules\` 下 `mpt run` 失败就断言命令不可用——那是包没被构建到该目录。

**不放进 tool.json 的动作**（它们需要本机进程/服务控制，HTTP 路径表达不了）由 sources Tab 的
Surface 按钮实现，归属 B：

| 动作 | 实现 |
|---|---|
| 立即采集 UI 配额 | Surface 启动 `{xbrdRepoRoot}\scripts\` 下的采集入口（venv runner），显示进度与结果 |
| 打开配额验证窗口 | Surface 启动 `{xbrdRepoRoot}\scripts\xbrd-start-ui-quota-edge.ps1` |
| 立即发布 `quota.mem` / `quota.codex` | Surface 调用对应 Service Unit（或直接触发一次发布） |
| 重启 `xbrd.mem.service` / `xbrd.codex-quota.service` | `IServiceUnitClient.RestartAsync(unitId)` |
| 查看日志 | Surface 打开日志路径 / 跳转 Logs Viewer |

所有本机进程启动必须 `UseShellExecute=false` 且 `CreateNoWindow=true`（或 `-NoNewWindow`），
不得弹控制台窗口（AGENTS.md「构建与测试不得弹出命令行窗口」）。

## 5. Service Units（id 与 manifest 冻结）

两个单元同属 `toolId: xbrd`，因此 Surface 通过
`MptAvaloniaSurfaceContext.ServiceUnits`（`ScopedServiceUnitClient`，作用域=本 toolId）
可以同时看到并启停它们（`ExternalTools.cs:190`）。

`exec` 必须写成 `bin/<exe>`：`Publish-ToolServiceUnit` 把发布产物放在
`service-units/<unitId>/bin/`，manifest 放在 `service-units/<unitId>/unit-manifest.json`，
而 `ServiceUnitCatalog.ResolvePath` 按 manifest 目录解析相对路径。

### xbrd.mem.service

```json
{
  "id": "xbrd.mem.service",
  "toolId": "xbrd",
  "displayName": "XBRD 内存来源服务",
  "exec": "bin/Xbrd.Mem.Service.exe",
  "arguments": [],
  "workingDirectory": "",
  "environment": {
    "MPT_TOOL_DATA_ROOT": "%LOCALAPPDATA%/MyPowerTools/state/tools/xbrd",
    "XBRD_PUBLISHER": "http://ow.lixinrui000.cn:8080",
    "XBRD_MEM_INTERVAL_SECONDS": "300"
  },
  "autostart": true,
  "restartPolicy": { "maxRestarts": 5, "backoffMs": 2000 },
  "readiness": { "kind": "none" },
  "stopTimeoutMs": 5000,
  "dataRoots": ["%LOCALAPPDATA%/MyPowerTools/state/tools/xbrd"],
  "dependsOn": [],
  "instanceToken": "xbrd-mem-service-v1"
}
```

### xbrd.codex-quota.service

```json
{
  "id": "xbrd.codex-quota.service",
  "toolId": "xbrd",
  "displayName": "XBRD Codex 配额服务",
  "exec": "bin/Xbrd.CodexQuota.Service.exe",
  "arguments": [],
  "workingDirectory": "",
  "environment": {
    "MPT_TOOL_DATA_ROOT": "%LOCALAPPDATA%/MyPowerTools/state/tools/xbrd",
    "XBRD_PUBLISHER": "http://ow.lixinrui000.cn:8080",
    "XBRD_CODEX_INTERVAL_SECONDS": "300"
  },
  "autostart": true,
  "restartPolicy": { "maxRestarts": 5, "backoffMs": 2000 },
  "readiness": { "kind": "none" },
  "stopTimeoutMs": 5000,
  "dataRoots": ["%LOCALAPPDATA%/MyPowerTools/state/tools/xbrd"],
  "dependsOn": [],
  "instanceToken": "xbrd-codex-quota-service-v1"
}
```

v1 用 `readiness: none`（先例：`ddns.service`）。升级到 `pipe`（`xbrd.mem.core` /
`xbrd.codex-quota.core`）留作后续，需先定业务协议。

#### 停机语义（v1，C 核实平台实现）

ServiceManager 用 `BreakawayProcessStarter`（`CREATE_BREAKAWAY_FROM_JOB |
CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW`，`BreakawayProcessStarter.cs:117`）启动 unit，
`UnitSupervisor.StopAsync` 只调用 `CloseMainWindow()`。对没有控制台窗口的进程那是空操作，
且 `CREATE_NEW_PROCESS_GROUP` 在 Win32 语义下禁用该进程组的 Ctrl+C。因此 **v1 的停机就是等
`stopTimeoutMs` 超时后强杀**：unit 既收不到 Ctrl+C，也收不到 `ProcessExit`（C 已用
`CreateProcess` + `CTRL_BREAK` 实测：unit 侧 `Console.CancelKeyPress` / `ProcessExit`
逻辑本身是好的，退出码 0；但平台路径到不了它们）。

由此对 unit 的硬性要求：**每个 tick 必须独立完成一次完整发布，不得有跨 tick 的半成品
状态**，因为进程随时可能被强杀。两个 unit 已按此实现（每 tick 重新采集并整包 POST）。
优雅停机留作后续，见第 9 节第 8 条。

#### 心跳：两份，分工不同（v1，C 实现）

每个 unit 每 tick 同时写两份心跳，路径与格式都不同，不要混用：

| 文件 | 路径 | 格式 | 用途 |
|---|---|---|---|
| **平台存活心跳** | `%MPT_DATA_ROOT%\state\<unitId>.heartbeat`（`MPT_DATA_ROOT` 缺省时回落 `%LOCALAPPDATA%\MyPowerTools`；也接受 `--heartbeat-file <path>` 覆盖） | 单行 ASCII 摘要（ISO8601 UTC + unitId + tick + status + pid，约 70 B，**覆盖写**） | `verify-release-candidate.remote.ps1` 的 `A5.R3` 只做 `Test-Path "$dataRoot\state\$unitId.heartbeat"` 判存活；`build-installer.ps1` / 系统服务页同源。**禁止**做成追加式日志 |
| **工具根 JSON 心跳** | `%MPT_TOOL_DATA_ROOT%\<unitId>.heartbeat` | 单行 UTF-8 JSON（status/revision/数值/error 等诊断字段） | Surface「来源与配额」页的诊断数据源（B 消费）；面向人排障，不是门禁契约 |

两份都必须是**小文件覆盖写**：门禁只查存在性，增长没有意义。平台心跳写失败只写 stderr，
不影响发布主流程。**必须跟随 `MPT_DATA_ROOT`**：远程门禁把 unit 装进隔离的临时 data root
（`verify-release-candidate.remote.ps1:204-207`），写死 `%LOCALAPPDATA%` 会导致 `A5.R3` FAIL。

#### 立即发布控制端点（C2，冻结接口）

Surface 的「立即发布」必须是**真触发一次采集并发布**，且**不重启 unit**（PID 不变）。

| unit | 控制端口（仅回环、仅当前用户） |
|---|---|
| `xbrd.mem.service` | `127.0.0.1:19225` |
| `xbrd.codex-quota.service` | `127.0.0.1:19226` |

| 端点 | 语义 |
|---|---|
| `POST /publish-now` | 立即执行一次该 unit 的采集+发布，返回 `{ ok, source_id, revision, duration_ms, status }`。**必须与周期 tick 互斥**：同一 unit 同时只允许一次发布，重复调用可返回 `skipped:true`。进程不重启。 |
| `GET /state` | 返回该 unit 的心跳/上次结果（与工具根 JSON 心跳同源，供 Surface 诊断） |

**降级规则（必须实现，不得崩溃）**：端口被占用或监听失败时，降级为**触发文件**
`%MPT_TOOL_DATA_ROOT%\xbrd.<unit>.trigger`（Surface 触碰 mtime 即触发一次），结果仍通过工具根
JSON 心跳回报；降级原因写日志。冻结的确切文件名（C 实现，B 只能按这两个写）：

| unit | 规范触发文件（新文件创建或 mtime 前进即触发） | 兼容别名 |
|---|---|---|
| `xbrd.mem.service` | `%MPT_TOOL_DATA_ROOT%\xbrd.mem.trigger` | `xbrd.mem.service.trigger` |
| `xbrd.codex-quota.service` | `%MPT_TOOL_DATA_ROOT%\xbrd.codex-quota.trigger` | `xbrd.codex-quota.service.trigger` |

**env**：端口用 `XBRD_CONTROL_PORT` 表达（unit manifest 的 `environment` 允许新增该键；
冻结字段 `id`/`exec`/`readiness`/`instanceToken`/`dataRoots` 不变）。**不新增 tool 设置项**：
端口是冻结常量，Surface 直接用上表地址；`ui/settings.schema.json` 本轮因此不需要新 key。
调用方可选设 `XBRD_CONTROL_PORT` 覆盖默认端口；非法值只记 stderr 并回落默认端口。

**`disabled` 语义**：`enableMemSource` / `enableCodexSource` 显式 `false` 时，unit 常驻但
`status=disabled`，**不发任何 HTTP**（不采集、不发布），`/state` 如实回报；恢复 `true` 后下一个
tick 或 `publish-now` 生效。此时 `publish-now` 返回 `{ok:true, skipped:true, status:"disabled"}`
且**不改动路由器上的快照**。

**实现细节与边界（C2，C 实现并实测）**

- 发布者唯一：周期 tick 与 `publish-now` 走**同一个循环**，控制端点只“请求再跑一轮”，因此二者
  天然不会并发重复发布。请求落在发布进行中 → `{ok:true, skipped:true, status:"in-progress"}`；
  已有排队请求 → `status:"queued"`；等待超过 30 s → `{ok:false, skipped:true, status:"timeout"}`。
- `POST /publish-now` 响应 = 冻结的 `{ok, source_id, revision, duration_ms, status}` 外加
  `skipped` / `error` / `http_status` / `pid`。`ok=true` 表示“已按请求处理”（成功发布，或按
  `disabled`/并发语义合法跳过）；发布失败或超时为 `ok=false`，细节看 `status`。
- `GET /state` 额外给出 `control`（`http`|`trigger-file`）、`port`、`trigger_file`、
  `trigger_files_watched`、`publishing`、`publish_requests`、`uptime_s`、`last`。
- 工具根 JSON 心跳新增 `control` 字段：降级时 `/state` 按定义不可达，Surface 只能靠心跳发现
  `control:"trigger-file"` 并改写触发文件。
- 监听地址固定 `IPAddress.Loopback`（绝不通配 0.0.0.0）。**诚实边界**：回环端口只保证“不跨机器”，
  同一台机器的其他本地用户仍可调用——v1 接受这一风险（端点只能触发一次公开配额数据发布，无鉴权、
  无提权面）；要严格“仅当前用户”需命名管道 + DACL 或共享 token，列为 v2。
- `--once` / `--dry-run` **不启动**控制端点（它们本来就只发布一次就退出）。

**落地状态（2026-09-22 第二段，C 更新）**：C2 已在仓库 publish 产物中实现并逐项实测通过——
回环端点（`POST /publish-now` 使 `age_s` 归零、`revision` 变化、**PID 不变**）、连续两次立即发布、
并发两次（一 `ok` 一 `skipped:true`）、端口被占用降级触发文件（stderr 记录 + `control:"trigger-file"`
+ 触发文件与别名均可用）、`disabled` 不发 HTTP。**仍未 overlay**：安装目录跑的是 19:31 的上一版
（无控制端点，19225/19226 未监听），需 A 在集成阶段 overlay + 重启这两个 unit 后，Surface 才可调用；
集成后按第 8 节复验一次。

## 6. 发布 + 控制契约（router API；发布只读，控制面增量）

### 6.1 发布（本工具与路由器 cron 共用）

- 快照：`POST {publisherUrl}/api/v1/sources/<source_id>/snapshot`
- Schema `xbrd.source.snapshot.v1`；必填 `schema, schema_version, source_id, ttl_s, status, changed_fields`
- `ttl_s` ∈ [5, 172800]；`status` ∈ `ok|degraded|error|stale`
- 本工具负责的 source id：`quota.mem`（kind `quota`，panel key `mem`）、`quota.codex`（panel key `codex`）
- **不触碰**他人的生产者：`quota.proxy` / `quota.mes`（UI Quota Worker）、`quota.glm` /
  `quota.deepseek` / `quota.lab` / `weather.minhang`（路由器 cron）、`plan.smoke`（开发残留）

### 6.2 控制面（D2 冻结接口，Surface 按此对接，不得改名）

| 方法/路径 | 语义 |
|---|---|
| `GET /api/v1/sources` | 现有只读接口；每个来源**新增** `enabled` 与 `capabilities`（自描述可用动作子集：`refresh\|disable\|log\|delete`） |
| `GET /api/v1/sources/<id>/log?lines=200` | tail 该来源的运行日志；返回 `{ ok, log_path, lines[], truncated }` |
| `POST /api/v1/sources/<id>/refresh` | **立即真跑该来源的采集插件**（不是重读摘要）；返回 `{ ok, started_at, duration_ms, exit_code, snapshot_updated, stdout_tail, error }` |
| `POST /api/v1/sources/<id>/disable` | 停用该来源（见下方语义） |
| `POST /api/v1/sources/<id>/enable` | 恢复启用 |
| `DELETE /api/v1/sources/<id>` | 注销：从状态与 panel 中移除（**仅无生产者的残留来源**） |

响应统一 `{ "ok": bool, ... }`。

**capabilities 矩阵（由发布器自描述）**

| 来源类别 | 例子 | capabilities |
|---|---|---|
| 路由器 cron | `quota.glm` / `quota.deepseek` / `quota.lab` / `weather.minhang` | `refresh` + `disable` + `log`；**不给 `delete`**（cron 会重新上报，停用才是正解） |
| 无生产者残留 | `plan.smoke` | 只有 `delete` |
| UI Quota（Windows UI Quota Worker 产出，本机处理） | `quota.proxy` / `quota.mes` | 路由器侧**不给任何能力** |
| MPT Service Unit 产出（本机处理） | `quota.mem` / `quota.codex` | 路由器侧**不给任何能力** |

**语义（逐条）**

- **`refresh` 必须真跑**：在容器内执行该来源的采集插件——就是 cron 现在 `docker exec` 的同一件事，
  读同一套 `/data/plugins/secrets/*.env`；执行后经既有 snapshot 通路更新状态。
  **并发保护**：同一来源同时只允许一个 refresh，第二次返回 `409` 或排队标记。
  **失败要如实返回**：例如 `quota.glm` 当前是 `Authentication Failed`，refresh 必须返回真实的
  `exit_code` / `error` / `stdout_tail`，**不得假装成功**。
- **`disable`**：持久化（如 `/data/source-control.json`）；此后**拒绝接受该来源的新快照**
  （HTTP 200 但 `accepted:false`），**不计入 unhealthy/expired 统计**，
  `/api/v1/sources` 里 `enabled:false` 且 `effective_status:"disabled"`，
  **panel 保留最后一次 good 值**（不能让圆屏变空）。`enable` 反向恢复。
- **`delete`**：从状态与 `/panel.json` 中移除该来源（含 panel 里对应 `quota.<key>` 条目），并清理其
  snapshot 历史引用；**只允许无生产者的残留来源**，有生产者的来源只能 `disable`。
- **`log`**：只允许白名单来源，路径限定 `/srv/iot/data/plugins/log/*-cron.log`；
  **禁止目录穿越**（越界/非白名单一律 404）。
- **兼容性**：不得改变现有路由、`xbrd.panel.v1` 契约、`/panel.json` 内容格式、
  `/api/v1/sources` 既有字段；**只做增量**。

**落地状态（2026-09-22 第一段实测）**：D2 尚未部署——`GET /api/v1/sources` 还没有
`enabled`/`capabilities`（9 个来源的字段仍是旧的），`GET /api/v1/sources/quota.glm/log?lines=3`
返回 **404**。本节是冻结目标；命中验收放集成段（第 8 节）。

## 7. 写作者所有权（不重叠）

| 所有者 | 写入范围 |
|---|---|
| A | `current-integration/modules/xbrd/**`、`build.ps1`、`tool-release.json`、`README.md`、`CONTRACT.md`、MPT 主仓 `scripts/build-all-tools.ps1` 与 `.gitmodules` |
| B | `current-integration/src/Xbrd.Surface/**` |
| C | `current-integration/src/Xbrd.Mem.Service/**`、`current-integration/src/Xbrd.CodexQuota.Service/**` |
| V（验证） | 只读；不改任何文件 |

构建与 `Start-MyPowerTools-Dev.ps1` 是全局独占资源：只有 A 在集成阶段运行。

## 8. 验收标准（v1）

1. `pwsh -File build.ps1 -MyPowerToolsRepoRoot F:\repo\MyPowerTools` 退出码 0，`artifacts/package` 就位。
2. `Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId xbrd` 成功，开发版 Shell 启动。
3. 工具卡片出现；`panel` Tab 内嵌路由器 `/panel-app/`，渲染的是**复用手机端 `quota-universal` 的配额
   UI**——验收看 hero(codex) + tiles(DS/LAB/TAG/MES) + strip(MEM)，**不是** `{"schema":"xbrd.panel.v1"...}`
   原始 JSON、不是空白、不是恢复页；路由器/页面不可达时显示 MPT 恢复页（Try again / Open externally）
   且 Shell 不崩；`sources` Tab 与「面板数据预览」卡不受影响（原始 JSON 入口见第 3 节 `panelUrl`）。
4. 系统 › 服务 页出现 `xbrd.mem.service` 与 `xbrd.codex-quota.service`（active）；
   `%LOCALAPPDATA%\MyPowerTools\state\<unitId>.heartbeat` 两份单行小文件存在并随 tick 前进。
5. `GET /api/v1/sources` 显示 `quota.mem`、`quota.codex` 的 `age_s` 由两个 unit 的 tick 驱动
   （旧生产者已于 2026-09-22 17:2x 由 Lead 停用，见 README「迁移注意」；此后不应再有第三方写入）。
6. 仓库内无明文密钥；`routerToken` 走 MPT Secret Store。
7. （2026-09 追加，A）包必须通过官方校验，两条都退出码 0：

```powershell
artifacts\sdk\cli\MyPowerTools.Cli.exe validate tools\xbrd\artifacts\package --schemas schemas
artifacts\sdk\cli\MyPowerTools.Cli.exe validate contracts tools\xbrd\artifacts\package --schemas schemas
# 期望：package: valid / contract: xbrd state=running commands=7 surfaces=4 dashboard=True settings=static-surface logs=ok
```

注意：`validate contracts` 是**包级校验器**，它自己会真打 `/health`，`state=running`、`commands=7`
只代表「包 + 静态索引 + facade 健康」成立，**不代表 Runner 已把模块登记为 transport indexed**
（见第 4 节「可达性真相」）。`state\modules\xbrd` 不存在是预期。

8. （2026-09-22，V 实测 + A 复验）命令可达性以第 4 节表为准：
   - 工具页 / WebBridge / `mpt run`（**在含本包的 modules root 下**）/ `validate contracts` 都已通过；
   - 在仓库 `F:\repo\MyPowerTools\modules` 下 `mpt run xbrd.health` 失败是**布局问题**（那里没有 xbrd 包），
     验收时不要用它判定命令实现；
   - **Shell 命令面板是否展示 xbrd 命令 = UNCERTAIN（未实测）**，不得断言可用或不可用；
   - `state\modules\xbrd` / transport `indexed` 对本工具**不是**验收条件。

9. **「管理机制」验收口径（2026-09-22，A3 重写）**：sources Tab 每一行动作都必须**真触发生产端**，
   只改本地显示或只重读摘要一律判 FAIL。按来源类别分别验：

   | 来源类别 | 动作 | 必须看到的证据 |
   |---|---|---|
   | 本工具 unit（`quota.mem` / `quota.codex`） | 行内「立即发布」 | `POST 127.0.0.1:19225\|19226/publish-now` 返回 `{ok,revision,duration_ms}`；`GET /api/v1/sources/<id>` 的 `age_s` **立刻归零**、`revision` 变化；**unit PID 不变**（证明没重启）；并发第二次返回 `skipped:true` 或 409 |
   | 路由器 cron（`quota.glm` / `quota.deepseek` / `quota.lab` / `weather.minhang`） | 「立即刷新」 | `POST /api/v1/sources/<id>/refresh` 真跑插件：返回体含 `exit_code`/`stdout_tail`；`quota.glm` 当前是认证失败，**必须如实返回失败**；`disable` 后一次快照返回 `accepted:false`、不计入 unhealthy/expired、`effective_status:"disabled"` 且 **panel 保留 last-good**；`enable` 恢复 |
   | UI Quota（`quota.proxy` / `quota.mes`） | 「采集 / 打开验证窗口」 | 动作**在本机执行**（UI Quota Worker / `{xbrdRepoRoot}\scripts\` 脚本），不经路由器控制面；路由器侧 `capabilities` 为空 |
   | 无生产者残留（`plan.smoke`） | 行内「注销」 | `DELETE /api/v1/sources/<id>` 后该来源从 `/api/v1/sources` 与 `/panel.json` 消失，`/health` 的 `source_count`/`expired_count` 相应变化 |

   另外：`panel` Tab 在上述任何操作后都不受影响（仍是 `/panel-app/` 的配额 UI）；
   `sources` Tab 的时间线**只记状态迁移**，不记「点击回声」（见第 9 节第 13 条）。

## 9. 已知约束与平台边界（2026-09，A 核实）

1. **`module.json.entrypoints` 不能为空**（`module.schema.json` 要求 `minItems: 1`）。
   xbrd 没有本机模块运行时，声明路由器发布器 HTTP facade：
   `{ "kind": "http", "priority": 80, "baseUrl": "http://ow.lixinrui000.cn:8080", "health": { "path": "/health" } }`。
   `runtimePolicy.preferred = "service"` 与 `TransportSelector.PolicyCategory`（http → service）一致。
   Runner 的 `HealthMonitor` 会对 `baseUrl + /health` 做健康检查，因此工具卡状态反映发布器可达性。
2. **`baseUrl` 是静态字面量**：模块清单没有 `${settings.*}` 展开机制。发布器地址变更时必须同时改
   `module.json` 的 `baseUrl` 与 `settings.json` 的 `publisherUrl`，否则健康检查与面板指向不同地址。
3. **命令 id 不得以 `.refresh` / `.open-external` 结尾**：web route 会过滤
   （`ShellWorkspaceController.ExternalTools.cs:47-49`）。
4. **`ui/settings.json` 是 UI surface，不是设置值**；值文件在模块根（第 3 节）。
   另：`ui/tool.json` **不声明 `dataRoots`**。`ToolRegistry.ResolveToolValue` 对非绝对 URI 只做
   `Path.Combine(toolDirectory, value)`，**不展开 `%LOCALAPPDATA%`**，写 `%LOCALAPPDATA%/...` 会得到
   `<toolDir>\%LOCALAPPDATA%\...` 这种错误路径。数据根的真实声明在两个 `unit-manifest.json`
   的 `dataRoots`（由 ServiceManager 校验）。
5. **首次引入 Service Unit 必须先让 unit 目录存在于安装布局**
   `%LOCALAPPDATA%\Programs\MyPowerTools\service-units\<unitId>`，
   否则 `update-windows-dev.ps1` 的 overlay 会抛
   `Installed service unit is missing for overlay`。处理步骤见 README「首次引入新 Service Unit 的坑」。
6. **发布/CI 工具清单已注册（2026-09，A，Lead 批准）**：
   - `scripts/publish-windows.ps1` 的 `$packageByTool`：`'xbrd' = 'xbrd'`（进 `build-provenance.json`）；
   - `scripts/build-all-tools.ps1` 的 registry：`xbrd` 条目 + 两个 `ServiceUnits`
     （`xbrd.mem.service` / `xbrd.codex-quota.service`）；
   - `scripts/verify-release-candidate.ps1`：`$expectedTools` 加 `xbrd`，
     `$expectedServiceUnits` 加两个 unit，`A5.3-loadable-surfaces` 9→10，
     `A5.7-local-runner-discovery` 列表加 `xbrd` 且计数 10→11；
   - `scripts/verify-release-candidate.remote.ps1`：`A5.R4-installed-catalog-discovery` 列表加 `xbrd`
     且计数 7→8（`A5.R2`/`A5.R3` 是动态枚举，无需改）；
   - `scripts/materialize-tool-submodules.ps1` 的 `$toolIds`、`scripts/create-source-bundle.ps1` 的
     `$toolIds`、`.github/workflows/ci.yml` 与 `scripts/ci-local/Invoke-WindowsCi.ps1` 的
     「Verify tool submodules」列表：加 `xbrd`；
   - 本仓新增 `source-map.json`（`sourceClassification: native-mypowertools-module`，
     `originalSnapshotPath: null`），满足 ci.yml / Invoke-WindowsCi 对 `tool-release.json` +
     `source-map.json` 的成对要求。
   遗留（非 xbrd 范围）：`materialize-tool-submodules.ps1` 读的是 `bundleManifest.tools.id`，
   而 `create-source-bundle.ps1` 写的是 `tools[].toolId`，且它要求每个工具在 bundle 里有
   `original-source/`（`xbrd`、`screenshot` 都没有 upstream 快照）——该脚本与当前 bundle 生产者
   对不上，A 已报告 Lead。

7. **本机动作不进 tool.json**：采集、打开验证窗口、立即发布、重启 unit、看日志需要本机进程/服务控制，
   Surface 按钮实现（B）；所有进程启动必须 `UseShellExecute=false` + `CreateNoWindow=true`
   或 `-NoNewWindow`（AGENTS.md）。

8. **Codex 配额新鲜度：v1 代理 + v2 后续项（2026-09，C，Lead 批准）**。
   `MyPowerTools.Platform.Abstractions.CodexQuotaSnapshot` 只暴露 `ResetsAt`，**没有事件时间戳**，
   因此旧 plugin「事件超过 `ttl_s` 判 stale」那一半无法直接复刻（reset 已过期那一半已复刻）。
   v1 替代实现：仅当读取走 sessions 回退（`Source == "sessions"`）时，取
   `%CODEX_HOME%\sessions\**\*.jsonl` 的最大 mtime 作为「最近一次 Codex 活动」的**上界代理**；
   超过 `ttl_s`(300s) 时仍发布数值但 `status: degraded`、`error: "no recent codex rate-limit event"`、
   `changed_fields` 只含 `quota.codex.status`；sessions 目录不可用则按 `ok` 发布并在 stdout 心跳行标
   `freshness=unknown`。app-server 路径不套该代理：RPC 返回的是账户实时状态（旧 plugin 的事件时间戳
   在那条路径上就是 now，永不 stale），套用只会把真正新鲜的数据误判为 degraded。
   **v2 后续项**：在该 record 上加 `ObservedAt`（MPT 主仓改动，非本工具写作者范围），
   即可去掉 mtime 代理。另：v1 的优雅停机（readiness `pipe` 或停机文件）也属 v2，见第 5 节。

9. **后续项：工具图标 glyph（本次不做，2026-09，Lead 决定）**。
   `ui/tool.json` 的 `icon` 是 `tool.external`，而 `ShellToolProductService.IconGlyph`
   （`src/MyPowerTools.Shell.Avalonia/Services/ShellToolProductService.cs:177-191`）只登记了
   adb-forwarder / remote-notifications / remote-commands / process-monitor / screenease /
   input-monitor / doubao-agent / smartbird-thermostat，未登记 `tool.xbrd`，于是工具卡片回退成
   title 首字母 `X屏`。若要显示专用 glyph，需要在 Shell 里加一行
   `"tool.xbrd" => "XB"` 并重编 Shell；**成本高于收益，本次不改 Shell**，仅作为后续项记录。

10. **后续项：模块级注册 / 命令面板确定可见（2026-09-22，Lead 提出，A 记录）**。
    现状（第 4 节「可达性真相」）：命令已通过 Shell/WebBridge 与静态索引两条路径可用，
    `mpt run` 在含本包的布局下也成功；缺的只是「模块被 Runner 登记为 transport indexed」与
    `state\modules\xbrd` 目录，以及 Shell 命令面板可见性尚未实测。若要补齐，两条候选：
    - **(a) 给模块加真实运行时入口**：例如一个极小的 `jsonrpc-stdio`（或 `inproc-dotnet`）命令宿主，
      把两条 HTTP 命令实现为模块命令。可获得 transport 索引 + `state\modules\xbrd` + 面板按钮。
      代价是多一个要随包发布/维护的进程与协议。
    - **(b) 用 `execution.activation` 让调色板只做导航**：`docs/sdk/commands-events-logs.md:5` 的官方模式——
      在 `commands.index.json` 里给命令加嵌套 `execution.activation = { "type": "navigation", "toolId": "xbrd",
      "routeId": "sources" }`（保留外层执行契约），调色板通用导航到 sources Tab，真正的动作仍由
      Surface 按钮/HTTP 完成。**仍需实测**「命令面板是否读取无运行时模块的静态索引」，否则等于把
      UNCERTAIN 换个位置。
    现阶段两条都不是必需项：第 4 节表里 `execution.type = "http.request"` 的静态索引路径在 `mpt run`
    下已验证可用。

11. **面板 Tab（复用手机端 quota-universal UI）的托管形态：(i) 路由器静态托管——已采用（2026-09-22）**。
    背景：手机端 `apps/quota-universal`（Expo/RN + react-native-web）渲染的就是同一份 `xbrd.panel.v1`，
    用户裁定复用；D 完成 web 导出并部署到路由器 `/panel-app/`（A 实测
    `GET http://ow.lixinrui000.cn:8080/panel-app/` 与 `http://10.33.0.1:8080/panel-app/` 均 200
    `text/html`），因此采用 (i)，MPT 侧零新增服务。下表保留两种形态的参数，**(ii) 未采用**，
    仅作为「路由器不可用/需要离线页面」时的备选（其完整改动清单见下）。

    | | (i) 路由器托管 | (ii) MPT 本地托管 |
    |---|---|---|
    | `panel` route `surface.source` | `${settings.publisherUrl}/panel-app/` | `http://127.0.0.1:19224/` |
    | `allowedOrigins` | `["${settings.publisherUrl}"]` | `["http://127.0.0.1:19224"]` |
    | `openExternal` | `true` | `true`（打开同一个本地页；原始数据仍走 `settings.panelUrl`） |
    | MPT 侧新增服务 | **无** | 新增 Service Unit `xbrd.panel.service`（HttpListener：静态页 + `/panel.json` 代理到 `{publisherUrl}`，`readiness: none`、`autostart: true`） |
    | 页面取数方式 | 同源相对 `panel.json`（`/panel-app/` 与 `/panel.json` 同源） | 同源相对 `panel.json`（由本地 unit 代理，避免跨域/路由器无 CORS） |
    | 前置条件 | 路由器可静态托管且 `/panel-app/` 可达；D 的部署脚本要能连上路由器（**当前 ~/ssh 22 不可达、`plink` 默认 22 无端口参数，D 报告阻塞**） | 端口 19224 空闲（**A 实测：当前无监听、连接被拒=可用**）；unit 首次引入需先预置安装目录（见第 9.5 条） |
    | 风险 | 部署依赖 SSH；路由器存储/权限未知 | 多一个常驻进程与端口；页面资源随包发布，版本与路由器数据解耦 |

    `settings.panelUrl` 在两种形态下都只保留「原始 JSON 入口」语义（见第 3 节）。

    **(ii) 本地托管的完整改动清单**（**本次未采用**，保留备查；由 Lead 按需派发）：
    1. 新增 `current-integration/src/Xbrd.Panel.Service/{Xbrd.Panel.Service.csproj, Program.cs, unit-manifest.json}`，
       unit id `xbrd.panel.service`，`exec: bin/Xbrd.Panel.Service.exe`，`autostart: true`，
       `readiness: { "kind": "none" }`，`stopTimeoutMs`、`dataRoots`、`instanceToken: xbrd-panel-service-v1`，
       env `XBRD_PUBLISHER`（+ 可选 `XBRD_PANEL_PORT=19224`）；控制台必须 `CreateNoWindow`（AGENTS.md）。
    2. 手机端 web 导出产物随 unit 发布（`web/` 目录），页面用**同源相对** `panel.json` 取数。
    3. `tool-release.json` 的 `serviceUnits` 追加第三个 unit。
    4. `scripts/build-all-tools.ps1` 的 xbrd `ServiceUnits` 追加该 unit（publish-windows 按
       `source-manifest.json` 动态拷贝 unit，`$packageByTool` 不用改）。
    5. `scripts/verify-release-candidate.ps1`：`$expectedServiceUnits` 追加 `xbrd.panel.service`
       （A5.1b 清单 + A5.2 关键载荷随之校验该 unit 的 manifest/exe）。
       **A5.3 / A5.7 不需要 +1**：A5.3 数的是 `payload\modules` 下的 `*.Surface.dll`，A5.7 匹配 Runner
       `--once` 输出的模块 id——新增的是 Service Unit（不是模块），两者都不受影响。
    6. `scripts/verify-release-candidate.remote.ps1`：A5.R2/A5.R3 从 `$installResult.units` 动态枚举，
       无硬编码清单；但要确认安装器把第三个 unit 一并装进隔离 data root。
    7. `ui/tool.json`：`panel` route 换成 (ii) 的两个参数（这也是第 2 节里唯一被冻结的形态差异）。
    8. `ui/settings.schema.json`：`panelUrl` 描述改为「原始 JSON 入口」；若要允许改端口，
       需新增设置 key（如 `panelLocalUrl`）并把 route source 换成 `${settings.panelLocalUrl}`，
       否则端口写死在 unit manifest 的 env 里。
    9. `commands.index.json` 的 `xbrd.open.panel` 文案（复用手机端 UI）。
    10. 首次 dev overlay 前，把 `xbrd.panel.service` 预置到
        `%LOCALAPPDATA%\Programs\MyPowerTools\service-units\xbrd.panel.service`（第 9.5 条的坑）。
    11. 文档：README「首次引入新 Service Unit 的坑」补该 unit；CONTRACT 第 5 节补它的 manifest 契约。
    12. 验证：`Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId xbrd` → 系统›服务 出现第三个 unit
        （active）+ 面板 Tab 渲染手机端配额 UI；unit 未起时显示 MPT 恢复页；`shell-faults.log` 无新增。
        `scripts/artifacts-policy.json` 无需新增条目（产物仍在 `artifacts/tools/xbrd/<v>/`）。
    **(i) 路由器托管（已采用）的实际改动清单**：① `ui/tool.json` 的 panel route 换成
    `source = ${settings.publisherUrl}/panel-app/`（title「面板」）；② `commands.index.json` 的
    `xbrd.open.panel` 文案；③ `settings.schema.json` 的 `panelUrl` 描述改为「原始 JSON 入口」。
    D 侧负责导出与 `/panel-app/` 部署；MPT 包、unit、发布清单、计数全部不变。

12. **坑 + 平台级后续项：Shell 持久化 tool 快照会掩盖 route 改动（2026-09-22 实测，A）**。
    - **现象**：改 `ui/tool.json` 的 route（尤其 `surface.source`）→ dev overlay 跑完 →
      安装目录 `%LOCALAPPDATA%\Programs\MyPowerTools\modules\xbrd\ui\tool.json` 已是新值 →
      **Shell 仍用旧 route 打开页面**（本次实测：面板 Tab 仍加载 `.../panel.json`，
      而 manifest 已指向 `.../panel-app/`）。
    - **根因**：Shell 把工具描述符持久化在
      `%LOCALAPPDATA%\MyPowerTools\state\shell-home-tools.v1.pb`
      （`ShellHomeSnapshotCache`，protobuf `ListToolsResponse`；启动读缓存的逻辑在
      `MainWindow.Startup.cs:36-57`），**dev overlay 不会失效它**——overlay 只替换安装目录的
      模块/服务，不动 data root 缓存；`--prewarm` 启动时尤其明显。
    - **处置**：改 route 后删除该 `.pb`（或让 Shell 刷新工具目录）再重跑 overlay。
      诊断方式：解析该 protobuf，看 `Route.Source` 是否还是旧值。
    - **平台级后续项（本轮不改 MPT 脚本）**：`scripts/update-windows-dev.ps1` 在工具包
      （`modules/<packageId>/**`）发生变化时，应一并失效
      `<dataRoot>\state\shell-home-tools.v1.pb`（或让 Shell 在 reconcile 后以 live tools 覆盖），
      否则任何工具改 route 的人都会看到「新 Shell + 旧 route」。

13. **设计错误与纠正：只读 viewer 伪装成 manager（2026-09-22，Lead 自述 + A 记录）**。
    背景：Lead 冻结第 6 节时只定义了**只读**发布契约，却让 Surface 的来源行提供了
    「立即刷新 / 停用」按钮——这两个按钮后端没有任何控制能力，只能重读摘要或改本地显示，
    即**假管理**（用户以为停用了来源，其实路由器还在接受它的快照）。
    纠正（本轮 D2/C2/B2 三件事）：
    - 控制面下沉到生产者：路由器加控制 API（第 6.2 节），unit 加 `publish-now`/`/state`（第 5 节）；
    - Surface 按 `capabilities` 决定动作是否可用（有能力的才给按钮，没能力的禁用并说明原因）；
    - 验收改成「每行动作必须真触发生产端」（第 8 节第 9 条）。
    **UI 原则（新增，长期有效）：严禁在时间线/事件流里记录「点击回声」**——即「用户点了刷新」这类
    交互事实不是状态，不得进时间线；时间线只记录**状态迁移**（source 的 `status`/`effective_status`/
    `revision`/`age_s` 变化）。否则时间线被操作噪声淹没，真正要排障的降级链路反而找不到。
    同理：点击的即时反馈留在按钮/Toast 上，不进日志与事件流。
