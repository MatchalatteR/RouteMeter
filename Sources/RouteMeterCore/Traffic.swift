import Foundation

public struct TrafficEntry: Codable, Equatable {
    public let name: String
    public let type: String
    public let rx_transfer: UInt64
    public let tx_transfer: UInt64
    public let rx_current_rate: UInt64
    public let tx_current_rate: UInt64
    public var id: String { type.lowercased() + ":" + name }
    public var isDirect: Bool { name.lowercased() == "direct" }
    public init(name: String, type: String = "tcp", down: UInt64, up: UInt64, downRate: UInt64 = 0, upRate: UInt64 = 0) {
        self.name = name; self.type = type; rx_transfer = down; tx_transfer = up
        rx_current_rate = downRate; tx_current_rate = upRate
    }
    var valid: Bool {
        ["tcp", "udp"].contains(type.lowercased()) && !name.isEmpty && name.utf8.count <= 1024 &&
        !["reject", "reject-200", "reject-dict", "discard"].contains(name.lowercased())
    }
}
public struct Envelope: Codable {
    public let version: Int
    public let timestamp: Double
    public let statistics: [TrafficEntry]
    public init(timestamp: Double, statistics: [TrafficEntry], version: Int = 1) {
        self.version = version; self.timestamp = timestamp; self.statistics = statistics
    }
}
public struct ByteCounts: Codable, Equatable {
    public var down: UInt64 = 0
    public var up: UInt64 = 0
    public init(down: UInt64 = 0, up: UInt64 = 0) { self.down = down; self.up = up }
    public var total: UInt64 { Self.add(down, up) }
    public static func + (lhs: Self, rhs: Self) -> Self {
        Self(down: add(lhs.down, rhs.down), up: add(lhs.up, rhs.up))
    }
    private static func add(_ a: UInt64, _ b: UInt64) -> UInt64 {
        let (value, overflow) = a.addingReportingOverflow(b)
        return overflow ? .max : value
    }
}

/// Exact byte totals with an explicit initial baseline. QX restarts rebase counters.
public struct TrafficAccumulator: Codable {
    public private(set) var directTotal = ByteCounts()
    public private(set) var proxyTotal = ByteCounts()
    public private(set) var started: Date?
    public private(set) var lastTimestamp: Double = 0
    public private(set) var directRate = ByteCounts()
    public private(set) var proxyRate = ByteCounts()
    public private(set) var nodes: [String] = []
    private var previous: [String: TrafficEntry] = [:]
    private var needsBaseline = true
    public var totalRate: ByteCounts { directRate + proxyRate }
    public var totalVolume: ByteCounts { directTotal + proxyTotal }
    public init() {}

    @discardableResult public mutating func ingest(_ packet: Envelope, now: Date = Date()) -> Bool {
        guard packet.version == 1, packet.timestamp.isFinite, packet.timestamp > lastTimestamp,
              abs(now.timeIntervalSince1970 - packet.timestamp / 1000) <= 30,
              packet.statistics.count <= 512 else { return false }
        let entries = packet.statistics.filter(\.valid)
        guard !entries.isEmpty, Set(entries.map(\.id)).count == entries.count else { return false }
        // A decrease in either direction signals a new generation for the whole snapshot.
        let reset = entries.contains { entry in
            guard let old = previous[entry.id] else { return false }
            return entry.rx_transfer < old.rx_transfer || entry.tx_transfer < old.tx_transfer
        }
        let baseline = needsBaseline || reset
        if started == nil { started = now }
        directRate = ByteCounts(); proxyRate = ByteCounts()
        var names = Set<String>()
        if reset { previous.removeAll() }
        for entry in entries {
            let rate = ByteCounts(down: entry.rx_current_rate, up: entry.tx_current_rate)
            if entry.isDirect { directRate = directRate + rate }
            else { proxyRate = proxyRate + rate; names.insert(entry.name) }
            if !baseline {
                let old = previous[entry.id]
                let delta = ByteCounts(down: entry.rx_transfer - (old?.rx_transfer ?? 0),
                                       up: entry.tx_transfer - (old?.tx_transfer ?? 0))
                if entry.isDirect { directTotal = directTotal + delta }
                else { proxyTotal = proxyTotal + delta }
            }
            previous[entry.id] = entry
        }
        // Bound persisted history even when many node names are used over time.
        if previous.count > 1024 { previous = Dictionary(uniqueKeysWithValues: entries.map { ($0.id, $0) }) }
        nodes = names.sorted(); needsBaseline = false; lastTimestamp = packet.timestamp
        return true
    }
    public mutating func reset(now: Date = Date()) {
        directTotal = ByteCounts(); proxyTotal = ByteCounts(); started = now
        needsBaseline = true; previous.removeAll()
    }
}

public func formatBytes(_ value: UInt64, rate: Bool = false) -> String {
    let units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"]
    var n = Double(value); var i = 0
    while n >= 1024 && i < units.count - 1 { n /= 1024; i += 1 }
    let format = i == 0 || n >= 100 ? "%.0f" : "%.1f"
    return String(format: format, locale: Locale(identifier: "en_US_POSIX"), n) + " " + units[i] + (rate ? "/s" : "")
}
public func shortRate(_ value: UInt64) -> String {
    if value < 1024 { return "\(value)B" }
    let units = ["K", "M", "G", "T", "P", "E"]
    var n = Double(value) / 1024; var index = 0
    while n >= 1024 && index < units.count - 1 { n /= 1024; index += 1 }
    return String(format: n >= 100 ? "%.0f" : "%.1f", locale: Locale(identifier: "en_US_POSIX"), n) + units[index]
}
