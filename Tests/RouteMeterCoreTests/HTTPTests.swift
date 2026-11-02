import Foundation
import XCTest
@testable import RouteMeterCore

final class HTTPTests: XCTestCase {
    func request(_ extra: String = "", body: String = "{}") -> Data {
        Data("POST /statistics HTTP/1.1\r\nHost: 127.0.0.1:18764\r\nContent-Length: \(body.utf8.count)\r\n\(extra)\r\n\(body)".utf8)
    }
    func failure(_ data: Data, code: Int = 400, file: StaticString = #filePath, line: UInt = #line) {
        if case .failure(let actual) = HTTPParser.parse(data) { XCTAssertEqual(actual,code,file:file,line:line) }
        else { XCTFail("Expected HTTP rejection",file:file,line:line) }
    }
    func testCompleteAndFragmentedRequest() {
        let data = request("X-Route-Meter-Key: example\r\n")
        for i in 0..<data.count {
            if case .incomplete = HTTPParser.parse(data.prefix(i)) {} else { XCTFail("Should await more data at \(i)") }
        }
        if case .request(let r) = HTTPParser.parse(data) {
            XCTAssertEqual(r.path,"/statistics"); XCTAssertEqual(r.body,Data("{}".utf8))
            XCTAssertEqual(r.headers["x-route-meter-key"],"example")
        } else { XCTFail("Expected complete request") }
    }
    func testRejectDuplicateHeadersAndChunking() {
        failure(request("Content-Length: 2\r\n"))
        failure(request("Transfer-Encoding: chunked\r\n"))
        failure(request("Origin: https://example.invalid\r\n"))
        failure(request("Bad Header: x\r\n"))
    }
    func testLimitsAndPipelining() {
        failure(Data(repeating:65,count:8193),code:431)
        failure(Data(repeating:65,count:108193),code:413)
        failure(request()+request())
        failure(Data("POST /statistics HTTP/1.1\r\nHost: localhost\r\nContent-Length: -1\r\n\r\n".utf8))
        failure(Data("POST /statistics HTTP/1.1\r\nHost: localhost\r\nContent-Length: 100001\r\n\r\n".utf8))
    }
    func testAuthenticationAndUnknownMethods() {
        XCTAssertTrue(HTTPParser.authorized("abc",key:"abc"))
        XCTAssertFalse(HTTPParser.authorized("abd",key:"abc"))
        XCTAssertFalse(HTTPParser.authorized(nil,key:"abc"))
        XCTAssertFalse(HTTPParser.authorized("abc1",key:"abc"))
        failure(Data("DELETE / HTTP/1.1\r\nHost: localhost\r\n\r\n".utf8))
    }
}
