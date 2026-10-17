# Dispatch on macOS

macOS is a supported platform for the relay service: the SMTP listener, the REST API, the dashboard and
self-update all run natively. The build + release toolchain itself is cross-platform, but a few Windows-only
integration features (the Hyper-V/VirtualBox collector and the `w32evtlog` event-log source) do not exist on
macOS and are skipped there.

This document covers what is macOS-specific: the `launchd` units, the privileged-port decision, Gatekeeper,
and the update applier. Everything else - configuration ([`SPEC.md` §12](SPEC.md#12-configuration)),
credential storage (§17.5), spool file permissions (§14.5), TLS (§17.2) - is the same contract as the Linux
install, and the cryptographic behaviour is identical on both platforms.

## What ships

**One file:** `dispatch-<version>-macos.dmg`, containing `DispatchRelay.app` and
`Uninstall DispatchRelay.app` (Apple Silicon, self-contained - no .NET SDK, no Node, no database server
needed on the box). The bundle carries `install.sh`,
`dispatch-update.sh`, the two LaunchDaemon property lists and the arm64 binaries inside it, so the app's
Install button and the command-line route run the same script rather than two implementations of it.
Both `install.sh` and `dispatch-update.sh` are written for the macOS userland: BSD tools (`dscl`, `plutil`,
`pfctl`, `socketfilterfw`, `shasum`, `codesign`, `xattr`) and no GNU-only flags.

`release.yml` stages that directory once and `installer/macos/build-app.sh` packages it; CI installs through
both routes for real on every push touching `installer/**`, so a release never attaches a file nobody booted
a service with.

Two things this choice costs, stated so nobody rediscovers them:

- **No Intel.** There is no `bin-x64` payload and no `osx-x64` key in the upgrade manifest. An Intel Mac that
  asks for an update is refused by name rather than sent the wrong binaries.
- **No `.pkg`.** A package installer is the right shape for MDM fleets and was dropped, not deprecated: its
  only advantage over the `.dmg` is a double-clickable GUI installer, and with no Developer ID to sign and
  notarize with, Gatekeeper blocks exactly that. `docs/RELEASING.md` says how to bring it back if a Developer
  account ever gets bought.

## Install

Open the `.dmg`, drag `DispatchRelay.app` to Applications, launch it (see
[Gatekeeper and signing](#gatekeeper-and-signing) for the one-time right-click > Open), then click the menu
bar item and choose **Install background service**. Enter your password once. Then **Open Dashboard** -
`https://localhost:8420`, self-signed cert - and set the admin password on the first-run screen.

### Headless, or over SSH

The same installer is inside the bundle, and that is the route for a Mac mini with no logged-in user:

```bash
hdiutil attach dispatch-<version>-macos.dmg -nobrowse
sudo "/Volumes/Dispatch"*/DispatchRelay.app/Contents/Resources/payload/install.sh \
     --admin-password '<DashboardPassword!>'
hdiutil detach "/Volumes/Dispatch"*
```

It installs the LaunchDaemon at `/Library/LaunchDaemons/app.dispatchrelay.dispatch.plist`, bootstraps it,
waits for `/health`, and reports the listening ports. Against an existing PostgreSQL/MariaDB/SQL Server
instead of the bundled SQLite:

```bash
sudo .../payload/install.sh \
     --sql-connection 'Host=...;Port=5432;Database=DispatchLog;Username=...;Password=...' \
     --admin-password '<DashboardPassword!>'
```

## Install with DispatchRelay.app (the .dmg)

```bash
# drag DispatchRelay.app into /Applications, then launch it once
open /Applications/DispatchRelay.app
# click the menu bar item -> Install background service...
# then Open Dashboard (https://localhost:8420) and set the admin password on first run
```

The app is not the relay. It is a menu bar control panel for the `LaunchDaemon` - the relay has to start at
boot before anyone logs in and run as root to bind SMTP 25, neither of which an app launched from the Dock
can do ([why](../installer/macos/app/README.md#why-the-daemon-is-not-an-app)). Install it and the daemon is
the thing that runs; quit the app and mail keeps flowing.

"Install background service" copies the payload bundled inside the app into a `0700` root-owned directory and
runs `install.sh` from there - so root never executes a script out of a folder your user account can rewrite.
If a dashboard password was set previously it is kept; a fresh machine gets the first-run setup screen,
because `install.sh` only prompts for a password when it has a terminal, and neither the app's privileged
helper nor a double-clicked installer supplies one.

## Gatekeeper and signing

The binaries are **ad-hoc signed, not notarized** - the project has no Apple Developer ID. Apple Silicon
requires *a* signature to execute a Mach-O, and the payload carries an ad-hoc one, but a `.dmg` downloaded
through a browser is quarantined and macOS refuses the first launch. `install.sh` strips
the quarantine attribute from what it installs (it cannot strip it from the disk image you mounted, which was
downloaded by your browser); if you moved the payload by hand and see *"cannot be opened because the
developer cannot be verified"*, run:

```bash
xattr -dr com.apple.quarantine <the folder>
```

`dispatch-update.sh` re-seals the apphost ad-hoc after extracting an upgrade payload, because extraction is
allowed to move bytes and a stale signature kills the process on exec.

The `.dmg` and the app inside it are **ad-hoc signed, not notarized**:
`security find-identity -v -p codesigning` reports 0 valid identities on the hosted macOS runners, and the
repository has no certificate to hand them. Nothing about Dispatch needs Screen Recording, Full Disk Access
or any other privacy permission - the relay is a daemon, and the app is a menu bar item that shells out.

## Paths

| Path | Purpose |
|---|---|
| `/opt/dispatch/` | install root, like Linux. `current` is a symlink to the active release under `releases/<version>`; `dispatch-update.sh` + the public key live here |
| `/Applications/DispatchRelay.app/Contents/Resources/payload/` | the bundle's copy of `install.sh` + the arm64 binaries. The app installs from here, so it is the file to reach for if `/opt/dispatch/install.sh` is ever missing |
| `/var/db/dispatch/` | config + data, like Linux's `/var/lib/dispatch`: `appsettings.json`, the SQLite database, the spool, the `.dispatch-key` key file, and `updates/` (staging) |
| `/var/db/dispatch/updates/` | what the service stages (`staged/<version>/`, `apply.request`) and the applier's `status.json`, which the Updates page reads |
| `/var/log/dispatch/` | `dispatch.jsonl` plus the `launchd` stdout / stderr sinks |
| `/Library/LaunchDaemons/app.dispatchrelay.dispatch.plist` | the service |
| `/Library/LaunchDaemons/app.dispatchrelay.dispatch-update.plist` | the upgrade watcher |

`ASPNETCORE_CONTENTROOT` and the job's working directory are both pinned to `/var/db/dispatch`, because the
.NET host resolves the content root from the process working directory: without the pin a `launchd` quirk
could have the service read the *shipped* `appsettings.json` inside the release directory instead of the one
the installer wrote. `DISPATCH_KEY_DIR` is pinned to the same path so the key file is always
`/var/db/dispatch/.dispatch-key`, mode 600 - a random 256-bit key, never a machine-derived one.

## Who runs it: root by default

This is the one place macOS is **worse** than Linux. The systemd unit runs unprivileged with
`CAP_NET_BIND_SERVICE`, which lets an ordinary account bind port 25. macOS has no per-process capability
grant, so a `LaunchDaemon` either runs as root or cannot bind a privileged port. Dispatch defaults to root,
and the daemon itself never calls `setuid()` - it simply is root.

To run it as an ordinary account, create one and pass it:

```bash
sudo dscl . -create /Users/_dispatch UniqueID 451
sudo dscl . -create /Users/_dispatch PrimaryGroupID -2
sudo dscl . -create /Users/_dispatch Shell /usr/bin/false
sudo dscl . -create /Users/_dispatch IsHidden 1
sudo ./install.sh --user _dispatch --admin-password '<pw>'
```

An unprivileged daemon cannot bind 25/587, so the SMTP listener falls back to **2525** and logs a warning -
the same fallback it uses on Linux when the capability is missing, so the behaviour is consistent, not a
macOS quirk. Point your senders at 2525, or leave the daemon as root and forward 25 to it. The dashboard
port (8420) and submission (587) are unprivileged either way.

The watcher daemon is always root: it has to write under `/opt/dispatch`. That separation is what keeps
in-app updates working when the service itself runs unprivileged.

## Firewall

macOS has two layers, and `install.sh` handles both. It opens inbound SMTP in the Application Firewall only
when that firewall is already enabled - enabling it yourself, system-wide, is not an installer's call. If
`pf` is already enlisted it also contributes one anchor, `/anchor/com.dispatchrelay/anchor`:

```bash
sudo pfctl -s Anchors
```

pf rules are *not* removed by `--uninstall` (same policy as Linux's firewalld permanent rules). To drop them:
`sudo pfctl -a /anchor/com.dispatchrelay/anchor -F all`.

## Managing it without the app

The menu bar app is a convenience wrapper, not the management surface - a `.app` can be dragged to the Trash,
moved to another volume, or deleted by a storage-cleanup prompt, and none of those actions should leave a root
process bound to SMTP 25 that nobody can name. So the machine carries its own controls, and always has since
install: `/opt/dispatch/install.sh` is the script the installer itself uses.

```bash
sudo /opt/dispatch/install.sh --status    # daemon state, pid, dashboard, listening SMTP ports, spool counts
sudo /opt/dispatch/install.sh --stop      # boot the daemon out (a KeepAlive job ignores launchctl stop)
sudo /opt/dispatch/install.sh --start     # enable + bootstrap, clearing any persisted disabled override
sudo /opt/dispatch/install.sh --help      # the flag list, read out of the script's own header
```

`--status` is the command to reach for when the question is "is this thing even running" and the answer must
not come from a GUI.

## Uninstall

Every route leaves the same on-disk uninstaller, and it is the one to use:

```bash
sudo /opt/dispatch/install.sh --uninstall
```

It boots out both launchd jobs, removes both property lists and `/opt/dispatch`, and deliberately keeps
`/var/db/dispatch` (database, config, spool, `.dispatch-key`) and `/var/log/dispatch` - deleting an
operator's mail history is not a side effect someone should get from the word "uninstall". If you installed
the bundle's own copy is equivalent: `sudo "/Applications/DispatchRelay.app/Contents/Resources/payload/
install.sh" --uninstall`.

Three GUI-equivalent routes, all of which run that same script:

- **The app's menu** - "Remove background service..." leaves the app in place, "Uninstall completely..." also
  deletes `/Applications/DispatchRelay.app`. Quitting the app while the daemon is still installed now says so
  out loud, because "I moved it to the Trash and mail kept flowing" is a real and frightening thing to
  discover.
- **`Uninstall DispatchRelay.app`**, the second icon in the disk image. It exists for the case the app cannot
  cover: the app is already gone, or you do not trust a menu item inside the thing you are removing. It
  carries `install.sh` and nothing else, so it is a few hundred KB, and it works whether or not
  `DispatchRelay.app` is anywhere on the machine.
- **The command line**, above.

Dragging `DispatchRelay.app` to the Trash removes none of that, and that is by design: the relay is a system
service that happens to have a control panel, not an app with a helper.

No package format has an uninstaller, so `sudo pkgutil --forget app.dispatchrelay.dispatch` only clears the
installer receipt and touches no files. `pf` rules and Application Firewall entries added by the installer are
not removed (same policy as Linux's permanent firewalld rules); see [Firewall](#firewall).

To wipe the data too, once you are sure: `sudo rm -rf /var/db/dispatch /var/log/dispatch`. Deleting
`.dispatch-key` makes the stored provider secrets unrecoverable, so keep it if you mean to reinstall.

## Self-update

The Updates page in the dashboard accepts the same cross-platform upgrade package as Linux and Windows,
because the manifest now carries `osx-arm64` and `osx-x64` payloads alongside `linux-x64`, `linux-arm64` and
`win-x64` ([`RELEASING.md`](RELEASING.md)). A macOS host picks its own architecture's payload and verifies its sha256, as on every platform.

`launchd` has no `dispatch-update.path` equivalent, so the watcher is a `WatchPaths` job: it runs
`/opt/dispatch/dispatch-update.sh` as root whenever `/var/db/dispatch/updates/apply.request` (or its parent
directory) changes. The applier re-verifies the staged manifest against the shipped public key with
`openssl` - the same fail-closed check Linux runs - and then swaps the `current` symlink, restarts the
daemon, polls `/health`, and rolls back if the new version does not come up.

Restarting is where macOS differs most from Linux. systemd's `Type=notify` means `systemctl restart` returns
only once the new version is ready, so "did the update work" is free. `launchctl kickstart` returns as soon
as it has signalled the old process, so `dispatch-update.sh` asks the application itself - polling
`/health` for up to two minutes - and that poll *is* the health gate. A release that crash-loops never
passes it and the previous symlink is restored; `KeepAlive` alone would happily restart a broken version
forever.

On a machine where you have not installed via `install.sh` yet, the first install is manual - there is no
`brew install dispatch` on the public path.

## Limitations to know about

- **Config secrets are not in the Keychain.** They are encrypted with AES-256-GCM under a mode-600 key file,
  exactly as on Linux. Keychain-backed `ProtectedData` is Windows-only, and the macOS Keychain would need a
  code identity plus keychain access, which an ad-hoc-signed daemon does not have. The protection is the
  same; only the storage differs.
- **The machine-derived key fallback is weaker on macOS than on Linux.** It is only reached when no writable
  key directory is available, but if you hit it: macOS has no `/etc/machine-id`, so the key is stretched from
  the *machine name* and a security warning is logged. The installer pins `DISPATCH_KEY_DIR`, so this only
  affects someone running the binaries by hand.
- **No notarized installer.** `DispatchRelay.app` is ad-hoc signed, so a browser download is quarantined and
  Gatekeeper refuses the first open; right-click > Open, or `xattr -dr com.apple.quarantine`. Wiring a
  Developer ID is small and worth it: set `DISPATCH_APP_SIGN_IDENTITY` (which `build-app.sh` already honours)
  to a "Developer ID Application: ..." identity in the runner keychain, add `--options runtime`, then
  `xcrun notarytool submit` + `stapler staple` the `.dmg`. The tools are all present - only the certificate
  and the notary credentials are missing. A notarized `.pkg` for MDM fleets is the other thing an account
  would unlock; see `docs/RELEASING.md`.
- **Inbound SMTP is not enabled by default.** The service binds `::` (which includes IPv4), so no sysctl or
  IPv6-only wrinkle is involved; it is simply the same "opening an inbound SMTP relay on a workstation is a
  choice, not a default" policy as on Windows.

## See also

- [`SPEC.md` §16](SPEC.md#16-linux-installation) - the Linux install this one mirrors, and the §16.7 note
  pointing here
- [`../installer/README.md`](../installer/README.md) - installer flags and troubleshooting
- [`RELEASING.md`](RELEASING.md) - how the macOS `.dmg` and its upgrade payload are built
- [`database.md`](database.md) - the bundled SQLite default, and pointing at an existing server
