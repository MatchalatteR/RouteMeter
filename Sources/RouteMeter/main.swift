import AppKit
import SwiftUI
import RouteMeterCore

@MainActor final class AppDelegate: NSObject, NSApplicationDelegate {
    let updater = AppUpdater()
    var model: MeterModel?
    var item: NSStatusItem!
    let popover = NSPopover()
    var receiver: Receiver?
    var timer: Timer?
    var startupError: String?
    lazy var statusClicks = StatusItemClicks(singleClick: { [weak self] in self?.toggle() },
                                           doubleClick: { NSApp.terminate(nil) })
    func applicationDidFinishLaunching(_ note: Notification) {
        NSApp.setActivationPolicy(.accessory)
        if let receipt = AppUpdater.receipt("pid") { try? Data(String(getpid()).utf8).write(to: receipt, options: .atomic) }
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = item.button {
            button.target = self; button.action = #selector(statusItemClicked)
            button.sendAction(on: [.leftMouseUp, .rightMouseUp])
            button.setAccessibilityLabel("RouteMeter Direct / Proxy Download | Upload")
            button.setAccessibilityHelp("Click to open. Double-click to quit. Right-click for options.")
        }
        popover.behavior = .transient
        do {
            let storage = try Storage()
            let model = MeterModel(storage: storage); self.model = model
            if CommandLine.arguments.contains("--show-proxy-apps") { model.core.showApps = true }
            receiver = Receiver(storage: storage, receive: { [weak self] packet in
                self?.model?.ingest(packet); self?.render()
            }, status: { [weak self] error in
                self?.model?.serverError = error; self?.render()
                if error == nil, let receipt = AppUpdater.receipt("ready") {
                    try? Data("ready".utf8).write(to: receipt, options: .atomic)
                }
            })
            try receiver?.start()
        } catch { startupError = "Cannot load settings. Check the application data folder permissions." }
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated {
                self?.model?.clock = Date(); self?.model?.core.poll(); self?.render()
            }
        }
        timer?.tolerance = 0.2
        render()
        updater.checkIfNeeded()
        if CommandLine.arguments.contains("--show") || CommandLine.arguments.contains("--show-proxy-apps") || model?.storage.wasConfigured == false || startupError != nil { toggle() }
    }
    func render() {
        let live = model?.live == true && model?.meterError == nil
        var title = "Direct —  |  Proxy —"
        var tooltip = startupError ?? model?.stateText ?? "Waiting for QX"
        if live, let model {
            let d = model.direct, p = model.proxy
            title = "Direct ↓\(shortRate(d.down)) ↑\(shortRate(d.up))  |  Proxy ↓\(shortRate(p.down)) ↑\(shortRate(p.up))"
            tooltip += "\nDownload | Upload (bytes per second; K/M = 1024-based)"
            tooltip += "\nDirect: \(formatBytes(d.down,rate:true)) | \(formatBytes(d.up,rate:true))"
            tooltip += "\nProxy: \(formatBytes(p.down,rate:true)) | \(formatBytes(p.up,rate:true))"
            tooltip += "\nTotal: \(formatBytes(model.totalRate.down,rate:true)) | \(formatBytes(model.totalRate.up,rate:true))"
        }
        item.button?.attributedTitle = NSAttributedString(string: title, attributes: [
            .font: NSFont.monospacedSystemFont(ofSize: 12.5, weight: .medium),
            .foregroundColor: NSColor.labelColor
        ])
        item.button?.toolTip = tooltip + "\nClick to open. Double-click to quit. Right-click for options."
    }
    @objc func statusItemClicked() {
        let event = NSApp.currentEvent
        if event?.type == .rightMouseUp || event?.modifierFlags.contains(.control) == true {
            showContextMenu()
        } else {
            statusClicks.click(count: event?.clickCount ?? 1)
        }
    }
    func showContextMenu() {
        statusClicks.cancel()
        guard let button = item.button else { return }
        let menu = NSMenu()
        menu.autoenablesItems = false
        let visibility = NSMenuItem(title: popover.isShown ? "隐藏面板" : "显示面板",
                                    action: popover.isShown ? #selector(hidePanel) : #selector(showPanel), keyEquivalent: "")
        visibility.target = self
        menu.addItem(visibility)
        let quit = NSMenuItem(title: "退出 RouteMeter", action: #selector(quitFromMenu), keyEquivalent: "")
        quit.target = self
        menu.addItem(quit)
        let updateTitle = updater.busy ? "正在更新…" : updater.available.map { "更新至 \($0.version)…" } ?? "检查更新…"
        let update = NSMenuItem(title: updateTitle, action: #selector(updateFromMenu), keyEquivalent: "")
        update.target = self
        update.isEnabled = !updater.busy
        menu.addItem(update)
        popover.performClose(nil)
        menu.popUp(positioning: nil, at: NSPoint(x: button.bounds.minX, y: button.bounds.minY), in: button)
    }
    @objc func showPanel() {
        if !popover.isShown { toggle() }
    }
    @objc func hidePanel() { popover.performClose(nil) }
    @objc func quitFromMenu() { NSApp.terminate(nil) }
    @objc func updateFromMenu() {
        updater.performAction()
        if !popover.isShown { toggle(checkForUpdates: false) }
    }
    func toggle(checkForUpdates: Bool = true) {
        if popover.isShown { popover.performClose(nil); return }
        guard let button = item.button else { return }
        if checkForUpdates { updater.checkIfNeeded() }
        let screen = button.window?.screen ?? NSScreen.main
        let available = screen?.visibleFrame.size ?? NSSize(width: 900, height: 760)
        let width = max(280, min(450, available.width - 32))
        let height = max(180, min(620, available.height - 80))
        let view: AnyView
        if let model {
            view = AnyView(VStack(spacing: 0) {
                ScrollView(.vertical, showsIndicators: true) {
                    MeterPanel(model: model, panelWidth: width)
                }
                Divider()
                UpdateControls(updater: updater)
            }.frame(width: width, height: height))
        } else {
            view = AnyView(VStack(alignment: .leading, spacing: 16) {
                Text("RouteMeter").font(.title2)
                Text(startupError ?? "Startup failed.")
                Button("Quit") { NSApp.terminate(nil) }
            }.padding(24).frame(width: width))
        }
        let controller = NSHostingController(rootView: view); controller.sizingOptions = []
        popover.contentViewController = controller
        popover.contentSize = NSSize(width: width, height: height)
        NSApp.activate(ignoringOtherApps: true)
        popover.show(relativeTo: button.bounds, of: button, preferredEdge: .minY)
    }
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        statusClicks.cancel()
        if !popover.isShown { toggle() }; return true
    }
    func applicationWillTerminate(_ notification: Notification) {
        statusClicks.cancel(); timer?.invalidate(); model?.save(); receiver?.stop()
    }
}

do {
    if CommandLine.arguments.count == 4, CommandLine.arguments[1] == "--core-self-test" {
        try CoreSelfTest.run(manifest: CommandLine.arguments[2], report: CommandLine.arguments[3])
    } else if CommandLine.arguments.contains("--print-task") {
        print(try Storage().config.taskLine)
    } else if CommandLine.arguments.contains("--headless") {
        // Used for isolated integration checks. The regular app never needs this option.
        let storage = try Storage()
        var accumulator = storage.load()
        let receiver = Receiver(storage: storage, receive: { packet in
            if accumulator.ingest(packet) { try? storage.save(accumulator) }
        }, status: { error in if let error { fputs(error + "\n", stderr) } })
        try receiver.start()
        RunLoop.main.run()
    } else {
        MainActor.assumeIsolated {
            let application = NSApplication.shared
            let delegate = AppDelegate(); application.delegate = delegate; application.run()
        }
    }
} catch {
    fputs("RouteMeter could not initialize local storage. Check file permissions and configuration.\n", stderr)
    exit(1)
}
