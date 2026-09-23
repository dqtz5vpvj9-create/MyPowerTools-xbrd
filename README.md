# xbrd (MyPowerTools tool)

XBRD 圆屏的 MyPowerTools 集成：一个工具、两个 Tab。

- **面板**：内嵌路由器 **`/panel-app/`**（`source = ${settings.publisherUrl}/panel-app/`，Web Surface）——**复用手机端配额 UI**：手机端 `apps/quota-universal`（Expo/RN + react-native-web）渲染的就是同一份 `xbrd.panel.v1`，D 已把它的 web 导出部署到路由器 `/panel-app/`，MPT 侧零新增服务。验收看 hero=codex、tiles=DS/LAB/TAG/MES、strip=MEM，**不是** `{"schema":"xbrd.panel.v1"...}` 原始 JSON。`settings.panelUrl`（默认 `http://ow.lixinrui000.cn:8080/panel.json`）是**原始数据入口**（Open externally / 原始数据 / sources Tab 的「面板数据预览」诊断），**不再是面板 Tab 的 source**。路由器不可达时由 MPT 显示恢复页（Try again / Open externally）。备选方案 (ii)「本地 `http://127.0.0.1:19224/` + `xbrd.panel.service`」未采用，参数与清单保留在 [CONTRACT.md](CONTRACT.md) 第 9 节第 11 条。
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

### 改了 route/source 却看不到变化？先删 Shell 的 tool 快照（2026-09-22 实测）

**现象**：改了 `ui/tool.json` 的 route（尤其 `surface.source`），dev overlay 也跑完了，
安装目录 `%LOCALAPPDATA%\Programs\MyPowerTools\modules\xbrd\ui\tool.json` 已是新值，
但 Shell 打开页面时**仍用旧 route**（例：面板 Tab 仍加载 `.../panel.json` 而不是 `.../panel-app/`）。

**根因**：Shell 把工具描述符**持久化**在

```text
%LOCALAPPDATA%\MyPowerTools\state\shell-home-tools.v1.pb     # ShellHomeSnapshotCache，protobuf ListToolsResponse
```

Shell 启动时优先读它（`MainWindow.Startup.cs:36-57`；`--prewarm` 启动尤其明显），
而 **dev overlay 不会失效这个快照**——它只替换安装目录里的模块/服务，不动 data root 的缓存。
诊断：该文件里的描述符仍是旧 route（可用 protobuf 解析确认 `route.Source`）。

**处置**（二选一）：

```powershell
# A) 直接失效缓存后重跑 overlay（本次采用，立即生效）
Remove-Item "$env:LOCALAPPDATA\MyPowerTools\state\shell-home-tools.v1.pb" -Force
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts\Start-MyPowerTools-Dev.ps1 -Scope Tools -ToolId xbrd
# B) 让 Shell 自己去刷新工具目录（打开 All tools / 触发 RUNNER 工具目录刷新），再重新打开目标 Tab
```

注意：overlay 重启 Shell 时若缓存仍在，Shell 会先用**旧**描述符渲染并后台 reconcile；
即使 Shell 是新进程，也可能出现「新 Shell + 旧 route」。平台级建议见 CONTRACT 第 9 节第 12 条
（dev overlay 在工具包变化时主动失效该快照，本轮不改 MPT 脚本）。

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

## 自动化激活陷阱（验证用，2026-09-22 V 的方法学发现）

`--surface-activation` 是 **Shell / `MyPowerTools.Shell.Avalonia.exe`（及 `MyPowerTools.exe`）的参数**，
`MyPowerTools.Cli.exe` **没有**这个参数（传给它只会打印 help）。
用 PowerShell 的 `Start-Process -ArgumentList @('--surface-activation', $json)` 会**剥掉 JSON 里的引号**，
`ToolActivationProtocol.Deserialize` 抛 `JsonException` → 返回 `null` → 静默退化成
`ShellActivationRequest.FocusShell`：**退出码 0、窗口弹出但不导航**，看起来「成功」其实什么都没打开
（`Shell.Avalonia/Program.cs:25-38`）。正确做法二选一：

