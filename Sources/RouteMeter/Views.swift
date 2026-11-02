import AppKit
import SwiftUI
import RouteMeterCore

func bytes(_ value: UInt64, rate: Bool = false) -> String { formatBytes(value, rate: rate) }

struct Sparkline: View {
    let ticks: [Tick]
    let active: Bool
    var body: some View {
        ZStack {
            TrafficLine(ticks: ticks, direct: true).stroke(Color.secondary.opacity(0.7), lineWidth: 1.7)
            TrafficLine(ticks: ticks, direct: false).stroke(Color.accentColor, lineWidth: 1.7)
        }.opacity(active ? 1 : 0.35)
    }
}

private struct TrafficLine: Shape {
    let ticks: [Tick]
    let direct: Bool
    func path(in rect: CGRect) -> Path {
        let peak: Double = max(1024.0, ticks.map { max($0.direct, $0.proxy) }.max() ?? 0)
        let height: CGFloat = max(0, rect.height - 4)
        let denominator = CGFloat(max(59, ticks.count - 1))
        var path = Path()
        for (index, tick) in ticks.enumerated() {
            let rate: Double = direct ? tick.direct : tick.proxy
            let fraction = CGFloat(min(1.0, rate / peak))
            let x: CGFloat = rect.minX + rect.width * CGFloat(index) / denominator
            let y: CGFloat = rect.minY + height - height * fraction + 2
            let point = CGPoint(x: x, y: y)
            if index == 0 { path.move(to: point) } else { path.addLine(to: point) }
        }
        return path
    }
}

struct MeterPanel: View {
    @ObservedObject var model: MeterModel
    var panelWidth: CGFloat = 450
    var body: some View {
        VStack(alignment: .leading, spacing: 20) {
            HStack {
                Text("RouteMeter").font(.system(size: 18, weight: .semibold))
                Spacer()
                Circle().fill(model.live ? Color.accentColor : Color.secondary).frame(width: 6,height: 6)
                Text(model.stateText).font(.system(size: 12)).foregroundStyle(.secondary)
            }
            VStack(alignment: .leading, spacing: 22) {
                route("Direct", value: model.direct, color: .secondary)
                route("Proxy", value: model.proxy, color: .accentColor)
                route("Total", value: model.totalRate, color: .primary)
            }
            Text("Total = Direct + Proxy").font(.system(size: 12)).foregroundStyle(.secondary)
            if model.ticks.count > 1 { VStack(alignment: .leading,spacing: 5) {
                Sparkline(ticks: model.ticks, active: model.live).frame(height: 64)
                HStack {
                    Text("Last 60 samples · Download + Upload · B/s")
                    Spacer()
                    Text("Direct  /  Proxy")
                }.font(.system(size: 11)).foregroundStyle(.secondary)
            } }
            Divider()
            VStack(alignment: .leading,spacing: 10) {
                HStack {
                    Text("Cumulative usage").font(.system(size: 14,weight: .medium))
                    Spacer()
                    Text(model.started, format: .dateTime.month(.twoDigits).day(.twoDigits).hour().minute()).font(.system(size: 12)).foregroundStyle(.secondary)
                    Button("Reset") { model.reset() }.buttonStyle(.plain).font(.system(size: 12)).foregroundStyle(Color.accentColor)
                }
                HStack {
                    Text("").frame(width: 56)
                    Text("Download").frame(maxWidth: .infinity,alignment: .leading)
                    Text("Upload").frame(maxWidth: .infinity,alignment: .leading)
                    Text("Total").frame(maxWidth: .infinity,alignment: .leading)
                }.font(.system(size: 12)).foregroundStyle(.secondary)
                total("Direct", model.directTotal)
                total("Proxy", model.proxyTotal)
                total("Total", model.totalVolume)
                Text("Each row: Total = Download + Upload").font(.system(size: 11)).foregroundStyle(.secondary)
            }
            if !model.nodes.isEmpty {
                Text(model.nodes.joined(separator: " · ")).font(.system(size: 12)).foregroundStyle(.secondary).lineLimit(2)
            }
            Text("QX traffic only · Refreshes every 2 seconds.\nTotals persist across app restarts. Not a billing meter.")
                .font(.system(size: 12)).foregroundStyle(.secondary).fixedSize(horizontal: false,vertical: true)
            if let error = model.serverError ?? model.notice {
                Text(error).font(.system(size: 12)).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
            }
            Divider()
            VStack(alignment: .leading, spacing: 12) {
                HStack {
                    Button(model.copied ? "Task copied" : "Copy QX task") { model.copyTask() }
                    Spacer()
                    Button("Setup guide") { model.openGuide() }
                }.buttonStyle(.plain).foregroundStyle(Color.accentColor)
                if !model.live {
                    Text("QX: Tasks → Add → Add as text. Paste the task, update its script, and keep the QX task switch on.")
                        .font(.system(size: 12)).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
                Toggle("Launch at login", isOn: Binding(get: { model.startup }, set: { model.setStartup($0) })).toggleStyle(.checkbox)
            }.font(.system(size: 13))
            Divider()
            HStack {
                Button("Open QX") { model.openQX() }
                Spacer()
                Button("Quit") { NSApp.terminate(nil) }
            }.buttonStyle(.plain).font(.system(size: 13)).foregroundStyle(.secondary)
        }.padding(24).frame(width: panelWidth)
    }
    private func route(_ title: String, value: ByteCounts, color: Color) -> some View {
        HStack(alignment: .center,spacing: 12) {
            HStack(spacing: 5) {
                Circle().fill(color).frame(width: 5,height: 5)
                Text(title).font(.system(size: 14,weight: .semibold))
            }.frame(width: 66,alignment: .leading)
            speedColumn("Download", value.down)
            Text("|").font(.system(size: 22,weight: .ultraLight)).foregroundStyle(.secondary)
            speedColumn("Upload", value.up)
        }.frame(maxWidth: .infinity,alignment: .leading)
    }
    private func speedColumn(_ title: String, _ value: UInt64) -> some View {
        VStack(alignment: .leading,spacing: 6) {
            Text(title).font(.system(size: 13)).foregroundStyle(.secondary)
            Text(model.live ? bytes(value,rate: true) : "—")
                .font(.system(size: 24,weight: .semibold,design: .rounded)).monospacedDigit()
                .lineLimit(1).minimumScaleFactor(0.8)
        }.frame(maxWidth: .infinity,alignment: .leading)
    }
    private func total(_ label: String, _ value: ByteCounts) -> some View {
        HStack {
            Text(label).frame(width: 56,alignment: .leading)
            Text(bytes(value.down)).frame(maxWidth: .infinity,alignment: .leading)
            Text(bytes(value.up)).frame(maxWidth: .infinity,alignment: .leading)
            Text(bytes(value.total)).fontWeight(.semibold).frame(maxWidth: .infinity,alignment: .leading)
        }.font(.system(size: 14)).monospacedDigit()
    }
}

