// Run: swiftc Sources/RouteMeter/StatusItemClicks.swift scripts/test-status-clicks.swift -o /tmp/routemeter-click-tests && /tmp/routemeter-click-tests
import AppKit

@main struct StatusItemClickTests {
    @MainActor static func main() async throws {
        var toggles = 0
        var quits = 0
        let clicks = StatusItemClicks(interval: 0.05,
                                      singleClick: { toggles += 1 },
                                      doubleClick: { quits += 1 })
        func settle() async throws { try await Task.sleep(nanoseconds: 150_000_000) }

        clicks.click(count: 1)
        precondition(toggles == 0 && quits == 0, "The first click must wait.")
        try await settle()
        precondition(toggles == 1 && quits == 0, "One click must toggle once.")
        print("PASS delayed single click")

        clicks.click(count: 1)
        clicks.click(count: 2)
        precondition(quits == 1, "The second click must quit immediately.")
        try await settle()
        precondition(toggles == 1 && quits == 1, "A double click must not toggle.")
        print("PASS double click cancels panel action")

        clicks.click(count: 3)
        try await settle()
        precondition(toggles == 1 && quits == 1, "A third click must not repeat quit.")
        print("PASS triple click does not repeat quit")

        clicks.click(count: 1)
        clicks.cancel()
        try await settle()
        precondition(toggles == 1 && quits == 1, "Reopen or termination must cancel pending work.")
        print("PASS pending click cancellation")

        clicks.click(count: 1)
        try await settle()
        clicks.click(count: 1)
        try await settle()
        precondition(toggles == 3 && quits == 1, "Two separate clicks must toggle twice.")
        print("PASS separate clicks open and close")
    }
}
