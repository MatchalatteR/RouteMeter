import AppKit
import SwiftUI
import RouteMeterCore

final class AppDelegate: NSObject, NSApplicationDelegate {
    var model: MeterModel?
    var item: NSStatusItem!
    let popover = NSPopover()
    var receiver: Receiver?
    var timer: Timer?
    var startupError: String?
    func applicationDidFinishLaunching(_ note: Notification) {
        NSApp.setActivationPolicy(.accessory)
        item = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        if let button = item.button {
            button.target = self; button.action = #selector(toggle)
            button.setAccessibilityLabel("RouteMeter Direct / Proxy Download | Upload")
        }
        popover.behavior = .transient
        do {
            let storage = try Storage()
            let model = MeterModel(storage: storage); self.model = model
            receiver = Receiver(storage: storage, receive: { [weak self] packet in
                self?.model?.ingest(packet); self?.render()
            }, status: { [weak self] error in self?.model?.serverError = error; self?.render() })
            try receiver?.start()
        } catch { startupError = "Cannot read local configuration. Check the RouteMeter application data folder and file permissions." }
        timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in
            self?.model?.clock = Date(); self?.render()
        }
        timer?.tolerance = 0.2
        render()
        if CommandLine.arguments.contains("--show") || model?.storage.wasConfigured == false || startupError != nil { toggle() }
    }
    func render() {
        let live = model?.live == true && model?.serverError == nil
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
        item.button?.toolTip = tooltip
    }
    @objc func toggle() {
        if popover.isShown { popover.performClose(nil); return }
        guard let button = item.button else { return }
        let screen = button.window?.screen ?? NSScreen.main
        let available = screen?.visibleFrame.size ?? NSSize(width: 900, height: 760)
        let width = max(280, min(450, available.width - 32))
        let height = max(180, min(620, available.height - 80))
        let view: AnyView
        if let model {
            view = AnyView(ScrollView(.vertical, showsIndicators: true) {
                MeterPanel(model: model, panelWidth: width)
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
        if !popover.isShown { toggle() }; return true
    }
    func applicationWillTerminate(_ notification: Notification) { model?.save(); receiver?.stop() }
}

do {
    if CommandLine.arguments.contains("--print-task") {
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
        let application = NSApplication.shared
        let delegate = AppDelegate(); application.delegate = delegate; application.run()
    }
} catch {
    fputs("RouteMeter could not initialize local storage. Check file permissions and configuration.\n", stderr)
    exit(1)
}
