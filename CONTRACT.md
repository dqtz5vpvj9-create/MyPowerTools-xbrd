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
| `panel` | 面板 | `web` | `http://ow.lixinrui000.cn:8080/panel`（`openExternal: true`，`allowedOrigins` 含该 origin） |
| `sources` | 来源与配额 | `dotnet` | `surface/Xbrd.Surface.dll`，类型 `Xbrd.Surface.XbrdSurfaceFactory` |

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

## 4. Commands（id 冻结）

**命名规则**：id 不得以 `.refresh` 或 `.open-external` 结尾——web route 会把这两个后缀的命令
从页面命令栏过滤掉（`ExternalTools.cs:47-49`）。

| id | title | 归属 | 行为 |
|---|---|---|---|
| `xbrd.health` | 检查面板与来源健康 | A（HTTP） | GET `{publisherUrl}/health` |
| `xbrd.sources.reload` | 重新读取来源清单 | A（HTTP） | GET `{publisherUrl}/api/v1/sources` |
| `xbrd.quota.collect-now` | 立即采集 UI 配额 | A（进程） | 运行 `{xbrdRepoRoot}\scripts\xbrd-ui-quota-worker-status.ps1` 的采集入口（venv runner） |
| `xbrd.quota.open-challenge` | 打开配额验证窗口 | A（进程） | `{xbrdRepoRoot}\scripts\xbrd-start-ui-quota-edge.ps1` |
| `xbrd.mem.publish-now` | 立即发布内存来源 | A | POST 一次 `quota.mem` |
| `xbrd.codex.publish-now` | 立即发布 Codex 配额 | A | POST 一次 `quota.codex` |
| `xbrd.mem.restart` | 重启内存来源服务 | B（ServiceUnits） | `IServiceUnitClient.RestartAsync("xbrd.mem.service")` |
| `xbrd.codex.restart` | 重启 Codex 配额服务 | B | `IServiceUnitClient.RestartAsync("xbrd.codex-quota.service")` |
| `xbrd.logs.tail` | 查看日志 | A | 返回采集器日志路径列表 |

`commands.index.json` 另含两条 navigation 命令：`xbrd.open.panel` → route `panel`，
`xbrd.open.sources` → route `sources`。

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
