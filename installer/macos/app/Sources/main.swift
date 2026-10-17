// DispatchRelay - menu bar control panel for the Dispatch SMTP relay daemon.
//
// Why this file exists at all: the product is a LaunchDaemon. It has to start at boot before anyone logs in,
// and it has to run as root to bind SMTP 25/587, because macOS has no per-process capability grant for
// privileged ports. None of that is compatible with "a normal Mac app", and the split is deliberate. What a
// daemon cannot do is be *visible*: no icon, no entry in /Applications, no answer to "is it even installed",
// which is how a working install and a dead one came to look identical from the outside. This bundle is the
// visible half - state, a way to open the dashboard, and Install/Remove buttons driving the same install.sh
// every macOS route ships.
//
// One file, compiled with swiftc by installer/macos/build-app.sh: no Xcode project, no package manifest, no
// third-party dependency, so the same macOS runner that publishes the payload builds this too.
//
// Privileged actions go through `do shell script ... with administrator privileges`, i.e. Authorization
// Services, which needs no entitlement and no helper tool. SMAppService/SMJobBless would be the alternative,
// and both want a Developer ID signature this project does not have yet; see app/README.md.
//
// The scripts it runs copy the payload into a root-owned 0700 directory before executing it. Running a shell
// script straight out of an app bundle would mean root executing a file the installing user can rewrite - not
// an escalation, since that user is an administrator already, but a hole worth avoiding for free.

import AppKit

private let daemonLabel = "app.dispatchrelay.dispatch"
private let daemonPlist = "/Library/LaunchDaemons/\(daemonLabel).plist"
private let installedScript = "/opt/dispatch/install.sh"
private let dashboardURLString = "https://localhost:8420"

// MARK: - State

private enum ServiceState {
    case notInstalled          // no LaunchDaemon plist on disk
    case installedNotRunning   // plist present, but nothing answered /health
    case running               // /health said yes

    var line: String {
        switch self {
        case .notInstalled:        return "Not installed"
        case .installedNotRunning: return "Installed - not responding"
        case .running:             return "Running"
        }
    }
}

/// The dashboard serves a self-signed certificate, so default trust evaluation must not be the gate for a
/// liveness probe. Answering at all is the fact being asked for.
private final class LocalTrust: NSObject, URLSessionDelegate {
    func urlSession(_ session: URLSession, didReceive challenge: URLAuthenticationChallenge,
                    completionHandler: @escaping (URLSession.AuthChallengeDisposition, URLCredential?) -> Void) {
        if let trust = challenge.protectionSpace.serverTrust {
            completionHandler(.useCredential, URLCredential(trust: trust))
        } else {
            completionHandler(.performDefaultHandling, nil)
        }
    }
}

private final class Model {
    private let session: URLSession

    init() {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 3
        config.timeoutIntervalForResource = 3
        session = URLSession(configuration: config, delegate: LocalTrust(), delegateQueue: nil)
    }

    var isInstalled: Bool { FileManager.default.fileExists(atPath: daemonPlist) }

    /// /health is unauthenticated by design (spec §11) and answers only once the service has finished
    /// starting - migrations applied, listeners bound - so it is both the liveness test and the ready test.
    func probe(_ completion: @escaping (ServiceState) -> Void) {
        guard isInstalled else { completion(.notInstalled); return }
        guard let url = URL(string: dashboardURLString) else { completion(.installedNotRunning); return }
        let task = session.dataTask(with: url) { data, response, _ in
            let httpOK = (response as? HTTPURLResponse)?.statusCode == 200
            let saysHealthy = data.flatMap { String(data: $0, encoding: .utf8) }?.contains("healthy") ?? false
            completion(httpOK && saysHealthy ? .running : .installedNotRunning)
        }
        task.resume()
    }
}

// MARK: - Privileged execution

