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
| `panel` | 面板 | `web` | `${settings.panelUrl}`（默认 `http://ow.lixinrui000.cn:8080/panel`；`openExternal: true`，`allowedOrigins: ["${settings.publisherUrl}"]`） |
| `sources` | 来源与配额 | `dotnet` | `surface/Xbrd.Surface.dll`，类型 `Xbrd.Surface.XbrdSurfaceFactory` |

**2026-09 修正（A 包与集成）**：route 的 `source` 与 `allowedOrigins` 由字面量改成 `${settings.*}`
token，默认值与原来完全一致。原因：`settings.panelUrl` / `settings.publisherUrl` 已冻结为设置 key，
若 route 写死字面量，用户在设置里改地址对面板 Tab 无效（设置项变成死配置）。

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

注意 `module.json` 的 http entrypoint `baseUrl` 无法使用 token（模块清单没有设置展开机制），
改发布器地址时必须同时改那里，见第 9 节。

`surface/Xbrd.Surface.dll` 是相对 **tool.json 所在目录**（`ui/`）的路径：dev overlay 会把
`*.Surface.csproj` 的产物复制到 `ui/surface/`（见 `update-windows-dev.ps1:662`）。

## 3. Settings（key 冻结）

| key | 类型 | 默认 | 说明 |
|---|---|---|---|
| `publisherUrl` | string | `http://ow.lixinrui000.cn:8080` | 路由器发布器 |
| `panelUrl` | string | `http://ow.lixinrui000.cn:8080/panel` | 内嵌面板页 |
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

## 6. 发布契约（router API，只读，不改路由器）

- 快照：`POST {publisherUrl}/api/v1/sources/<source_id>/snapshot`
- Schema `xbrd.source.snapshot.v1`；必填 `schema, schema_version, source_id, ttl_s, status, changed_fields`
- `ttl_s` ∈ [5, 172800]；`status` ∈ `ok|degraded|error|stale`
- 本工具负责的 source id：`quota.mem`（kind `quota`，panel key `mem`）、`quota.codex`（panel key `codex`）
- **不触碰** `quota.proxy` / `quota.mes`（UI Quota Worker 发布）、`quota.glm` / `quota.deepseek` /
  `quota.lab` / `weather.minhang`（路由器 cron 发布）、`plan.smoke`（开发残留，待注销）

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
3. 工具卡片出现；`panel` Tab 内嵌路由器面板；`sources` Tab 渲染来源清单。
4. 系统 › 服务 页出现 `xbrd.mem.service` 与 `xbrd.codex-quota.service`（active）。
5. `GET /api/v1/sources` 显示 `quota.mem`、`quota.codex` 由本工具更新，且**没有**双写
   （迁移期必须先停掉对应计划任务/常驻 worker，见 README）。
6. 仓库内无明文密钥；`routerToken` 走 MPT Secret Store。
7. （2026-09 追加，A）包必须通过官方校验，两条都退出码 0：

```powershell
artifacts\sdk\cli\MyPowerTools.Cli.exe validate tools\xbrd\artifacts\package --schemas schemas
artifacts\sdk\cli\MyPowerTools.Cli.exe validate contracts tools\xbrd\artifacts\package --schemas schemas
# 期望：package: valid / contract: xbrd state=running commands=7 surfaces=4 dashboard=True settings=static-surface logs=ok
```

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
