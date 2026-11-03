import AppKit
import Combine
import Foundation
import RouteMeterCore

private final class ReleaseSession: NSObject, URLSessionTaskDelegate {
    func urlSession(_ session: URLSession, task: URLSessionTask, willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest, completionHandler: @escaping (URLRequest?) -> Void) {
        let allowed = ["github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com"]
        let url = request.url
        completionHandler(url?.scheme == "https" && allowed.contains(url?.host ?? "") && url?.user == nil && url?.password == nil ? request : nil)
    }
}

@MainActor final class AppUpdater: ObservableObject {
    @Published private(set) var available: ReleaseUpdate?
    @Published private(set) var busy = false
    @Published private(set) var status: String?
    let version = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "0.0.0"
    private let delegate = ReleaseSession()
    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.ephemeral
        config.timeoutIntervalForRequest = 30
        config.timeoutIntervalForResource = 300
        return URLSession(configuration: config, delegate: delegate, delegateQueue: nil)
    }()
    private var checked: Date?
    init() {
        if CommandLine.arguments.contains("--update-failed") { status = "Update failed. Previous version restored." }
    }
    var buttonTitle: String {
        if let available { return "Update to \(available.version)" }
        return "Check for updates"
    }
    func checkIfNeeded() {
        guard checked.map({ Date().timeIntervalSince($0) > 6 * 3600 }) ?? true else { return }
        Task { await check(silent: true) }
    }
    func performAction() {
        guard !busy else { return }
        Task { if let available { await install(available) } else { await check(silent: false) } }
    }
    private func check(silent: Bool) async {
        guard !busy else { return }; busy = true; checked = Date()
        if !silent { status = "Checking…" }
        defer { busy = false }
        do {
            var request = URLRequest(url: URL(string: "https://api.github.com/repos/MatchalatteR/RouteMeter/releases/latest")!)
            request.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
            request.setValue("RouteMeter/\(version)", forHTTPHeaderField: "User-Agent")
            let (data, response) = try await session.data(for: request)
            guard (response as? HTTPURLResponse)?.statusCode == 200, data.count < 1_000_000 else { throw UpdateError.network }
            #if arch(arm64)
            let architecture = "arm64"
            #else
            let architecture = "x86_64"
            #endif
            available = try JSONDecoder().decode(GitHubRelease.self, from: data).update(current: version, architecture: architecture)
            if !silent { status = available == nil ? "Up to date" : nil }
        } catch { if !silent { status = message(error) } }
    }
    private func install(_ update: ReleaseUpdate) async {
        guard !busy else { return }; busy = true; status = "Downloading…"
        defer { busy = false }
        var staging: URL?
        do {
            let destination = Bundle.main.bundleURL.standardizedFileURL
            let parent = destination.deletingLastPathComponent()
            guard destination.pathExtension == "app", !destination.path.contains("/AppTranslocation/"),
                  FileManager.default.isWritableFile(atPath: parent.path), FileManager.default.isWritableFile(atPath: destination.path),
                  (try destination.resourceValues(forKeys: [.isSymbolicLinkKey])).isSymbolicLink == false else { throw UpdateError.location }
            let work = parent.appendingPathComponent(".RouteMeter-update-" + UUID().uuidString, isDirectory: true)
            try FileManager.default.createDirectory(at: work, withIntermediateDirectories: false, attributes: [.posixPermissions: 0o700])
            staging = work
            let (download, response) = try await session.download(from: update.archive.browser_download_url)
            defer { try? FileManager.default.removeItem(at: download) }
            guard (response as? HTTPURLResponse)?.statusCode == 200 else { throw UpdateError.network }
            let (checksum, checkResponse) = try await session.data(from: update.checksum.browser_download_url)
            guard (checkResponse as? HTTPURLResponse)?.statusCode == 200 else { throw UpdateError.network }
            status = "Verifying…"
            // File verification and archive extraction stay off the UI thread.
            try await Task.detached {
                try update.verify(archive: download, checksum: checksum)
                try Self.run("/usr/bin/ditto", ["-x", "-k", download.path, work.path])
                try Self.validate(work.appendingPathComponent("RouteMeter.app"), version: update.version)
            }.value
            guard let resource = Bundle.main.resourceURL?.appendingPathComponent("RouteMeter_RouteMeter.bundle/install-update.sh"),
                  FileManager.default.fileExists(atPath: resource.path) else { throw UpdateError.install }
            let helper = work.appendingPathComponent("install-update.sh")
            try FileManager.default.copyItem(at: resource, to: helper)
            let log = work.appendingPathComponent("install.log")
            FileManager.default.createFile(atPath: log.path, contents: nil, attributes: [.posixPermissions: 0o600])
            let handle = try FileHandle(forWritingTo: log)
            defer { try? handle.close() }
            let process = Process(); process.executableURL = URL(fileURLWithPath: "/bin/bash")
            process.arguments = [helper.path, work.path, destination.path, String(ProcessInfo.processInfo.processIdentifier), update.version]
            process.standardInput = FileHandle.nullDevice; process.standardOutput = handle; process.standardError = handle
            try process.run()
            staging = nil // The independent helper now owns the transaction and backup.
            status = "Restarting…"
            NSApp.terminate(nil)
        } catch {
            if let staging { try? FileManager.default.removeItem(at: staging) }
            status = message(error)
        }
    }
    private func message(_ error: Error) -> String {
        (error as? UpdateError)?.errorDescription ?? "Update failed. Try again."
    }
    nonisolated private static func run(_ command: String, _ arguments: [String]) throws {
        let process = Process(); process.executableURL = URL(fileURLWithPath: command); process.arguments = arguments
        process.standardOutput = FileHandle.nullDevice; process.standardError = FileHandle.nullDevice
        try process.run(); process.waitUntilExit()
        guard process.terminationStatus == 0 else { throw UpdateError.package }
    }
    nonisolated private static func validate(_ app: URL, version: String) throws {
        let fm = FileManager.default
        let values = try app.resourceValues(forKeys: [.isDirectoryKey, .isSymbolicLinkKey])
        guard values.isDirectory == true, values.isSymbolicLink == false,
              let bundle = Bundle(url: app), bundle.bundleIdentifier == "io.github.MatchalatteR.RouteMeter",
              bundle.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String == version,
              bundle.object(forInfoDictionaryKey: "CFBundleExecutable") as? String == "RouteMeter",
              fm.isExecutableFile(atPath: app.appendingPathComponent("Contents/MacOS/RouteMeter").path),
              fm.isExecutableFile(atPath: app.appendingPathComponent("Contents/Helpers/RouteMeterCore").path) else { throw UpdateError.package }
        if let minimum = bundle.object(forInfoDictionaryKey: "LSMinimumSystemVersion") as? String {
            let parts = minimum.split(separator: ".").compactMap { Int($0) }
            guard (1...3).contains(parts.count), ProcessInfo.processInfo.isOperatingSystemAtLeast(
                OperatingSystemVersion(majorVersion: parts[0], minorVersion: parts.count > 1 ? parts[1] : 0,
                                       patchVersion: parts.count > 2 ? parts[2] : 0)) else { throw UpdateError.package }
        }
        #if arch(arm64)
        let architecture = "arm64"
        #else
        let architecture = "x86_64"
        #endif
        try run("/usr/bin/lipo", ["-verify_arch", architecture, app.appendingPathComponent("Contents/MacOS/RouteMeter").path])
        try run("/usr/bin/codesign", ["--verify", "--deep", "--strict", app.path])
    }

    // Acknowledge launch only after local storage, the receiver and the view initialize.
    nonisolated static func receipt(_ name: String) -> URL? {
        let args = CommandLine.arguments
        guard let index = args.firstIndex(of: "--update-receipt"), args.count > index + 1 else { return nil }
        let directory = URL(fileURLWithPath: args[index + 1]).standardizedFileURL
        guard directory.lastPathComponent.hasPrefix(".RouteMeter-update-"),
              directory.deletingLastPathComponent() == Bundle.main.bundleURL.deletingLastPathComponent().standardizedFileURL else { return nil }
        return directory.appendingPathComponent(name)
    }
}