private enum Privileged {
    /// Run a shell script as root through Authorization Services - the system's one password prompt. The
    /// handler is called on a background queue. osascript's stderr carries the script's own error text, which
    /// is what the menu surfaces: install.sh writes a real diagnosis there when it gives up.
    static func run(_ script: String, _ handler: @escaping (_ ok: Bool, _ text: String) -> Void) {
        DispatchQueue.global(qos: .userInitiated).async {
            // Already root? Run the script. Going through Authorization Services here would raise a prompt
            // that nobody can answer - from `sudo`, from a launchd job, or from CI - so the one environment
            // where these binaries most need to work is the one that would hang.
            if geteuid() == 0 {
                runDirectly(script, handler)
                return
            }
            let escaped = script
                .replacingOccurrences(of: "\\", with: "\\\\")
                .replacingOccurrences(of: "\"", with: "\\\"")
            // `with timeout` because a first install waits on database migrations, and the default Apple
            // Event timeout would report a failure over an install that was merely slow.
            let appleScript = "with timeout of 1800 seconds\n do shell script \"\(escaped)\" with administrator privileges\nend timeout"

            // Output goes to files, not Pipes. A Pipe plus waitUntilExit() deadlocks once the child writes
            // more than the 64 KB buffer, and install.sh printing its launchctl dump can get there; a
            // readabilityHandler would fix that and introduce a data race on the accumulating Data.
            run(URL(fileURLWithPath: "/usr/bin/osascript"), arguments: ["-e", appleScript], handler: handler)
        }
    }

    /// The same execution path with or without the authorization wrapper.
    private static func runDirectly(_ script: String, _ handler: @escaping (Bool, String) -> Void) {
        run(URL(fileURLWithPath: "/bin/sh"), arguments: ["-c", script], handler: handler)
    }

    private static func run(_ exe: URL, arguments: [String], handler: @escaping (Bool, String) -> Void) {
            let dir = FileManager.default.temporaryDirectory
            let outURL = dir.appendingPathComponent("dispatch-out-\(UUID().uuidString).txt")
            let errURL = dir.appendingPathComponent("dispatch-err-\(UUID().uuidString).txt")
            FileManager.default.createFile(atPath: outURL.path, contents: nil)
            FileManager.default.createFile(atPath: errURL.path, contents: nil)
            defer { try? FileManager.default.removeItem(at: outURL); try? FileManager.default.removeItem(at: errURL) }

            let proc = Process()
            proc.executableURL = exe
            proc.arguments = arguments
            guard let outFH = FileHandle(forWritingAtPath: outURL.path),
                  let errFH = FileHandle(forWritingAtPath: errURL.path) else {
                handler(false, "could not open temporary files to capture the installer's output")
                return
            }
            proc.standardOutput = outFH
            proc.standardError = errFH
            do {
                try proc.run()
                proc.waitUntilExit()
                outFH.closeFile()
                errFH.closeFile()
                let body = (try? Data(contentsOf: outURL)).flatMap { String(data: $0, encoding: .utf8) } ?? ""
                if proc.terminationStatus == 0 {
                    handler(true, body)
                } else {
                    let problems = (try? Data(contentsOf: errURL)).flatMap { String(data: $0, encoding: .utf8) }?
                        .trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
                    // osascript prefixes "Command failed." with no detail; the script's own stderr is the
                    // part worth showing, and install.sh writes a real diagnosis there since #52.
                    handler(false, problems.isEmpty
                        ? "the privileged command exited \(proc.terminationStatus) with no output. Details: /var/log/dispatch and /tmp/dispatch-pkg-postinstall.log"
                        : problems)
                }
            } catch {
                handler(false, "could not run \(exe.lastPathComponent): \(error)")
            }
        }
    }
}

// MARK: - Lifecycle scripts

/// The one place that knows how to remove this product. Every "uninstall" affordance - the app's menu, the
/// standalone uninstaller in the dmg, and a script run from a terminal - ends up in this text, so they cannot
/// disagree about what gets deleted and what is kept.
enum Lifecycle {
    static let installedScript = "/opt/dispatch/install.sh"
    static let mainAppPath = "/Applications/DispatchRelay.app"
    static var bundlePayload: String {
        Bundle.main.resourceURL?.appendingPathComponent("payload").path ?? ""
    }

