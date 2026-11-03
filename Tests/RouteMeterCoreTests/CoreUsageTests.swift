import Foundation
import XCTest
@testable import RouteMeterCore

final class CoreUsageTests: XCTestCase {
    func snapshot(proxyDownload: UInt64 = 20, timestamp: Double? = nil) throws -> CoreUsage {
        let bytes: (UInt64, UInt64) -> [String: UInt64] = { ["download": $0, "upload": $1] }
        let usage: [String: Any] = ["direct": bytes(10, 1), "proxy": bytes(20, 2), "unknown": bytes(0, 0)]
        let totals: [String: Any] = ["direct": bytes(10, 1), "proxy": bytes(proxyDownload, 2), "unknown": bytes(0, 0)]
        let now = Date().timeIntervalSince1970 * 1000
        let body: [String: Any] = ["schema": 2, "program": "RouteMeterCore", "epoch": "test-epoch", "instance": "test-instance",
            "since": Int64(now - 1000), "timestamp": Int64(timestamp ?? now), "sequence": 1, "totals": totals,
            "apps": [["id": "example", "name": "Example", "attribution": "executable", "usage": usage, "active": 0, "connections": 1]],
            "uncertainUpload": 0, "overflow": false, "checkpoint": 0, "measurement": "routing-core-payload-lifecycle"]
        return try JSONDecoder().decode(CoreUsage.self, from: JSONSerialization.data(withJSONObject: body))
    }
    func testSchemaAndConservation() throws {
        XCTAssertTrue(try snapshot().valid())
        XCTAssertFalse(try snapshot(proxyDownload: 21).valid())
        XCTAssertFalse(try snapshot(timestamp: (Date().timeIntervalSince1970 - 60)*1000).valid())
    }
    func testOnlyPrivateLoopbackConnectionFiles() throws {
        let key = String(repeating: "a", count: 64)
        func connection(_ url: String, token: String? = nil) throws -> CoreConnection {
            try JSONDecoder().decode(CoreConnection.self, from: JSONSerialization.data(withJSONObject: ["schema": 1, "endpoint": url, "token": token ?? key]))
        }
        XCTAssertEqual(try connection("http://127.0.0.1:18765").usageURL?.path, "/v1/usage")
        XCTAssertNil(try connection("http://example.invalid:18765").usageURL)
        XCTAssertNil(try connection("http://127.0.0.1.attacker.invalid:18765").usageURL)
        XCTAssertNil(try connection("http://user@127.0.0.1:18765").usageURL)
        XCTAssertNil(try connection("http://127.0.0.1:18765", token: "short").usageURL)
        XCTAssertNil(try connection("http://127.0.0.1:18765?token=bad").usageURL)
    }
    func testEpochResetDifferenceNeverWraps() {
        XCTAssertEqual(difference(ByteCounts(down: 4, up: 7), ByteCounts(down: 10, up: 5)), ByteCounts(down: 0, up: 2))
    }
}
