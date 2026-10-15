# Codex 额度重置邮件

额度服务复用每 300 秒一次的采集，只使用成功的 app-server 实时读数。首次观察建立基线；周期截止时间已过且更新到未来，或已用比例下降时，将两个额度窗口的变化合并为一封邮件。提前恢复会标注“提前重置或额度调整”，接口无法证明具体原因。截止时间单独提前或延后不会触发邮件。

在服务数据目录（默认 `%LOCALAPPDATA%/MyPowerTools/state/tools/xbrd`）创建 `codex-reset-mail.json`：

```json
{
  "Enabled": true,
  "Recipient": "your-address@example.com",
  "CliPath": "C:/path/to/agently-cli.exe"
}
```

需要当前服务用户已完成 Agently OAuth 登录，且 CLI 支持 `message +send --confirmed`。启用此配置表示授权向指定收件人自动发送额度变化通知。配置每次采集重新读取，无需重启服务。授权信息由 Agently 管理，仓库不保存凭据或个人邮箱。

基线与待发邮件一起原子写入 `codex-reset-mail-state.json`；失败保留待发项，在下次采集重试。初次启动、正常消耗、旧日志回退不会发信。状态损坏时保留原文件并记录失败，避免静默清空历史。禁用时设置 `Enabled` 为 false。

邮件投递与本地状态写入无法组成跨服务事务：如果邮件已发出，但 CLI 响应丢失或进程在状态落盘前退出，重试可能重复投递。关机期间的多次重置、两次采集间被消耗掩盖的提前重置，以及额度本来为零消耗且没有可见字段变化的重置，无法逐次识别。

验证：`dotnet run --project tools/xbrd/tests/QuotaResetMail/QuotaResetMail.Tests.csproj`（在父仓库根目录运行）。测试使用无效收件人及不存在的发送程序，不发送邮件。