    /// `install.sh --uninstall`, from whichever copy is reachable: the on-disk one is root-owned and is the
    /// script that actually installed this machine, so it wins. The bundle's copy is the fallback for a
    /// half-finished install, and is copied to a root-owned 0700 dir first so root never execs a file the
    /// user running the command can rewrite.
    static func uninstallScript(alsoDeleteApp: Bool) -> String {
        var script = """
        if [ -x "\(installedScript)" ]; then
          "\(installedScript)" --uninstall
        else
          set -e
          d="$(/usr/bin/mktemp -d /var/tmp/dispatch-uninstall.XXXXXX)"
          /bin/chmod 700 "$d"
          /bin/cp -R "\(bundlePayload)/." "$d/"
          /bin/chmod -R go-rwx "$d"
          trap '/bin/chmod -R u+rwX "$d" 2>/dev/null; /bin/rm -rf "$d"' EXIT
          "$d/install.sh" --uninstall
        fi
        """
        if alsoDeleteApp {
            script += "\n/bin/rm -rf "\(mainAppPath)""
        }
        return script
    }

    static let keptOnDisk =
        "Your database, config, spool and encryption key stay in /var/db/dispatch. Deleting mail history " +
        "should not be a side effect of an uninstall - remove that yourself once you are sure."
}

// MARK: - Menu controller

private final class Controller: NSObject, NSMenuDelegate {
    private let status = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
    private let model = Model()
    private let menu = NSMenu()
    private var stateItem: NSMenuItem!
    private var openItem: NSMenuItem!
    private var installItem: NSMenuItem!
    private var removeItem: NSMenuItem!
    private var uninstallItem: NSMenuItem!
    private var busy = false

