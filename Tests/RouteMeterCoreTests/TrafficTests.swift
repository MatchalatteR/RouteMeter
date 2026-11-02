import Foundation
import XCTest
@testable import RouteMeterCore

final class TrafficTests: XCTestCase {
    let now = Date(timeIntervalSince1970: 1_800_000_000)
    func packet(_ entries: [TrafficEntry], offset: Double = 0, version: Int = 1) -> Envelope {
        Envelope(timestamp: now.timeIntervalSince1970 * 1000 + offset, statistics: entries, version: version)
    }
    func testClassificationRatesAndBaseline() {
        var meter = TrafficAccumulator()
        XCTAssertTrue(meter.ingest(packet([
            .init(name:"DIRECT",down:100,up:50,downRate:10,upRate:2),
            .init(name:"direct",type:"udp",down:100,up:50,downRate:20,upRate:3),
            .init(name:"Node A",down:300,up:100,downRate:40,upRate:4),
            .init(name:"Node B",type:"udp",down:300,up:100,downRate:50,upRate:5),
            .init(name:"reject",down:999,up:999,downRate:999),
            .init(name:"Unsupported",type:"icmp",down:999,up:999,downRate:999)
        ]), now:now))
        XCTAssertEqual(meter.directRate, ByteCounts(down:30,up:5))
        XCTAssertEqual(meter.proxyRate, ByteCounts(down:90,up:9))
        XCTAssertEqual(meter.totalRate.total,134)
        XCTAssertEqual(meter.totalVolume.total,0)
        XCTAssertEqual(meter.nodes,["Node A","Node B"])
    }
    func testDeltasAndNewNode() {
        var meter = TrafficAccumulator()
        meter.ingest(packet([.init(name:"direct",down:100,up:50)]),now:now)
        meter.ingest(packet([.init(name:"direct",down:140,up:70),.init(name:"New node",down:20,up:5)],offset:2),now:now)
        XCTAssertEqual(meter.directTotal.total,60)
        XCTAssertEqual(meter.proxyTotal.total,25)
        XCTAssertEqual(meter.totalVolume.total,85)
    }
    func testMissingNodeDoesNotDoubleCountOnReturn() {
        var meter = TrafficAccumulator()
        meter.ingest(packet([.init(name:"direct",down:1,up:1),.init(name:"A",down:100,up:100)]),now:now)
        meter.ingest(packet([.init(name:"direct",down:2,up:2)],offset:1),now:now)
        meter.ingest(packet([.init(name:"direct",down:3,up:3),.init(name:"A",down:105,up:106)],offset:2),now:now)
        XCTAssertEqual(meter.proxyTotal.total,11)
    }
    func testAnyCounterDecreaseRebasesEntireSnapshot() {
        var meter = TrafficAccumulator()
        meter.ingest(packet([.init(name:"direct",down:100,up:100),.init(name:"A",down:100,up:100)]),now:now)
        meter.ingest(packet([.init(name:"direct",down:10,up:200),.init(name:"A",down:200,up:200)],offset:1),now:now)
        XCTAssertEqual(meter.totalVolume.total,0)
        meter.ingest(packet([.init(name:"direct",down:11,up:201),.init(name:"A",down:201,up:201)],offset:2),now:now)
        XCTAssertEqual(meter.totalVolume.total,4)
    }
    func testPersistedStateContinuesDeltas() throws {
        var meter = TrafficAccumulator()
        meter.ingest(packet([.init(name:"direct",down:100,up:50)]),now:now)
        let data = try JSONEncoder().encode(meter)
        var restored = try JSONDecoder().decode(TrafficAccumulator.self,from:data)
        restored.ingest(packet([.init(name:"direct",down:110,up:55)],offset:2),now:now)
        XCTAssertEqual(restored.totalVolume.total,15)
        XCTAssertEqual(restored.started,now)
    }
    func testFreshnessVersionAndOrdering() {
        var meter = TrafficAccumulator()
        let e = [TrafficEntry(name:"direct",down:100,up:0)]
        XCTAssertFalse(meter.ingest(packet(e,offset:-31_000),now:now))
        XCTAssertFalse(meter.ingest(packet(e,offset:31_000),now:now))
        XCTAssertFalse(meter.ingest(packet(e,version:2),now:now))
        XCTAssertTrue(meter.ingest(packet(e),now:now))
        XCTAssertFalse(meter.ingest(packet(e),now:now))
        XCTAssertFalse(meter.ingest(packet(e,offset:-1),now:now))
        XCTAssertFalse(meter.ingest(Envelope(timestamp:.nan,statistics:e),now:now))
    }
    func testInvalidAndDuplicateSnapshotsDoNotChangeState() {
        var meter = TrafficAccumulator()
        let e = TrafficEntry(name:"direct",down:100,up:50)
        XCTAssertFalse(meter.ingest(packet([]),now:now))
        XCTAssertFalse(meter.ingest(packet([e,e]),now:now))
        XCTAssertFalse(meter.ingest(packet(Array(repeating:e,count:513)),now:now))
        XCTAssertNil(meter.started)
    }
    func testResetEstablishesANewBaseline() {
        var meter = TrafficAccumulator()
        meter.ingest(packet([.init(name:"direct",down:100,up:0)]),now:now)
        meter.reset(now:now)
        meter.ingest(packet([.init(name:"direct",down:200,up:0)],offset:1),now:now)
        XCTAssertEqual(meter.totalVolume.total,0)
        meter.ingest(packet([.init(name:"direct",down:201,up:0)],offset:2),now:now)
        XCTAssertEqual(meter.totalVolume.total,1)
    }
    func testOverflowDoesNotTrapAndFormattingHasLargeUnits() {
        XCTAssertEqual((ByteCounts(down:.max,up:1)+ByteCounts(down:1)).down,.max)
        XCTAssertEqual(ByteCounts(down:.max,up:1).total,.max)
        XCTAssertEqual(formatBytes(1024,rate:true),"1.0 KB/s")
        XCTAssertEqual(shortRate(1<<30),"1.0G")
        XCTAssertTrue(formatBytes(.max).contains("EB"))
    }
    func testNegativeCounterJSONFailsDecoding() {
        let data = Data(#"{"name":"direct","type":"tcp","rx_transfer":-1,"tx_transfer":0,"rx_current_rate":0,"tx_current_rate":0}"#.utf8)
        XCTAssertThrowsError(try JSONDecoder().decode(TrafficEntry.self,from:data))
    }
}
