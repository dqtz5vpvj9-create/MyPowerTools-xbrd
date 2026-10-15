using System.Text.Json;
using MyPowerTools.Platform.Abstractions;
using Xbrd.CodexQuota.Service;

var now = DateTimeOffset.UtcNow;
CodexQuotaSnapshot Snapshot(double used, DateTimeOffset reset) =>
    new(new(used, 300, reset), new(used, 10080, reset), "app-server");
void Check(bool result, string name)
{
    if (!result) throw new Exception(name);
    Console.WriteLine("PASS " + name);
}
var initial = Snapshot(60, now.AddHours(1));
Check(QuotaResetMail.Detect(initial, initial, now).Count == 0, "unchanged");
Check(QuotaResetMail.Detect(initial, Snapshot(70, now.AddHours(1)), now).Count == 0, "normal consumption");
Check(QuotaResetMail.Detect(initial, Snapshot(0, now.AddHours(1)), now).Count == 2, "early reset with unchanged deadline");
Check(QuotaResetMail.Detect(Snapshot(10, now.AddMinutes(-1)), Snapshot(20, now.AddHours(5)), now).Count == 2,
    "scheduled reset followed by consumption");
Check(QuotaResetMail.Detect(initial, Snapshot(60, now.AddHours(2)), now).Count == 0, "deadline adjustment alone");
Check(QuotaResetMail.Detect(initial, new(null, null, "app-server"), now).Count == 0, "missing windows");
Check(QuotaResetMail.Detect(initial, Snapshot(double.NaN, now.AddHours(1)), now).Count == 0, "invalid percent");

var root = Path.Combine(Path.GetTempPath(), "xbrd-mail-test-" + Guid.NewGuid());
Directory.CreateDirectory(root);
try
{
    await File.WriteAllTextAsync(Path.Combine(root, "codex-reset-mail.json"),
        JsonSerializer.Serialize(new QuotaResetMail.Config(true, "test@example.invalid", Path.Combine(root, "missing-mail.exe"))));
    var notifier = new QuotaResetMail(root);
    await notifier.ObserveAsync(initial, now, default);
    var statePath = Path.Combine(root, "codex-reset-mail-state.json");
    QuotaResetMail.State Read() => JsonSerializer.Deserialize<QuotaResetMail.State>(File.ReadAllText(statePath))!;
    Check(Read().Pending.Count == 0, "first observation does not send");
    var reset = Snapshot(0, now.AddHours(1));
    try { await notifier.ObserveAsync(reset, now.AddSeconds(1), default); } catch (System.ComponentModel.Win32Exception) { }
    Check(Read().Pending.Count == 1, "send failure preserves combined pending notice");
    try { await new QuotaResetMail(root).ObserveAsync(reset, now.AddSeconds(2), default); } catch (System.ComponentModel.Win32Exception) { }
    Check(Read().Pending.Count == 1, "restart retries without enqueueing duplicates");
}
finally { Directory.Delete(root, recursive: true); }
