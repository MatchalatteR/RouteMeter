import Foundation

public struct HTTPRequest {
    public let method: String
    public let path: String
    public let headers: [String: String]
    public let body: Data
}
public enum HTTPParseResult {
    case incomplete
    case request(HTTPRequest)
    case failure(Int)
}
/// Deliberately narrow HTTP/1.1 reader for a local, non-browser statistics bridge.
public enum HTTPParser {
    public static let maxBody = 100_000
    public static let maxHeader = 8_192
    public static func parse(_ data: Data) -> HTTPParseResult {
        guard data.count <= maxHeader + maxBody else { return .failure(413) }
        guard let boundary = data.range(of: Data("\r\n\r\n".utf8)) else {
            return data.count > maxHeader ? .failure(431) : .incomplete
        }
        guard boundary.lowerBound <= maxHeader,
              let text = String(data: data[..<boundary.lowerBound], encoding: .utf8) else { return .failure(400) }
        let lines = text.components(separatedBy: "\r\n")
        let first = (lines.first ?? "").split(separator: " ", omittingEmptySubsequences: false)
        guard first.count == 3, first[2] == "HTTP/1.1", first[1].hasPrefix("/"),
              ["GET", "POST"].contains(first[0]) else { return .failure(400) }
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { return .failure(400) }
            let name = String(line[..<colon]).lowercased()
            guard !name.isEmpty, name.allSatisfy({ $0.isASCII && ($0.isLetter || $0.isNumber || $0 == "-") }),
                  headers[name] == nil else { return .failure(400) }
            headers[name] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }
        // Do not support chunking, CORS, browsers, pipelining, or duplicate headers.
        guard headers["transfer-encoding"] == nil, headers["origin"] == nil,
              headers["host"] != nil else { return .failure(400) }
        let lengthText = headers["content-length"] ?? (first[0] == "GET" ? "0" : "")
        guard !lengthText.isEmpty, lengthText.allSatisfy(\.isNumber),
              let length = Int(lengthText), length <= maxBody else { return .failure(400) }
        let body = data[boundary.upperBound...]
        guard body.count >= length else { return .incomplete }
        guard body.count == length else { return .failure(400) }
        return .request(HTTPRequest(method: String(first[0]), path: String(first[1]), headers: headers, body: Data(body)))
    }
    public static func authorized(_ supplied: String?, key: String) -> Bool {
        guard let supplied, supplied.utf8.count == key.utf8.count else { return false }
        var difference: UInt8 = 0
        for (a,b) in zip(supplied.utf8, key.utf8) { difference |= a ^ b }
        return difference == 0
    }
}
