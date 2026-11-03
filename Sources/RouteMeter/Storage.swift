import Foundation
import Security
import RouteMeterCore

struct AppConfig: Codable {
    var key: String
    var scriptToken: String?
    var port: UInt16?
    var receiverPort: UInt16 { port ?? 18764 }
    var scriptPath: String { "/bridge/\(scriptToken ?? "")/statistics-push.js" }
    var taskLine: String {
        "*/2 * * * * * http://127.0.0.1:\(receiverPort)\(scriptPath), tag=RouteMeter, enabled=true"
    }
}
final class Storage {
    let root: URL
    var config: AppConfig
    let wasConfigured: Bool
    var warning: String?
    init(root supplied: URL? = nil) throws {
        if let supplied {
            root = supplied
        } else if let custom = ProcessInfo.processInfo.environment["ROUTEMETER_DATA_DIR"] {
            root = URL(fileURLWithPath: custom, isDirectory: true)
        } else {
            root = FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/RouteMeter", isDirectory: true)
        }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        try FileManager.default.setAttributes([.posixPermissions: 0o700], ofItemAtPath: root.path)
        let file = root.appendingPathComponent("config.json")
        wasConfigured = FileManager.default.fileExists(atPath: file.path)
        if wasConfigured {
            config = try JSONDecoder().decode(AppConfig.self, from: Data(contentsOf: file))
            guard Self.validToken(config.key) else { throw StorageError.invalidConfig }
        } else {
            config = AppConfig(key: try Self.randomToken(), scriptToken: nil, port: nil)
        }
        if config.scriptToken == nil { config.scriptToken = try Self.randomToken() }
        guard Self.validToken(config.scriptToken ?? ""), config.receiverPort >= 1024 else { throw StorageError.invalidConfig }
        try write(try JSONEncoder().encode(config), to: "config.json")
        let candidates = [Bundle.main.resourceURL, Bundle.main.bundleURL,
                          URL(fileURLWithPath: CommandLine.arguments[0]).standardizedFileURL.deletingLastPathComponent()]
        let templateURL = candidates.compactMap { $0?.appendingPathComponent("RouteMeter_RouteMeter.bundle/statistics-push.js") }
            .first { FileManager.default.fileExists(atPath: $0.path) }
        guard let templateURL else { throw StorageError.missingResource }
        let template = try String(contentsOf: templateURL, encoding: .utf8)
        let script = template.replacingOccurrences(of: "__LOCAL_KEY__", with: config.key)
            .replacingOccurrences(of: "__PORT__", with: String(config.receiverPort))
        try write(Data(script.utf8), to: "statistics-push.local.js")
    }
    func load() -> TrafficAccumulator {
        let url = root.appendingPathComponent("state.json")
        guard FileManager.default.fileExists(atPath: url.path) else { return TrafficAccumulator() }
        do {
            let data = try Data(contentsOf: url)
            guard data.count <= 2_000_000 else { throw StorageError.invalidState }
            let saved = try JSONDecoder().decode(SavedState.self, from: data)
            guard saved.version == 1 else { throw StorageError.invalidState }
            return saved.traffic
        } catch {
            warning = "Saved totals could not be read. A new baseline was created."
            let backup = root.appendingPathComponent("state-unreadable-\(Int(Date().timeIntervalSince1970)).json")
            try? FileManager.default.moveItem(at: url, to: backup)
            return TrafficAccumulator()
        }
    }
    func save(_ traffic: TrafficAccumulator) throws {
        try write(try JSONEncoder().encode(SavedState(version: 1, traffic: traffic)), to: "state.json")
    }
    func script() throws -> Data { try Data(contentsOf: root.appendingPathComponent("statistics-push.local.js")) }
    private func write(_ data: Data, to name: String) throws {
        let url = root.appendingPathComponent(name)
        try data.write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
    private static func validToken(_ token: String) -> Bool {
        token.count == 64 && token.allSatisfy { "0123456789abcdef".contains($0) }
    }
    private static func randomToken() throws -> String {
        var data = [UInt8](repeating: 0, count: 32)
        guard SecRandomCopyBytes(kSecRandomDefault, data.count, &data) == errSecSuccess else { throw StorageError.randomFailure }
        return data.map { String(format: "%02x", $0) }.joined()
    }
    private struct SavedState: Codable { let version: Int; let traffic: TrafficAccumulator }
    private enum StorageError: Error { case invalidConfig, invalidState, randomFailure, missingResource }
}
