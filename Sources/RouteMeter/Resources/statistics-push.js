// RouteMeter QX bridge. Provisioned locally; never sends data to the Internet.
$configuration.sendMessage({ action: "get_traffic_statistics" }).then(result => {
  if (result.error || !Array.isArray(result.ret)) { $done(); return; }
  // Allowlist aggregate fields. No destinations, request bodies, or subscription secrets.
  const statistics = result.ret.map(e => ({
    name: e.name, type: e.type,
    rx_transfer: e.rx_transfer, tx_transfer: e.tx_transfer,
    rx_current_rate: e.rx_current_rate, tx_current_rate: e.tx_current_rate
  }));
  $task.fetch({
    url: "http://127.0.0.1:__PORT__/statistics", method: "POST",
    headers: {"Content-Type": "application/json", "X-Route-Meter-Key": "__LOCAL_KEY__"},
    body: JSON.stringify({version: 1, timestamp: Date.now(), statistics})
  }).then(() => $done(), () => $done());
}, () => $done());
