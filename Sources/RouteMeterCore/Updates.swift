import Foundation
import CryptoKit

public struct ReleaseVersion: Comparable, Equatable {
    public let text: String
    private let parts: [Int]
    public init?(_ text: String) {
        let values = text.split(separator: ".", omittingEmptySubsequences: false)
        guard values.count == 3, values.allSatisfy({ !$0.isEmpty && $0.allSatisfy({ $0.isASCII && $0.isNumber }) }),
              values.allSatisfy({ $0 == "0" || !$0.hasPrefix("0") }) else { return nil }
        let parts = values.compactMap { Int($0) }
        guard parts.count == 3 else { return nil }
        self.text = text; self.parts = parts
    }
    public static func < (lhs: Self, rhs: Self) -> Bool { lhs.parts.lexicographicallyPrecedes(rhs.parts) }
}

public struct GitHubRelease: Decodable {
    public struct Asset: Decodable {
        public let name: String
        public let size: Int
        public let browser_download_url: URL
    }
    public let tag_name: String
    public let draft: Bool
    public let prerelease: Bool
    public let assets: [Asset]

    public func update(current: String, architecture: String) throws -> ReleaseUpdate? {
        guard let installed = ReleaseVersion(current) else { throw UpdateError.invalidVersion }
        guard !draft, !prerelease, tag_name.hasPrefix("v"), let version = ReleaseVersion(String(tag_name.dropFirst())) else {
            throw UpdateError.invalidRelease
        }
        guard version > installed else { return nil }
        guard ["arm64", "x86_64"].contains(architecture) else { throw UpdateError.invalidRelease }
        let name = "RouteMeter-\(version.text)-\(architecture).zip"
        func asset(_ name: String, limit: Int) throws -> Asset {
            let matches = assets.filter { $0.name == name }
            guard matches.count == 1, let result = matches.first, result.size > 0, result.size <= limit,
                  result.browser_download_url.absoluteString == "https://github.com/MatchalatteR/RouteMeter/releases/download/\(tag_name)/\(name)" else {
                throw UpdateError.invalidRelease
            }
            return result
        }
        return try ReleaseUpdate(version: version.text, archive: asset(name, limit: 256_000_000),
                                 checksum: asset(name + ".sha256", limit: 4096))
    }
}

public struct ReleaseUpdate {
    public let version: String
    public let archive: GitHubRelease.Asset
    public let checksum: GitHubRelease.Asset
    public func verify(archive url: URL, checksum data: Data) throws {
        guard data.count == checksum.size, let text = String(data: data, encoding: .utf8) else { throw UpdateError.checksum }
        let fields = text.split(whereSeparator: { $0.isWhitespace })
        guard fields.count == 2, fields[0].count == 64, fields[0].allSatisfy({ $0.isASCII && $0.isHexDigit }),
              fields[1] == archive.name || fields[1] == "*" + archive.name else { throw UpdateError.checksum }
        let contents = try Data(contentsOf: url, options: .mappedIfSafe)
        guard contents.count == archive.size else { throw UpdateError.checksum }
        let digest = SHA256.hash(data: contents).map { String(format: "%02x", $0) }.joined()
        guard digest == fields[0].lowercased() else { throw UpdateError.checksum }
    }
}

public enum UpdateError: LocalizedError {
    case invalidVersion, invalidRelease, network, checksum, location, package, install
    public var errorDescription: String? {
        switch self {
        case .invalidVersion: return "Cannot read the installed version."
        case .invalidRelease: return "No compatible update package."
        case .network: return "Cannot reach GitHub. Try again."
        case .checksum: return "Update verification failed. Try again."
        case .location: return "Move RouteMeter to a writable Applications folder."
        case .package: return "The update package is invalid."
        case .install: return "Cannot install the update. Try again."
        }
    }
}
