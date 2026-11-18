import AppKit

/// Opening a transient popover on the first click can intercept the second event.
@MainActor final class StatusItemClicks {
    private var pending: DispatchWorkItem?
    private let interval: TimeInterval
    private let singleClick: () -> Void
    private let doubleClick: () -> Void

    init(interval: TimeInterval = NSEvent.doubleClickInterval,
         singleClick: @escaping () -> Void, doubleClick: @escaping () -> Void) {
        self.interval = interval
        self.singleClick = singleClick
        self.doubleClick = doubleClick
    }

    func click(count: Int) {
        cancel()
        if count == 2 {
            doubleClick()
        } else if count == 1 {
            let work = DispatchWorkItem { [weak self] in
                guard let self else { return }
                self.pending = nil
                self.singleClick()
            }
            pending = work
            DispatchQueue.main.asyncAfter(deadline: .now() + interval, execute: work)
        }
    }

    func cancel() {
        pending?.cancel()
        pending = nil
    }
}