    func start() {
        if let button = status.button {
            button.image = NSImage(systemSymbolName: "shippingbox", accessibilityDescription: "Dispatch Relay")
            button.imagePosition = .imageLeading
            button.title = " Dispatch Relay"
        }
        menu.delegate = self
        menu.autoenablesItems = false

        stateItem = item("Checking...", nil, "", target: nil)
        openItem = item("Open Dashboard", #selector(openDashboard), "d", target: self)
        installItem = item("Install background service...", #selector(installTapped), "", target: self)
        removeItem = item("Remove background service...", #selector(removeTapped), "", target: self)
        // The difference between "remove the service" and this is everything a user who is leaving the
        // product would otherwise have to remember: that the daemon is a LaunchDaemon, not this app, and
        // that moving the app to the Trash leaves a root process bound to SMTP 25 running. So say it here.
        uninstallItem = item("Uninstall completely...", #selector(uninstallEverythingTapped), "", target: self)
        let refreshItem = item("Refresh", #selector(refresh), "r", target: self)
        let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "?"

        menu.addItem(stateItem)
        menu.addItem(.separator())
        menu.addItem(openItem)
        menu.addItem(installItem)
        menu.addItem(removeItem)
        menu.addItem(uninstallItem)
        menu.addItem(.separator())
        menu.addItem(refreshItem)
        menu.addItem(item("Dispatch Relay \(version)", nil, "", target: nil))
        menu.addItem(.separator())
        menu.addItem(item("Quit", #selector(NSApplication.terminate(_:)), "q", target: nil))
        status.menu = menu

        refresh()
        // A slow first start (migrations over a big spool) must not leave a stale "not responding" showing
        // until somebody happens to open the menu.
        Timer.scheduledTimer(withTimeInterval: 20, repeats: true) { [weak self] _ in self?.refresh() }
    }

    /// Items need an explicit target: with autoenablesItems off there is no responder-chain walk, and a
    /// status-bar menu has no window to route through anyway.
    private func item(_ title: String, _ action: Selector?, _ key: String, target: AnyObject?) -> NSMenuItem {
        let i = NSMenuItem(title: title, action: action, keyEquivalent: key)
        if action != nil { i.target = target } else { i.isEnabled = false }
        return i
    }

    // Re-probe whenever the menu opens, so the status line is current rather than up to 20s old.
    func menuNeedsUpdate(_ menu: NSMenu) { refresh() }

    @objc private func refresh() {
        model.probe { state in DispatchQueue.main.async { self.apply(state) } }
    }

    private func apply(_ state: ServiceState) {
        guard !busy else { return }
        stateItem.title = "Status: " + state.line
        openItem.isEnabled = (state == .running)
        installItem.isEnabled = (state == .notInstalled)
        removeItem.isEnabled = (state != .notInstalled)
        uninstallItem.isEnabled = (state != .notInstalled)
    }

    @objc private func openDashboard() {
        if let url = URL(string: dashboardURLString) { NSWorkspace.shared.open(url) }
    }

    @objc private func installTapped() {
        guard let payload = Bundle.main.resourceURL?.appendingPathComponent("payload").path else {
            fail("Install", "the app bundle carries no payload directory; it was built wrong")
            return
        }
        // install.sh is run from the copy, not from the bundle, and --prebuilt points inside the copy so it
        // never sees a path the bundle could have influenced after this call began.
        perform("""
        set -e
        d="$(/usr/bin/mktemp -d /var/tmp/dispatch-install.XXXXXX)"
        /bin/chmod 700 "$d"
        /bin/cp -R "\(payload)/." "$d/"
        /bin/chmod -R go-rwx "$d"
        trap '/bin/chmod -R u+rwX "$d" 2>/dev/null; /bin/rm -rf "$d"' EXIT
        "$d/install.sh" --prebuilt "$d/bin-arm64"
        """, doing: "Installing the background service")
    }

    /// For the quit dialog, which cannot route through a menu item's target.
    func removeServiceNow() { removeTapped() }
    func uninstallEverythingNow() { uninstallEverythingTapped() }

    @objc private func removeTapped() {
        // The same script the standalone uninstaller and the CLI run, minus deleting the app bundle: someone
        // using this menu item has not asked to lose the menu.
        perform(Lifecycle.uninstallScript(alsoDeleteApp: false), doing: "Removing the background service")
    }

    @objc private func uninstallEverythingTapped() {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "Remove the Dispatch service and this app?"
        alert.informativeText = "The daemon, both launchd jobs, /opt/dispatch and " +
            "\(Lifecycle.mainAppPath) are deleted. " + Lifecycle.keptOnDisk
        alert.addButton(withTitle: "Uninstall everything")
        alert.addButton(withTitle: "Cancel")
        guard alert.runModal() == .alertFirstButtonReturn else { return }
        perform(Lifecycle.uninstallScript(alsoDeleteApp: true), doing: "Uninstalling Dispatch")
        // The bundle deletes itself as part of that script, so staying alive would be the odd behaviour.
        NSApp.terminate(nil)
    }

    /// Run a privileged script with a completion handler that has to survive the caller's run loop.
    private func performAndWait(_ script: String) -> (Bool, String) {
        let done = DispatchSemaphore(value: 0)
        var result = (false, "")
        Privileged.run(script) { ok, text in
            result = (ok, text)
            done.signal()
        }
        done.wait()
        return result
    }

    private func perform(_ script: String, doing verb: String) {
        busy = true
        stateItem.title = "\(verb)..."
        openItem.isEnabled = false; installItem.isEnabled = false; removeItem.isEnabled = false
        Privileged.run(script) { ok, text in
            DispatchQueue.main.async {
                self.busy = false
                self.refresh()
                if !ok { self.fail(verb, text) }
            }
        }
    }

    private func fail(_ verb: String, _ message: String) {
        let alert = NSAlert()
        alert.alertStyle = .warning
        alert.messageText = "\(verb) failed"
        alert.informativeText = message
        alert.addButton(withTitle: "Copy details")
        alert.addButton(withTitle: "OK")
        if alert.runModal() == .alertFirstButtonReturn {
            NSPasteboard.general.clearContents()
            NSPasteboard.general.setString(message, forType: .string)
        }
        refresh()
    }
}

/// Privileged.run reports asynchronously; the command-line paths and the uninstaller have no run loop to
/// wait on, so they block on a semaphore instead. Safe because they never call it from the main queue.
private func runPrivilegedSync(_ script: String) -> (Bool, String) {
    let done = DispatchSemaphore(value: 0)
    var result = (false, "")
    Privileged.run(script) { ok, text in
        result = (ok, text)
        done.signal()
    }
    done.wait()
    return result
}

#if UNINSTALLER
/// `Uninstall DispatchRelay.app` - the second icon in the disk image.
///
/// It exists for the one case the control panel cannot cover: the control panel is already gone, or the person
/// does not trust a menu item inside the thing they are trying to remove. It carries install.sh and no
/// binaries, so it stays small, and it removes the daemon, /opt/dispatch and DispatchRelay.app wherever the
/// user put it. Run with --yes and it does the whole thing without a dialog, which is also how CI drives the
/// real binary instead of a transcription of what the binary would run.
final class AppDelegate: NSObject, NSApplicationDelegate {
    func applicationDidFinishLaunching(_ notification: Notification) {
        let batch = CommandLine.arguments.contains("--yes")
        if !batch {
            let ask = NSAlert()
            ask.alertStyle = .warning
            ask.messageText = "Uninstall Dispatch Relay?"
            ask.informativeText = "Removes the background service, /opt/dispatch and DispatchRelay.app. "
                + Lifecycle.keptOnDisk
            ask.addButton(withTitle: "Uninstall")
            ask.addButton(withTitle: "Cancel")
            guard ask.runModal() == .alertFirstButtonReturn else { exit(0) }
        }

        let (ok, text) = runPrivilegedSync(Lifecycle.uninstallScript(alsoDeleteApp: true))

        if batch {
            print(ok ? "Dispatch Relay uninstalled." : "FAILED: \(text)")
        } else {
            let report = NSAlert()
            report.alertStyle = ok ? .informational : .critical
            report.messageText = ok ? "Dispatch Relay has been uninstalled" : "Uninstall failed"
            report.informativeText = ok ? Lifecycle.keptOnDisk : text
            report.runModal()
        }
        exit(ok ? 0 : 1)
    }

    func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool { true }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
// Unlike the control panel this one has no status item and does need a visible dialog.
app.setActivationPolicy(.regular)
app.run()

#else
/// The menu bar control panel. Quitting it deliberately does not stop the relay - the relay is a LaunchDaemon
/// that starts at boot and outlives any login session - which is correct behaviour nobody will guess, so it
/// is said out loud on the way out.
final class AppDelegate: NSObject, NSApplicationDelegate {
    private let controller = Controller()

    func applicationDidFinishLaunching(_ notification: Notification) {
        if CommandLine.arguments.contains("--uninstall-completely") {
            // The same script the menu item runs, for scripts and for CI. A test that drives a different code
            // path than the one people click is a test of the wrong path.
            let (ok, text) = runPrivilegedSync(Lifecycle.uninstallScript(alsoDeleteApp: true))
            if !ok { FileHandle.standardError.write(Data("FAILED: \(text)".utf8)) }
            exit(ok ? 0 : 1)
        }
        controller.start()
    }

    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        // Only worth asking when there is something running to ask about.
        guard FileManager.default.fileExists(atPath: daemonPlist) else { return .terminateNow }
        let ask = NSAlert()
        ask.alertStyle = .warning
        ask.messageText = "The relay keeps running after this quits."
        ask.informativeText = "Dispatch is a boot-time launchd daemon, not a process this app owns: it "
            + "continues to accept and forward mail on SMTP 25/587. Choose what you actually mean."
        ask.addButton(withTitle: "Quit (relay keeps running)")
        ask.addButton(withTitle: "Remove background service")
        ask.addButton(withTitle: "Uninstall everything")
        switch ask.runModal() {
        case .alertSecondButtonReturn: controller.removeServiceNow()
        case .alertThirdButtonReturn:  controller.uninstallEverythingNow()
        default: break
        }
        return .terminateNow
    }

    // Without this, macOS logs a secure-coding warning for state restoration on an accessory app.
    func applicationSupportsSecureRestorableState(_ app: NSApplication) -> Bool { true }
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
#endif
