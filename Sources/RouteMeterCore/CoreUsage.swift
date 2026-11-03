import Foundation

public struct CoreBytes: Codable, Equatable {
    public let download: UInt64
    public let upload: UInt64
    public var counts: ByteCounts { ByteCounts(down: download, up: upload) }
}
public struct CoreRoutes: Codable, Equatable {
    public let direct: CoreBytes
    public let proxy: CoreBytes
    public let unknown: CoreBytes
    public var total: ByteCounts { direct.counts + proxy.counts + unknown.counts }
}
public struct CoreApp: Codable {
    public let id: String
    public let name: String
    public let executable: String?
    public let attribution: String
    public let usage: CoreRoutes
    public let active: UInt64
    public let connections: UInt64
}
public struct CoreUsage: Codable {
    public let schema: Int
    public let program: String
    public let epoch: String
    public let instance: String
    public let since: Int64
    public let timestamp: Int64
    public let sequence: UInt64
    public let totals: CoreRoutes
    public let apps: [CoreApp]
    public let uncertainUpload: UInt64
    public let overflow: Bool
    public let persistenceError: String?
    public let checkpoint: Int64
    public let measurement: String

    public func valid(now: Date = Date()) -> Bool {
        guard schema == 2, program == "RouteMeterCore", measurement == "routing-core-payload-lifecycle",
              !epoch.isEmpty, !instance.isEmpty, abs(now.timeIntervalSince1970 - Double(timestamp)/1000) < 30,
              apps.count <= 4097, Set(apps.map(\.id)).count == apps.count else { return false }
        let d = apps.reduce(ByteCounts()) { $0 + $1.usage.direct.counts }
        let p = apps.reduce(ByteCounts()) { $0 + $1.usage.proxy.counts }
        let u = apps.reduce(ByteCounts()) { $0 + $1.usage.unknown.counts }
        return d == totals.direct.counts && p == totals.proxy.counts && u == totals.unknown.counts
    }
}

public struct CoreConnection: Decodable {
    public let schema: Int
    public let endpoint: String
    public let token: String
    public var usageURL: URL? {
        guard schema == 1, token.count == 64, token.allSatisfy({ "0123456789abcdef".contains($0) }),
              let parts = URLComponents(string: endpoint), parts.scheme == "http",
              ["127.0.0.1", "::1", "[::1]"].contains(parts.host ?? ""),
              parts.user == nil, parts.password == nil, parts.query == nil, parts.fragment == nil,
              parts.path.isEmpty || parts.path == "/", let port = parts.port, port > 0,
              let url = parts.url else { return nil }
        return url.appendingPathComponent("v1/usage")
    }
}

public func difference(_ current: ByteCounts, _ baseline: ByteCounts) -> ByteCounts {
    ByteCounts(down: current.down >= baseline.down ? current.down - baseline.down : 0,
               up: current.up >= baseline.up ? current.up - baseline.up : 0)
}
