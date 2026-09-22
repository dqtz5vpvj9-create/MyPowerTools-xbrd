# xbrd (MyPowerTools tool)

XBRD 圆屏的 MyPowerTools 集成：一个工具、两个 Tab。

- **面板**：内嵌路由器管理页（`settings.panelUrl`，默认 `http://ow.lixinrui000.cn:8080/panel`，Web Surface，只读，不改路由器）。
- **来源与配额**：9 个信息来源的状态/TTL 健康、采集器状态、两个常驻服务（原生 dotnet Surface）。

接口冻结见 [CONTRACT.md](CONTRACT.md)。字段名/契约改动必须先改 CONTRACT.md。

## 布局

```text
build.ps1                                        # 把包模板 stage 到 artifacts/package，并做契约自检
tool-release.json                                # MPT 主仓 scripts/build-all-tools.ps1 读取的独立构建契约
current-integration/modules/xbrd/
  module.json                                    # 模块清单（含 http entrypoint = 路由器发布器）
  commands.index.json                            # 命令索引（2 个 navigation + 2 个 http.request）
  settings.json                                  # 设置**值**（ToolRegistry 读取，`${settings.*}` 来源）
  settings.schema.json                           # 设置 schema（含 routerToken secret 声明）
  ui/tool.json                                   # 工具声明：routes / runtime / 命令
  ui/dashboard-card.json
  ui/detail-page.json
  ui/settings.json                               # ⚠ kind=settings 的 UI surface 声明，不是设置值
  ui/logs.json
  ui/surface/                                    # Surface 产物（*.Surface.csproj 由 dev overlay 自动 stage）
current-integration/src/Xbrd.Surface/            # 来源 Tab 的 Avalonia Surface（B）
current-integration/src/Xbrd.Mem.Service/        # Service Unit: xbrd.mem.service（C）
current-integration/src/Xbrd.CodexQuota.Service/ # Service Unit: xbrd.codex-quota.service（C）
artifacts/package/                               # build.ps1 生成，dev overlay 读取
```

### 包结构三条硬规则（踩过坑，别改回去）

1. **设置文件在模块根，不在 `ui/`**：`module.json` 的 `uiSurfaces` 里每个路径都会被
   `schemas/ui-surface.schema.json` 严格校验。`ui/settings.json` 必须是
   `kind: "settings"` 的 UI surface 声明；设置值放 `ui/settings.json` 会让
   `mpt validate` 直接失败。因此值文件是模块根的 `settings.json`，`ui/tool.json` 用
   `../settings.json` / `../settings.schema.json` 引用（ToolRegistry 相对 tool.json 所在目录解析）。
2. **`module.json` 的 `entrypoints` 不能为空**（`module.schema.json` 要求 `minItems: 1`）。
   本工具没有本机模块运行时，诚实的声明是路由器发布器 HTTP facade：
   `{ "kind": "http", "priority": 80, "baseUrl": "http://ow.lixinrui000.cn:8080", "health": { "path": "/health" } }`。
   Runner 会据此做 `/health` 健康检查（工具卡状态由此变为 running/degraded），
   并让 `commands.index.json` 里的 `execution.type = "http.request"` 命令真正可执行。
3. **`baseUrl` 是静态的**：`settings.publisherUrl` 改了以后要同步改 `module.json` 的 `baseUrl`，
   否则健康检查仍打旧地址（设置只影响 tool.json 的 runtime.endpoint 与面板地址）。

## 构建

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\build.ps1 `
  -MyPowerToolsRepoRoot F:\repo\MyPowerTools
```

退出码 0 即 `artifacts/package` 就位。`build.ps1` 会顺带自检（type/runtime/命令 id/命令是否进索引/
web route 与 allowedOrigins/settings 文件位置/`${settings.*}` token 是否都有值）。

## 开发版

工具必须以 submodule 形式位于 MPT 仓的 `tools/xbrd`（`update-windows-dev.ps1` 只认这个路径）：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/Start-MyPowerTools-Dev.ps1 `
  -Scope Tools -ToolId xbrd
```

### 首次引入新 Service Unit 的坑（必须知道）

`scripts/update-windows-dev.ps1` 只在**安装目录已经存在**该 unit 时才允许 overlay：

```text
%LOCALAPPDATA%\Programs\MyPowerTools\service-units\<unitId>   # 必须已存在
```

否则 Dev overlay 会抛 `Installed service unit is missing for overlay: ...`（判断在
`update-windows-dev.ps1` 的 `Publish-ToolServiceUnit`/overlay 段，unit 目录不存在即 throw）。
`xbrd.mem.service` / `xbrd.codex-quota.service` 首次引入时是手工把 unit 目录预置进安装布局的。
换机器或清理安装目录后，先建立完整安装布局并预置这两个 unit，再跑 Dev overlay：

```powershell
# 1) 完整安装布局（缺 Runtimes/service-units 时）
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\install-windows.ps1
# 2) 若 service-units 下还没有这两个目录，从本工具的构建产物预置：
#    artifacts/tools/xbrd/<version>/service-units/<unitId>/{bin, unit-manifest.json}
#    -> %LOCALAPPDATA%\Programs\MyPowerTools\service-units\<unitId>\
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\build-all-tools.ps1 -ToolId xbrd
# 3) 再 overlay
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId xbrd
```

