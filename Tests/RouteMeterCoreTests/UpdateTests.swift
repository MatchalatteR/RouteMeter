import Foundation
import CryptoKit
import XCTest
@testable import RouteMeterCore

final class UpdateTests: XCTestCase {
    func testNumericVersions() {
        XCTAssertLessThan(ReleaseVersion("0.9.9")!, ReleaseVersion("0.10.0")!)
        XCTAssertLessThan(ReleaseVersion("0.99.0")!, ReleaseVersion("1.0.0")!)
        XCTAssertEqual(ReleaseVersion("1.2.3"), ReleaseVersion("1.2.3"))
        for value in ["", "v1.2.3", "1.2", "1.2.3.4", "1.2.3-beta", "01.2.3", "-1.2.3", "１.2.3", "999999999999999999999999.0.0"] {
            XCTAssertNil(ReleaseVersion(value), value)
        }
    }
    private func release(version: String = "0.3.0", prerelease: Bool = false, draft: Bool = false,
                         architecture: String = "arm64", host: String = "github.com", size: Int = 3,
                         checksumSize: Int = 93, duplicate: Bool = false, missingChecksum: Bool = false) throws -> GitHubRelease {
        let name = "RouteMeter-\(version)-\(architecture).zip"
        let archive: [String: Any] = ["name": name, "size": size,
                                     "browser_download_url": "https://\(host)/MatchalatteR/RouteMeter/releases/download/v\(version)/\(name)"]
        var assets = [archive]
        if !missingChecksum { assets.append(["name": name + ".sha256", "size": checksumSize,
                                            "browser_download_url": "https://\(host)/MatchalatteR/RouteMeter/releases/download/v\(version)/\(name).sha256"]) }
        if duplicate { assets.append(archive) }
        return try JSONDecoder().decode(GitHubRelease.self, from: JSONSerialization.data(withJSONObject: [
            "tag_name": "v" + version, "prerelease": prerelease, "draft": draft, "assets": assets
        ]))
    }
    func testSelectNewerCompatibleReleaseOnly() throws {
        let stable = try release()
        XCTAssertEqual(try stable.update(current: "0.2.2", architecture: "arm64")?.version, "0.3.0")
        XCTAssertNil(try stable.update(current: "0.3.0", architecture: "arm64"))
        XCTAssertNil(try stable.update(current: "1.0.0", architecture: "arm64"))
        XCTAssertThrowsError(try stable.update(current: "0.2.2", architecture: "x86_64"))
        XCTAssertThrowsError(try stable.update(current: "unknown", architecture: "arm64"))
        for candidate in [try release(prerelease: true), try release(draft: true), try release(version: "0.3.0-rc1"),
                          try release(host: "example.invalid"), try release(size: 256_000_001), try release(size: 0),
                          try release(checksumSize: 4097), try release(duplicate: true), try release(missingChecksum: true)] {
            XCTAssertThrowsError(try candidate.update(current: "0.2.2", architecture: "arm64"))
        }
    }
    func testChecksumAndExactFileIdentity() throws {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: root) }
        let file = root.appendingPathComponent("archive.zip")
        let content = Data("abc".utf8); try content.write(to: file)
        let digest = SHA256.hash(data: content).map { String(format: "%02x", $0) }.joined()
        let line = "\(digest)  RouteMeter-0.3.0-arm64.zip\n"
        let update = try XCTUnwrap(release(checksumSize: line.utf8.count).update(current: "0.2.2", architecture: "arm64"))
        XCTAssertNoThrow(try update.verify(archive: file, checksum: Data(line.utf8)))
        let wrongName = line.replacingOccurrences(of: "arm64", with: "intel")
        XCTAssertThrowsError(try update.verify(archive: file, checksum: Data(wrongName.utf8)))
        XCTAssertThrowsError(try update.verify(archive: file, checksum: Data((line + line).utf8)))
        try Data("abd".utf8).write(to: file)
        XCTAssertThrowsError(try update.verify(archive: file, checksum: Data(line.utf8)))
        try Data("abc-extra".utf8).write(to: file)
        XCTAssertThrowsError(try update.verify(archive: file, checksum: Data(line.utf8)))
    }
}
