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
    @ObservedObject var updater: AppUpdater
    var panelWidth: CGFloat = 450
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HStack {
                Text("RouteMeter").font(.system(size: 18, weight: .semibold))
                Spacer()
                Circle().fill(model.live ? Color.accentColor : Color.secondary).frame(width: 6,height: 6)
                Text(model.stateText).font(.system(size: 13)).foregroundStyle(.secondary)
            }
            Picker("View", selection: Binding(get: { model.core.showApps }, set: { model.core.showApps = $0 })) {
                Text("Overview").tag(false)
                Text("Proxy apps").tag(true)
            }.pickerStyle(.segmented)
            if model.core.showApps {
                if model.core.selected {
                    CoreAppBreakdown(client: model.core)
                } else {
                    VStack(alignment: .leading, spacing: 14) {
                        Text("Proxy apps").font(.system(size: 15, weight: .semibold))
                        Label("Connect Core to view apps", systemImage: "network")
                            .font(.system(size: 14, weight: .medium)).foregroundStyle(.orange)
                        Text("QX provides totals only. App usage requires RouteMeter Core.")
                            .font(.system(size: 13)).fixedSize(horizontal: false, vertical: true)
                        Button("Connect Core…") { model.core.connect() }
                        Button("Back to Overview") { model.core.showApps = false }.buttonStyle(.plain).foregroundStyle(Color.accentColor)
                    }
                }
            } else {
            VStack(alignment: .leading, spacing: 22) {
                route("Direct", value: model.direct, color: .secondary)
                route("Proxy", value: model.proxy, color: .accentColor)
                route("Total", value: model.totalRate, color: .primary)
            }
            if model.ticks.count > 1 { VStack(alignment: .leading,spacing: 5) {
                Sparkline(ticks: model.ticks, active: model.live).frame(height: 64)
                HStack {
                    Text("Last 60 samples · B/s")
                    Spacer()
                    Text("Direct  /  Proxy")
                }.font(.system(size: 13)).foregroundStyle(.secondary)
            } }
            Divider()
            VStack(alignment: .leading,spacing: 10) {
                HStack {
                    Text("Usage").font(.system(size: 14,weight: .medium))
                    Spacer()
                    Text(model.started, format: .dateTime.month(.twoDigits).day(.twoDigits).hour().minute()).font(.system(size: 13)).foregroundStyle(.secondary)
                    Button("Reset usage") { model.reset() }.buttonStyle(.plain).font(.system(size: 13)).foregroundStyle(Color.accentColor)
                }
                HStack {
                    Text("").frame(width: 56)
                    Text("Download").frame(maxWidth: .infinity,alignment: .leading)
                    Text("Upload").frame(maxWidth: .infinity,alignment: .leading)
                    Text("Total").frame(maxWidth: .infinity,alignment: .leading)
                }.font(.system(size: 13)).foregroundStyle(.secondary)
                total("Direct", model.directTotal)
                total("Proxy", model.proxyTotal)
                if model.unknownTotal.total > 0 { total("Unknown", model.unknownTotal) }
                total("Total", model.totalVolume)
            }
            }
            if !model.nodes.isEmpty {
                Text(model.nodes.joined(separator: " · ")).font(.system(size: 13)).foregroundStyle(.secondary).lineLimit(2)
            }
            if let error = model.meterError ?? model.core.qualityWarning ?? model.notice {
                Text(error).font(.system(size: 13)).foregroundStyle(.orange).fixedSize(horizontal: false, vertical: true)
            }
            Divider()
            DisclosureGroup("Settings", isExpanded: $model.settingsExpanded) {
                VStack(alignment: .leading, spacing: 14) {
                    HStack {
                        Text("Source").foregroundStyle(.secondary)
                        Spacer()
                        Text(model.core.selected ? "RouteMeter Core" : "QX")
                    }
                    HStack {
                        Button("Connect Core…") { model.core.connect() }
                        Spacer()
                        if model.core.selected { Button("Use QX") { model.core.useQX() } }
                    }.buttonStyle(.plain).foregroundStyle(Color.accentColor)
                    if !model.core.selected {
                        HStack {
                            Button(model.copied ? "Copied" : "Copy QX task") { model.copyTask() }
                            Spacer()
                            Button("Setup guide") { model.openGuide() }
                        }.buttonStyle(.plain).foregroundStyle(Color.accentColor)
                        if !model.live {
                            Text("In QX, add the copied task as text and enable it.")
                                .foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        }
                    }
                    Toggle("Launch at login", isOn: Binding(get: { model.startup }, set: { model.setStartup($0) })).toggleStyle(.checkbox)
                }.padding(.top, 12)
            }.font(.system(size: 13))
            HStack {
                Text("Version \(updater.version)").foregroundStyle(.secondary)
                Spacer()
                if updater.busy { ProgressView().controlSize(.small) }
                Button(updater.buttonTitle) { updater.performAction() }
                    .disabled(updater.busy).foregroundStyle(Color.accentColor)
            }.buttonStyle(.plain).font(.system(size: 13))
            if let status = updater.status {
                Text(status).font(.system(size: 13)).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
            }
            Divider()
            HStack {
                if !model.core.selected { Button("Open QX") { model.openQX() } }
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

private struct CoreAppBreakdown: View {
    @ObservedObject var client: CoreClient
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Divider()
            Text("Proxy apps").font(.system(size: 15, weight: .semibold))
            if client.sample == nil {
                Label("Waiting for Core data", systemImage: "network")
                    .font(.system(size: 13)).foregroundStyle(.secondary)
            }
            Toggle("Show live rates", isOn: $client.appLiveRates).toggleStyle(.checkbox).font(.system(size: 13))
            TextField("Filter apps", text: $client.filter).textFieldStyle(.roundedBorder)
            if client.unattributedProxy.total > 0 {
                Text("Unattributed: \(formatBytes(client.unattributedProxy.total))")
                    .font(.system(size: 13)).foregroundStyle(.secondary)
            }
            HStack {
                Text("App").frame(maxWidth: .infinity, alignment: .leading)
                Text("Download | Upload").foregroundStyle(.secondary)
            }.font(.system(size: 13))
            ForEach(Array(client.rows.prefix(50).enumerated()), id: \.element.id) { index, row in
                VStack(alignment: .leading, spacing: 4) {
                    HStack(alignment: .firstTextBaseline) {
                        Text("\(index + 1).").foregroundStyle(.secondary).monospacedDigit()
                        Text(row.name).help(row.path)
                    }.font(.system(size: 13, weight: .medium))
                    let amount = client.appLiveRates ? row.rate : row.usage
                    let usable = !client.appLiveRates || client.live
                    HStack {
                        Text(usable ? formatBytes(amount.down, rate: client.appLiveRates) : "—")
                        Text("|").foregroundStyle(.secondary)
                        Text(usable ? formatBytes(amount.up, rate: client.appLiveRates) : "—")
                        Spacer()
                        if !client.appLiveRates { Text("Total " + formatBytes(amount.total)).foregroundStyle(.secondary) }
                    }.font(.system(size: 13)).monospacedDigit()
                }.padding(.vertical, 3)
            }
            if client.rows.isEmpty && client.sample != nil {
                Text(client.filter.isEmpty ? "No Proxy usage yet" : "No matching apps").font(.system(size: 13)).foregroundStyle(.secondary)
            }
            if client.rows.count > 50 {
                Text("Top 50 of \(client.rows.count) apps").font(.system(size: 13)).foregroundStyle(.secondary)
            }
        }
    }
}

