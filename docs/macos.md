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

Three artifacts, one install logic:

- `dispatch-<version>-macos.tar.gz` - one universal tarball containing both Apple Silicon (`bin-arm64`) and
  Intel (`bin-x64`) self-contained payloads, plus the installer and the launchd property lists. No .NET SDK,
  no Node, no database server needed on the target machine. The installer picks the payload matching the
  machine's architecture. `install.sh` and `dispatch-update.sh` are written for the macOS userland: BSD tools
  (`dscl`, `plutil`, `pfctl`, `socketfilterfw`, `shasum`, `codesign`, `xattr`) and no GNU-only flags.
- `dispatch-<version>-macos.pkg` - the same files in a flat package installer, for someone who would rather
  run an installer than read a README. It adds no install logic of its own: `installer(8)` runs a postinstall
  that execs the tarball's `install.sh` from the payload directory. See
  [Install with the .pkg](#install-with-the-pkg), and note that it is **unsigned**, which changes how you can
  launch it.

- `dispatch-<version>-macos.dmg` - **the recommended path on Apple Silicon.** Drag `DispatchRelay.app` into
  Applications; the app is a menu bar control panel that shows whether the daemon is running, opens the
  dashboard, and installs or removes the background service through one administrator prompt. It carries the
  same `install.sh` and the same `bin-arm64` payload inside it, so there is no second download and no second
  install logic. It is arm64-only by design, which halves the download and removes the
  "which architecture does this Mac want" question from the install path.
  See [`installer/macos/app/README.md`](../installer/macos/app/README.md) for why the service is a daemon the
  app *drives* rather than an app that *is* the service.

All three are staged by the same step in `release.yml`, and the `.pkg` and `.dmg` are assembled by
`installer/macos/build-pkg.sh` and `installer/macos/build-app.sh` - the same scripts CI installs for real on
every push touching `installer/**`, so a release never attaches an installer nobody has run. The tarball and
the `.pkg` stay universal (Intel Macs have a supported route); only the `.app` is Apple Silicon only.

## Install

If you are on Apple Silicon, use the [`.dmg`](#install-with-dispatchapp-the-dmg) - it is the least to
remember, and it is the only artifact that gives you something to see in `/Applications`. The tarball path
below is equivalent and is what the `.pkg` and the `.app` both shell out to.

```bash
sudo ./install.sh --admin-password '<DashboardPassword!>'
```

Then open `https://localhost:8420` and log in as `admin` with that password. The script installs a
LaunchDaemon at `/Library/LaunchDaemons/app.dispatchrelay.dispatch.plist`, bootstraps it, waits for
`/health`, and reports the listening ports. An existing PostgreSQL server is still supported:

```bash
sudo ./install.sh --sql-connection 'Host=...;Port=5432;Database=DispatchLog;Username=...;Password=...' \
     --admin-password '<DashboardPassword!>'
```

Two things about `launchd` are worth knowing before you install, because both surprise people coming from
systemd:

- **`launchctl stop` will not stop Dispatch.** A job with `KeepAlive` is restarted the moment it exits, so
  `stop` looks like it worked and the process comes straight back. To really stop it:

  ```bash
  sudo launchctl bootout system/app.dispatchrelay.dispatch
  ```

- **`launchctl print` is the status command.** `sudo launchctl print system/app.dispatchrelay.dispatch`
  shows the PID, the last exit code, and the standard-output / standard-error paths. The service log is
  still JSONL at `/var/log/dispatch/dispatch.jsonl` (see [`SPEC.md` §13.4](SPEC.md#134-log-file-locations));
  macOS unified logging
  carries little beyond crashes, since the service writes to files by design.

## Install with the .pkg

```bash
sudo installer -pkg dispatch-<version>-macos.pkg -target /
```

That is the command to type, and the reason this section leads with a command instead of a double-click is
[Gatekeeper](#gatekeeper-and-signing): the `.pkg` is built by CI, CI has no Apple Developer ID identity, so
the artifact is unsigned and Finder refuses to open it. `installer(8)` run from a terminal is not Gatekeeper,
and on a server it is what you would use anyway.

What the package does is small enough to state exactly. `installer(8)` copies the payload to
`/opt/dispatch/pkg` and runs `installer/macos/pkg/postinstall` as **root**, which execs that directory's
`install.sh`. Nothing else decides anything - layout, plist substitution, service account, firewall and CPU
architecture are all `install.sh`'s existing behaviour - so the `.pkg` and the tarball cannot drift. After a
successful install the postinstall deletes `/opt/dispatch/pkg/bin-arm64` and `bin-x64`, because `install.sh`
had already copied this machine's binaries into `/opt/dispatch/releases/<version>` and a self-contained
runtime is not something to keep twice, and it leaves the scripts behind so the same file still handles
reconfiguration and uninstall.

Two consequences of driving a terminal installer from a package:

- **No password is collected.** A `.pkg` has no tty to prompt on, and the distribution-style JavaScript prompt
  that would fix that is only worth its complexity once the installer is signed and notarized. So the admin
  password seed is left empty, which is a supported state rather than an accident: the dashboard shows a
  one-time first-run setup screen, and that screen is reachable without authentication *only* while no
  password hash exists. Everything under `/api/` stays 401 until you set it, so the machine is never left
  serving an open admin UI. Open `https://localhost:8420` once the package finishes and set the password.
- **To pass flags, leave a `pkg.conf`.** The postinstall sources `/opt/dispatch/pkg/pkg.conf` if it exists and
  forwards `DISPATCH_PKG_ARGS` to `install.sh`, which is how an unattended package install points at a
  database server or an unprivileged service account:

  ```bash
  printf 'DISPATCH_PKG_ARGS="--sql-connection %s"\n' "'Host=db.internal;Database=DispatchLog;Username=dispatch;Password=...'" \
    | sudo tee /opt/dispatch/pkg/pkg.conf
  ```

  Write that file *before* installing (or install, then re-run
  `sudo /opt/dispatch/pkg/install.sh <flags>` - it is idempotent and preserves the database).

Re-installing the `.pkg` over an existing host is the upgrade path for a machine that was installed with it:
`install.sh` boots the daemon out first, writes a new `releases/<version>` directory and re-points `current`.
The in-app Updates page stays the other route, and is the one that verifies a signed manifest.

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
because a `.pkg`/`.app` has no way to prompt for one.

## Gatekeeper and signing

The binaries are **ad-hoc signed, not notarized** - the project has no Apple Developer ID. Apple Silicon
requires *a* signature to execute a Mach-O, and the cross-compiled payload carries an ad-hoc one, but a
tarball downloaded through a browser is quarantined and macOS refuses the first launch. `install.sh` strips
the quarantine attribute from what it installs (it cannot strip it from the archive you extracted, which was
downloaded by your browser); if you moved the payload by hand and see *"cannot be opened because the
developer cannot be verified"*, run:

```bash
xattr -dr com.apple.quarantine <the folder>
```

`dispatch-update.sh` re-seals the apphost ad-hoc after extracting an upgrade payload, because extraction is
allowed to move bytes and a stale signature kills the process on exec. The `.pkg` inherits the same gap: it is
built unsigned, because `security find-identity -v -p codesigning` reports 0 valid identities on the hosted
macOS runners and the repository has no certificate to hand them. Nothing about Dispatch needs Screen
Recording, Full Disk Access or any other privacy permission - it is a daemon, not an app.

Wiring a Developer ID would be a small change and a real improvement, in this order: an Installer identity
available to CI (`pkgbuild --sign`, which `installer/macos/build-pkg.sh` already honours through
`DISPATCH_PKG_SIGN_IDENTITY`), then `xcrun notarytool submit` followed by `stapler staple` on the product. Only
the certificate and the notary credentials are missing - `pkgbuild`, `productbuild`, `hdiutil` and `notarytool`
are all present on the runners.

## Paths

| Path | Purpose |
|---|---|
| `/opt/dispatch/` | install root, like Linux. `current` is a symlink to the active release under `releases/<version>`; `dispatch-update.sh` + the public key live here |
| `/opt/dispatch/pkg/` | only after a `.pkg` install: the payload `install.sh` ran from. Kept so reconfiguration and `--uninstall` work without the original download; its `bin-*` directories are removed once the install succeeds |
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

## Uninstall

Every route leaves the same on-disk uninstaller, and it is the one to use:

```bash
sudo /opt/dispatch/install.sh --uninstall
```

It boots out both launchd jobs, removes both property lists and `/opt/dispatch`, and deliberately keeps
`/var/db/dispatch` (database, config, spool, `.dispatch-key`) and `/var/log/dispatch` - deleting an
operator's mail history is not a side effect someone should get from the word "uninstall". If you installed
from the tarball in a folder you still have, `sudo ./install.sh --uninstall` is the same thing. If you
installed the `.pkg` and something went far enough wrong that `/opt/dispatch/install.sh` is missing, the
payload's own copy is at `/opt/dispatch/pkg/install.sh`.

Installing through `DispatchRelay.app`? Its "Remove background service..." item calls the same script; you can
then drag the app itself to the Trash.

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
- **No notarized installer.** Neither the tarball nor the `.pkg` is signed; see "Gatekeeper and signing".
  Install the `.pkg` with `installer -pkg` from a terminal rather than by opening it.
- **Inbound SMTP is not enabled by default.** The service binds `::` (which includes IPv4), so no sysctl or
  IPv6-only wrinkle is involved; it is simply the same "opening an inbound SMTP relay on a workstation is a
  choice, not a default" policy as on Windows.

## See also

- [`SPEC.md` §16](SPEC.md#16-linux-installation) - the Linux install this one mirrors, and the §16.7 note
  pointing here
- [`../installer/README.md`](../installer/README.md) - installer flags and troubleshooting
- [`RELEASING.md`](RELEASING.md) - how the macOS tarball and its upgrade payloads are built
- [`database.md`](database.md) - the bundled SQLite default, and pointing at an existing server
