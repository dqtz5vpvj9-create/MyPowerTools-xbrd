# xbrd (MyPowerTools tool)

XBRD 圆屏的 MyPowerTools 集成：一个工具、两个 Tab。

- **面板**：内嵌路由器管理页 `http://ow.lixinrui000.cn:8080/panel`（Web Surface，远程，不改路由器）。
- **来源与配额**：9 个信息来源的状态/TTL 健康、采集器状态、两个常驻服务（原生 dotnet Surface）。

接口冻结见 [CONTRACT.md](CONTRACT.md)。

## 布局

```text
tool.json                                   # 工具声明（canonical 在 modules/xbrd/ui/tool.json）
current-integration/modules/xbrd/           # 包模板（module.json + ui/*.json + settings）
current-integration/src/Xbrd.Surface/       # 来源 Tab 的 Avaonia Surface（*.Surface.csproj，dev overlay 自动 stage）
current-integration/src/Xbrd.Mem.Service/   # Service Unit: xbrd.mem.service
current-integration/src/Xbrd.CodexQuota.Service/ # Service Unit: xbrd.codex-quota.service
artifacts/package/                          # build.ps1 生成，dev overlay 读取
```

## 构建

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File .\build.ps1 `
  -MyPowerToolsRepoRoot F:\repo\MyPowerTools
```

## 开发版

工具必须以 submodule 形式位于 MPT 仓的 `tools/xbrd`（`update-windows-dev.ps1` 只认这个路径）：

```powershell
pwsh.exe -NoLogo -NoProfile -NonInteractive -File scripts/Start-MyPowerTools-Dev.ps1 `
  -Scope Tools -ToolId xbrd
```

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