```powershell
$shell = "$env:LOCALAPPDATA\Programs\MyPowerTools\Shell\MyPowerTools.Shell.Avalonia.exe"
$json  = '{"ToolId":"xbrd","RouteId":"sources","ActivationUri":"mypowertools://activate/xbrd/sources"}'

# 1) 直接调用（PowerShell 单引号字符串，本次验证用的就是这种）
& $shell --surface-activation $json

# 2) 需要 Start-Process 时，用 ProcessStartInfo.ArgumentList（不经过引号剥离）
$psi = [System.Diagnostics.ProcessStartInfo]::new($shell)
$psi.UseShellExecute = $false
$psi.ArgumentList.Add('--surface-activation'); $psi.ArgumentList.Add($json)
[void][System.Diagnostics.Process]::Start($psi)
```

转发成功时新进程会通过命名管道把请求交给已运行的 Shell 后退出；判断是否真的导航，
不能只看退出码，要看窗口内容/子窗口（本工具的验证就是这么做的）。

## 契约自检（只读，不构建主仓）

```powershell
# 包 schema（module/tool/ui-surface）
artifacts\sdk\cli\MyPowerTools.Cli.exe validate tools\xbrd\artifacts\package --schemas schemas
# 更严格：加载 Runtime、健康检查、命令索引、UI surface、日志/settings 契约
artifacts\sdk\cli\MyPowerTools.Cli.exe validate contracts tools\xbrd\artifacts\package --schemas schemas
```

期望：`package: valid` 与 `contract: xbrd state=running commands=7 surfaces=4 dashboard=True settings=static-surface logs=ok`。

### 改 AXAML 的验收前置（2026-09-22 踩坑结论）

**凡是改动 AXAML 的提交，headless 验收必须包含至少一条「`new` 该 View + attach 对应
ViewModel/DataContext + 触发一次加载/布局」的用例**；只有 ViewModel 覆盖不算通过。

活例：`XbrdSourcesView.axaml:615`（查看快照抽屉）把
`<x:Double x:Key="MptSpacingM">12</x:Double>`（`src/MyPowerTools.UI/Themes/MptSpacing.axaml:8`）
用在了 `Margin="{DynamicResource MptSpacingM}"`，运行期抛

```
XBRD 来源与配额暂时无法加载
Unable to cast object of type 'System.Double' to type 'Avalonia.Thickness'.
```

整个 sources Tab 变成错误页，而当时 ViewModel 级 35 项用例全绿——因为异常发生在**视图加载**阶段
（style/DynamicResource 类型转换），只测 VM 根本走不到。同类风险：`Double` 误用于
`Margin`/`Padding`/`BorderThickness`、`Brush`/`Color`、`FontWeight`、`GridLength` 等 token。
详见 CONTRACT 第 9 节第 14 条。

## 命令在哪执行（为什么 tool.json 只有 2 个命令）

`ShellWorkspaceController.ExternalTools.cs` 里外部工具命令只有三条路径：`.open-external` 后缀开浏览器、
`runtime.endpoint` 是 HTTP 且命令带 `path` 时由 Shell 直接发 HTTP、其余交给 Runner 模块运行时（本工具没有）。
所以 tool.json 只声明两条能走 HTTP 的命令：

| 命令 | tool.json（Shell / web route 路径） | commands.index.json（命令面板路径） |
|---|---|---|
| `xbrd.health` | `GET ${settings.publisherUrl}/health` | `http.request` → module http entrypoint `/health` |
| `xbrd.sources.reload` | `GET ${settings.publisherUrl}/api/v1/sources` | `http.request` → module http entrypoint `/api/v1/sources` |

- **web route 的 WebBridge**：宿主支持 `command.invoke` / `settings.get` / `secrets.get` /
  `navigation.openExternal`（实现见 `ShellWorkspaceController.ExternalWebBridge.cs`），页面 origin 必须在
  `ui/tool.json` 的 `allowedOrigins` 里。但**面板 Tab 的目标页是手机端 RN-web 的静态导出（无论托管在
  路由器还是本地 unit），不认识 MPT WebBridge 协议**，所以实际不会调用 WebBridge；两条命令的入口是
  工具页/命令面板（见下条可达性）。
