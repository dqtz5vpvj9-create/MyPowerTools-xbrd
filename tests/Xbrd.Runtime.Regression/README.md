# XBRD runtime regressions

`dotnet run --project tests/Xbrd.Runtime.Regression` from the tool root executes the
source-linked settings, publishing-payload and Surface-control regression suite.
Pass `-- --list` to enumerate cases without executing any fixture or service code;
the first line is `MPT_TEST_CASES_V1`.

Safety boundary: all collection and publisher/control HTTP are explicitly injected
inert fakes. The actual service loops run in-process as test code, not as installed
services. The resident test occupies a test-only ephemeral loopback port to exercise
the real control-plane trigger watcher. It never invokes memory P/Invoke, Codex
app-server/session discovery, a real publisher or user credentials. Temporary files
are isolated and removed. The test compiles against the real Platform.Abstractions
SDK packages, not replacement implementations. The real source-row presenter is also
linked; its owner is a throw-on-action test stand-in so no UI or HostControl is created.

The one-off baseline rewriter and comparison instructions remain available in
[commit 25d67d0](https://github.com/dqtz5vpvj9-create/MyPowerTools-xbrd/tree/25d67d08bfd31bc65a4dffd606246f3c2416afa2/tests/Xbrd.Runtime.Regression).
The maintained suite compiles the current production sources directly.

A harness pass does not establish native Windows/macOS UI, installation, service
supervision, production SDK runtime loading or backend integration acceptance.
