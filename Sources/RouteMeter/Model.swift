import AppKit
import Foundation
import Combine
import RouteMeterCore

struct Tick { let direct: Double; let proxy: Double }
final class MeterModel: ObservableObject {
    @Published private var traffic: TrafficAccumulator
    @Published var ticks: [Tick] = []
    @Published var lastUpdate: Date?
    @Published var serverError: String?
    @Published var clock = Date()
    @Published var notice: String?
    @Published var copied = false
    @Published var startup = LoginStart.enabled
    let storage: Storage
    private var lastSave = Date.distantPast
    init(storage: Storage) {
        self.storage = storage; traffic = storage.load(); notice = storage.warning
    }
    var direct: ByteCounts { traffic.directRate }
    var proxy: ByteCounts { traffic.proxyRate }
    var directTotal: ByteCounts { traffic.directTotal }
    var proxyTotal: ByteCounts { traffic.proxyTotal }
    var totalRate: ByteCounts { traffic.totalRate }
    var totalVolume: ByteCounts { traffic.totalVolume }
    var started: Date { traffic.started ?? Date() }
    var nodes: [String] { traffic.nodes }
    var live: Bool { lastUpdate.map { clock.timeIntervalSince($0) < 9 } ?? false }
    var stateText: String { serverError == nil ? (live ? "QX connected" : (lastUpdate == nil ? "Waiting for QX" : "Data interrupted")) : "Receiver unavailable" }
    func ingest(_ packet: Envelope) {
        guard traffic.ingest(packet) else { return }
        let now = Date(); lastUpdate = now; clock = now
        ticks.append(Tick(direct: Double(direct.total), proxy: Double(proxy.total)))
        if ticks.count > 60 { ticks.removeFirst(ticks.count - 60) }
        if now.timeIntervalSince(lastSave) >= 10 { save() }
    }
    func reset() { traffic.reset(); save() }
    func setStartup(_ value: Bool) {
        do { try LoginStart.set(value); startup = LoginStart.enabled }
        catch { notice = "Could not update the login item."; startup = LoginStart.enabled }
    }
    func save() {
        do { try storage.save(traffic); lastSave = Date() }
        catch { notice = "Could not save totals. Check the application data folder." }
    }
    func copyTask() {
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(storage.config.taskLine, forType: .string)
        copied = true
        DispatchQueue.main.asyncAfter(deadline: .now()+3) { [weak self] in self?.copied = false }
    }
    func openQX() {
        if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: "com.crossutility.quantumult-x") {
            NSWorkspace.shared.open(url)
        } else { notice = "Install Quantumult X for macOS first." }
    }
    func openGuide() {
        NSWorkspace.shared.open(URL(string: "https://github.com/MatchalatteR/RouteMeter#setup-quantumult-x")!)
    }
}

enum LoginStart {
    static let label = "io.github.MatchalatteR.RouteMeter"
    static var file: URL {
        FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/LaunchAgents/\(label).plist")
    }
    static var enabled: Bool { FileManager.default.fileExists(atPath: file.path) }
    static func set(_ enabled: Bool) throws {
        if enabled {
            try FileManager.default.createDirectory(at: file.deletingLastPathComponent(), withIntermediateDirectories: true)
            let data = try PropertyListSerialization.data(fromPropertyList: [
                "Label": label, "ProgramArguments": ["/usr/bin/open", "-a", Bundle.main.bundleURL.path],
                "RunAtLoad": true, "StandardOutPath": "/dev/null", "StandardErrorPath": "/dev/null"
            ], format: .xml, options: 0)
            try data.write(to: file, options: .atomic)
            try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: file.path)
            // Registration is best-effort; the plist remains valid for the next login.
            run(["bootstrap", "gui/\(getuid())", file.path])
        } else {
            run(["bootout", "gui/\(getuid())/\(label)"])
            if FileManager.default.fileExists(atPath: file.path) { try FileManager.default.removeItem(at: file) }
        }
    }
    private static func run(_ args: [String]) {
        let process = Process(); process.executableURL = URL(fileURLWithPath: "/bin/launchctl"); process.arguments = args
        process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
        do { try process.run(); process.waitUntilExit() } catch { /* surfaced by startup state */ }
    }
}