- **可达性（2026-09-22 复核，详见 CONTRACT 第 4 节表）**：
  - 工具页 / WebBridge / `ui/tool.json` commands：**可用**；
  - `commands.index.json` 静态索引 + 包级 `validate contracts`：**可用**（`commands=7`）；
    在**含本包的 modules root**（开发版安装布局）下 `mpt run xbrd.health` 返回
    `succeeded: HTTP 200 {...source_count:9...}`（当时 `plan.smoke` 还在；它注销后为 `source_count:8`）；
    但在仓库 `F:\repo\MyPowerTools\modules` 下会报
    `Command 'xbrd.health' was not found.`——那是该目录**没有 xbrd 包**（`build-all-tools.ps1 -ToolId xbrd`
    没跑过），不是命令实现有问题；
  - **Shell 命令面板里是否出现/可执行这两条命令 = UNCERTAIN（未实测）**；
  - `state\modules\xbrd` / transport `indexed` 对本工具不是必要条件（本模块只有 `kind: http` facade，
    没有运行时入口）。补齐方案见 CONTRACT 第 9 节第 10 条。
- 命令 id 不得以 `.refresh` / `.open-external` 结尾（web route 会过滤，
  `ExternalTools.cs`）。`remote-http` 传输本身不会被 Shell 的 palette 路径识别为 HTTP，
  所以索引里必须显式写 `http.request`。
- 需要本机进程/服务控制的动作（采集、打开验证窗口、立即发布、重启 unit、看日志）由
  `sources` Tab 的 Surface 按钮实现（B 负责），不放进 tool.json。
  所有本机进程启动必须 `UseShellExecute=false`/`CreateNoWindow=true` 或 `-NoNewWindow`（AGENTS.md）。

## 来源管理：改前 vs 改后（「立即刷新」曾经只是重读）

**改前（假管理）**：`sources` Tab 的行内按钮没有任何后端——路由器 `/api/v1/sources` 只有读，
所以：

- 「立即刷新」= 重新 `GET /api/v1/sources`（**只是重读摘要**，不会让任何采集器真的跑一次）；
- 「停用 / 注销」= 只改本地显示，路由器照旧接受该来源的快照、照旧计入 unhealthy/expired 统计；
- 「看日志」= 没有实现。

用户以为停用了某个来源，实际它还在发布——这就是 **只读 viewer 伪装成 manager**（见 CONTRACT 第 9 节第 13 条）。

**改后（真管理，动作落在生产者侧）**：

