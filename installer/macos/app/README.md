# Dispatch.app - the macOS menu bar control panel

`Dispatch.app` is the visible half of the macOS product. The other half - the thing that actually relays
mail - is a LaunchDaemon, and the split is deliberate.

## Why the daemon is not an app

| Requirement | Why an `.app` cannot carry it |
|---|---|
| Listen on SMTP 25 / 587 | Ports below 1024 need root on macOS. There is no `CAP_NET_BIND_SERVICE` equivalent and no per-port grant, so a process running as the logged-in user cannot bind them. |
| Up at boot, before login | Printers, scanners, cron jobs, backup scripts and headless Mac minis point at this service. An app launched by a person in a GUI session is down at the login window, at logout, and during fast user switching. |
| One config, one spool, one key | A GUI app's data lives in `~/Library/Application Support`, which is per user. The daemon keeps everything in `/var/db/dispatch`. |
| Survive the terminal being closed | LaunchServices terminates apps the user quits; a relay that stops when someone closes a window loses mail. |

So `install.sh` installs `/Library/LaunchDaemons/app.dispatchrelay.dispatch.plist`, and this app does not
run the service - it finds it, reports on it, and installs or removes it.

## What the app actually does

- **Status line.** Installed = `/Library/LaunchDaemons/app.dispatchrelay.dispatch.plist` exists. Running =
  `https://localhost:8420/health` answers, which is also the readiness test: `/health` only responds once
  migrations are applied and the listeners are bound. The dashboard serves a self-signed certificate, so the
  probe accepts whatever the local server offers rather than evaluating trust - a liveness check that needs
  a CA is a liveness check that reports "stopped" on every Mac.
- **Open Dashboard.** `NSWorkspace.open`, nothing more.
- **Install / Remove background service.** One `Authorization Services` prompt, then root runs a script.
  `SMAppService` and `SMJobBless` are the modern alternatives and both want a Developer ID signature this
  project does not have; `do shell script ... with administrator privileges` needs no entitlement, no helper
  target, and no Xcode project.
- **The install script copies the payload before exec-ing it.** Root executing a shell script out of a
  user-writable app bundle would let an administrator run code as root by editing a file they already can
  edit - not an escalation, and still a hole worth avoiding for free. So the payload is copied to a
  `0700` root-owned directory under `/var/tmp`, `go-rwx`'d, and run from there. Remove prefers
  `/opt/dispatch/install.sh`, which `install.sh` itself leaves behind and root owns.

There is no second copy of the install logic here on purpose: `build-app.sh` ships `installer/macos/install.sh`
inside the bundle, so the app, the tarball and the `.pkg` are one install path in three containers.

## How it is built

`../build-app.sh <staged-dir> <version> <output.dmg>` compiles `Sources/main.swift` with `swiftc` (one file,
no package manifest, no third-party dependency), stamps `Info.plist`, and wraps the bundle in a `.dmg` with
an `Applications` symlink so installing really is a drag. It runs on the same macOS runner that builds the
`.pkg`, from the same staging directory as the tarball.

**Apple Silicon only.** That halves the download and removes "which `bin-<arch>` does this Mac want" from the
install path entirely. An Intel Mac still has the universal tarball and the `.pkg`; `build-app.sh` fails loudly
rather than quietly shipping an app whose payload has no `bin-arm64`.

## Signing: what is and is not true today

The binary and the bundle are **ad-hoc signed**. There is no Developer ID certificate and no
notarization, because the project has no Apple Developer account, so:

- A `.dmg` downloaded in a browser arrives quarantined, and Gatekeeper refuses the first open. Right-click >
  Open gets past it once; `xattr -dr com.apple.quarantine Dispatch.app` does the same from a terminal.
- `install.sh` strips quarantine from the binaries it installs and re-signs the apphost on the target
  machine, so the *daemon* is not affected by any of this. Only the app's first launch is.
- No hardened runtime options are set. With no entitlements it buys nothing here, and the daemon is spawned
  by launchd from `/opt`, not by this bundle.

Getting this to "double-click and it just opens" is a signing task, not a code task: a Developer ID
Application certificate, `codesign` with `--options runtime` and the right entitlements, `notarytool`
(`xcrun notarytool`, present on the runners) with an API key in CI secrets, and a staple. At that point
switching the privileged actions to `SMAppService` is worth doing too, because drag-to-Trash can then clean
up the daemon properly.

## Open item: the icon

`build-app.sh` will build `AppIcon.icns` from `Resources/DispatchIcon.png` if that file exists, and drop the
`CFBundleIconFile` key if it does not. The repo has no brand mark to derive one from - only screenshots - so
this ships with the generic icon until someone supplies the real thing. Deliberate: an invented placeholder
becomes the product's identity the moment it appears in a screenshot.
