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

To demonstrate the baseline failures, `python3 prepare_baseline.py <scratch-path>`
extracts pinned revision `f39e43cf46f243e43f18781f9db3b870bdd32be0` and applies the
same internal dependency-injection seams (HTTP handler, collectors and cancellation).
It also normalizes the two historical control copies into the shared namespace after
asserting they are identical; the control and ASCII formatter retain old behavior.
Run with `-p:ProductionRoot=<scratch-path>` and a clean build when switching roots.
The baseline has no settings integration, UTF-8 payload bound or trigger-file Surface
path. Only the shared settings reader remains unused; no test calls it directly.

A harness pass does not establish native Windows/macOS UI, installation, service
supervision, production SDK runtime loading or backend integration acceptance.
