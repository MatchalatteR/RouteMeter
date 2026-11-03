// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "RouteMeter",
    platforms: [.macOS(.v14)],
    products: [.executable(name: "RouteMeter", targets: ["RouteMeter"])],
    targets: [
        .target(name: "RouteMeterCore"),
        .executableTarget(name: "RouteMeter", dependencies: ["RouteMeterCore"], resources: [.copy("Resources/statistics-push.js"), .copy("Resources/install-update.sh")]),
        .testTarget(name: "RouteMeterCoreTests", dependencies: ["RouteMeterCore"])
    ]
)