## 契约自检（只读，不构建主仓）

```powershell
# 包 schema（module/tool/ui-surface）
artifacts\sdk\cli\MyPowerTools.Cli.exe validate tools\xbrd\artifacts\package --schemas schemas
# 更严格：加载 Runtime、健康检查、命令索引、UI surface、日志/settings 契约
artifacts\sdk\cli\MyPowerTools.Cli.exe validate contracts tools\xbrd\artifacts\package --schemas schemas
```

期望：`package: valid` 与 `contract: xbrd state=running commands=7 surfaces=4 dashboard=True settings=static-surface logs=ok`。

## 命令在哪执行（为什么 tool.json 只有 2 个命令）

`ShellWorkspaceController.ExternalTools.cs` 里外部工具命令只有三条路径：`.open-external` 后缀开浏览器、
`runtime.endpoint` 是 HTTP 且命令带 `path` 时由 Shell 直接发 HTTP、其余交给 Runner 模块运行时（本工具没有）。
所以 tool.json 只声明两条能走 HTTP 的命令：

| 命令 | tool.json（Shell/WebBridge 路径） | commands.index.json（命令面板路径） |
|---|---|---|
| `xbrd.health` | `GET ${settings.publisherUrl}/health` | `http.request` → module http entrypoint `/health` |
| `xbrd.sources.reload` | `GET ${settings.publisherUrl}/api/v1/sources` | `http.request` → module http entrypoint `/api/v1/sources` |

- **面板 Tab（WebBridge）**：内嵌页面可以调 `command.invoke`、`settings.get/set`、`secrets.get/set`、
  `navigation.openExternal`；面板页的 origin 必须在 `ui/tool.json` 的 `allowedOrigins` 里。
- **命令面板**：走 `commands.index.json` 的 `http.request`，由模块 http entrypoint 执行。
- 命令 id 不得以 `.refresh` / `.open-external` 结尾（web route 会过滤，
  `ExternalTools.cs`）。`remote-http` 传输本身不会被 Shell 的 palette 路径识别为 HTTP，
  所以索引里必须显式写 `http.request`。
- 需要本机进程/服务控制的动作（采集、打开验证窗口、立即发布、重启 unit、看日志）由
  `sources` Tab 的 Surface 按钮实现（B 负责），不放进 tool.json。
  所有本机进程启动必须 `UseShellExecute=false`/`CreateNoWindow=true` 或 `-NoNewWindow`（AGENTS.md）。

## 迁移注意（避免双跑）

启用本工具的两个 Service Unit 前，必须先停掉 esp32-screen 侧的重复生产者，否则同一
source 会被两个进程写：

```powershell
# quota.mem：停掉每 5 分钟的计划任务
Disable-ScheduledTask -TaskName 'XBRD Windows MEM Source' -TaskPath '\XBRD\'
# quota.codex：停掉常驻 worker + Stop hook + 5 分钟触发任务（三套重复发布）
Disable-ScheduledTask -TaskName 'XBRD Codex Quota Worker' -TaskPath '\'
Disable-ScheduledTask -TaskName 'XBRD Codex Quota Trigger' -TaskPath '\'
```

`quota.proxy` / `quota.mes` 仍由 `\XBRD\XBRD UI Quota Worker` 每日任务发布，本工具只做观测与手动触发，
不做迁移。

## git / 发布闭环

- 本仓（submodule）remote 为 GitHub `MyPowerTools-xbrd`；改完提交并推送，再在 MPT 主仓更新
  `tools/xbrd` 指针。
- MPT 主仓注册（均已就位）：
  - `scripts/build-all-tools.ps1` 的 `xbrd` 条目（Surface + 两个 ServiceUnit）；
    产物落在 `artifacts/tools/xbrd/<version>/`（已被 `scripts/artifacts-policy.json` 的 `tools/*/*` 覆盖）。
  - `scripts/publish-windows.ps1` 的 `$packageByTool`（`'xbrd' = 'xbrd'`，进 build-provenance）。
  - `scripts/verify-release-candidate.ps1`（`$expectedTools` / `$expectedServiceUnits` / 两个计数）、
    `scripts/verify-release-candidate.remote.ps1`（A5.R4 列表与计数）。
  - `scripts/materialize-tool-submodules.ps1`、`scripts/create-source-bundle.ps1`、
    `.github/workflows/ci.yml`、`scripts/ci-local/Invoke-WindowsCi.ps1` 的工具清单。
- 本仓 `source-map.json`（`sourceClassification: native-mypowertools-module`，
  `originalSnapshotPath: null`）满足 ci.yml / Invoke-WindowsCi 对
  `tool-release.json` + `source-map.json` 的成对要求。
- 遗留（非本工具范围）：`materialize-tool-submodules.ps1` 读 `bundleManifest.tools.id`，而
  `create-source-bundle.ps1` 写 `tools[].toolId`，且前者要求 bundle 内每个工具有 `original-source/`；
  该脚本与当前 bundle 生产者对不上，详见 CONTRACT.md 第 9 节第 6 条。