| 动作 | 落在哪里 | 接口 |
|---|---|---|
| 路由器 cron 来源：立即刷新 / 停用 / 恢复 / 看日志 | 路由器容器内真跑该来源的插件 | `POST /api/v1/sources/<id>/refresh\|disable\|enable`、`GET .../log?lines=N`（CONTRACT 6.2） |
| 无生产者残留（`plan.smoke`）：注销 | 路由器状态 + `/panel.json` | `DELETE /api/v1/sources/<id>` |
| 本工具 unit（`quota.mem` / `quota.codex`）：立即发布 | **本机回环端点，不重启进程** | `POST 127.0.0.1:19225\|19226/publish-now`、`GET /state`（CONTRACT 第 5 节；端口占用时降级为触发文件） |
| UI Quota（`quota.proxy` / `quota.mes`）：采集 / 验证窗口 | **本机动作**（UI Quota Worker / `{xbrdRepoRoot}\scripts\` 脚本） | 不经路由器控制面 |

各来源类别的管理能力矩阵（路由器自描述 `capabilities`，Surface 据此决定按钮可用性）：

| 来源类别 | 例子 | 刷新 | 停用/恢复 | 注销 | 日志 | 动作执行方 |
|---|---|---|---|---|---|---|
| 路由器 cron | `quota.glm` / `quota.deepseek` / `quota.lab` / `weather.minhang` | ✅ | ✅ | ❌（cron 会重新上报） | ✅ | 路由器 |
| 无生产者残留 | `plan.smoke` | ❌ | ❌ | ✅ | ❌ | 路由器 |
| 本工具 unit | `quota.mem` / `quota.codex` | ✅（立即发布） | 由 unit 启停 / `enable*Source` 设置 | ❌ | 工具根 JSON 心跳 / unit 日志 | 本机 unit |
| UI Quota | `quota.proxy` / `quota.mes` | ✅（本机脚本） | 本机 | ❌ | 本机 | 本机 UI Quota Worker |

两条硬规则：

1. **每个动作必须真触发生产端**（CONTRACT 第 8 节第 9 条）；只改本地显示一律判 FAIL。
2. **时间线只记状态迁移，不记「点击回声」**：`source.status/effective_status/revision/age_s` 的变化才进
   时间线；「用户点了刷新」这类交互事实不进（点击反馈留在按钮/Toast），否则排障链路会被噪声淹没。

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

### 迁移现状（2026-09-22 17:2x，Lead 已执行）

- `\XBRD\XBRD Windows MEM Source`、`\XBRD Codex Quota Worker`、`\XBRD Codex Quota Trigger`
  三个计划任务已 **Disabled**；
- Codex 的 Stop hook 已从 `~/.codex/hooks.json` 移除，备份在 `~/.codex/hooks.json.bak-xbrd-20260922`；
- 此后 `quota.mem` / `quota.codex` 只由 `xbrd.mem.service` / `xbrd.codex-quota.service` 发布；
- **回滚路径**：重新启用上述三个任务 + 从 `hooks.json.bak-xbrd-20260922` 恢复 hooks，然后停掉对应 unit。
- `\XBRD\XBRD UI Quota Worker`（quota.proxy / quota.mes）**未动**，仍按原计划运行。

### ★ `quota.codex` 有一个已知第二生产者（用户决定保留）

V 的最终验收把两笔离栅发布判为 FAIL，D 用路由器容器访问日志定案（2026-09-22）：

```
2026-09-22T12:36:37.597Z  10.33.0.171  POST /api/v1/sources/quota.codex/snapshot  200
2026-09-22T12:49:01.464Z  10.33.0.171  同上
2026-09-22T12:53:07.901Z  10.33.0.171  同上
quota.codex 写入计数：18 × 10.33.0.171（VMware 虚机 ubuntu，MAC 00:0c:29:c7:f0:91）｜3 × 10.33.0.145（本机）
```

**结论：保留那台 VM，按「已知第二生产者」处理**（不判 FAIL、不改本工具、不停 VM）。因此：

- **「单生产者」不是 `quota.codex` 的验收判据**；`quota.codex` 的 `age_s`/`revision` 可能由 VM 驱动，
  「本机没发布但 age 归零」是**预期现象，不是故障**。
- `quota.mem` 仍要求单生产者（唯一生产者 `xbrd.mem.service`，`10.33.0.145`）；
  若它出现别的 `producer.remote_addr`，按故障处理。
- 排障一律先看发布器新增的 **写入方溯源** `producer.remote_addr` / `producer.user_agent`
  （`GET /api/v1/sources` 与 `/api/v1/sources/<id>` 都带，纯增量）；明细见 CONTRACT §6.1、§8.5/§8.9、§9.15。

已知生产者清单：`quota.mem` = 本机 unit `10.33.0.145` + `Go-http-client/1.1`（唯一）；
`quota.codex` = 本机 unit 同上 + VM `10.33.0.171`；路由器 cron 四源 = `192.168.29.79`；
`quota.proxy`/`quota.mes` = 本机 UI Quota Worker `10.33.0.145` + `Python-urllib/3.12`。

### 生产者侧两条硬约束（LAB 修复中发现）

- 快照的 `error` 字段**上限 127 字节（UTF-8 字节计数）**，超限整条被拒（400
  `error must be a short string`、不落盘）——中文提示要按**字节**截断（一版 223 字节的中文错误串被拒）。
- `status=error` 的快照 **`panel_patch` 必须为空**，否则会覆盖面板上的最后正常值（`quota.lab`、TAG 已修）。
- LAB 的实现参考（会话生命周期、退出端点 `POST /api/user/auth/logout`、复用 + 单飞 + 退避）在
  `esp32-screen/docs`，此处只做指引。

### 本轮其它已交付事实（2026-09-22）

- `quota.mes` / `quota.proxy`（MES/TAG）已修：**TAG `left=411G` 解析修正**（旧实现把 total 当 left）、
  错误分类不再把 `parse_rejected` 写成 CF、tag/mes 并行 + step-gui 布局指纹缓存（**34s → 9.4–15.3s**）；
- 这两个源在路由器侧仍是**零能力**（动作在 MPT 本地：立即采集 / 打开验证窗口），与 CONTRACT §8.9 一致。

#### UI Quota Worker 调度 = 每日 03:30（2026-09-22 定稿）

用户明确要求 **MES/TAG 保持每日 03:30**（中途试过「每 4 小时」，已由 D 回滚）。实测计划任务状态：
`\XBRD\XBRD UI Quota Worker` = **1 个 Daily trigger 03:30**、`NextRunTime=2026-09-23 03:30`、
`RestartCount=3` / `RestartInterval=PT15M` 保留。

`scripts/xbrd-install-ui-quota-worker.ps1` 新增的 `-EveryHours 3|4|6` 与 `-StartAt` 是**可选**的频率开关，
**默认仍是每日 03:30**（不传即保持原样）。

为什么每日一次够用：`quota.proxy` / `quota.mes` 的 `ttl_s=172800`（**48h**），单次成功即可覆盖整个周期；
失败时任务自身重试 **3 次 × 15min**；MPT 侧还有 sources Tab 的「立即采集 / 打开验证窗口」可手动补。

#### 手机端取数：候选地址链（2026-09-22 定稿）

用户要求手机 App 改读 `http://ow.tail.lixinrui000.cn/` 以便外网（Tailscale）可用，但 11 台测试手机
**都没装 Tailscale**，tailnet `100.64.0.0/10` 对纯 LAN 客户端不可达——**单纯换默认地址会让家里没装
Tailscale 的手机也读不到**。故改为候选链（D 已实现并真机验证）：

- **Android app + 桌面小组件**：`http://ow.tail.lixinrui000.cn/panel.json`（tailnet）→
  `http://ow.lixinrui000.cn:8080/panel.json`（LAN），逐个尝试、**首个成功者生效**；
  **last-known-good** 存在共用 SharedPreferences（`xbrd_quota_widget` / `panel_source_url`），
  下次优先试它（实测 warm **2.7s** vs cold **9.2s**，省掉每次的 6s 超时）。
- **可诊断**：显示来源标签 `via tailnet` / `via lan`；全失败时错误逐条汇总
  （`fetch failed: tailnet=…; lan=…`）。
- **Web 平台优先级不变**（task-6 冻结，本轮未改，**有意保持单地址**）；
  `DEFAULT_PANEL_URL` 现指 tailnet。
- **Android 明文白名单**：`base-config` 仍 `cleartextTrafficPermitted="false"`，白名单同时含
  `ow.tail.lixinrui000.cn` 与 `ow.lixinrui000.cn`；**新增域名必须同步加白名单**，否则报
  `CLEARTEXT communication ... not permitted`。
- 版本：`apps/quota-universal` **1.0.5 / versionCode 6**；
  `scripts/xbrd-build-install-quota-app.ps1` 已修两处（USB 序列号不再误走 `adb connect`；
  非 root 时 `verify expired cache state` 标 SKIPPED）。
- **未验证项**：没有装 Tailscale 的设备，**`via tailnet` 路径未真机实测**（只证明 tailnet 端点从
  可达网络 200、APK 内地址常量与明文白名单正确）。拿到设备后需补验。详见 CONTRACT §6.3。

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
