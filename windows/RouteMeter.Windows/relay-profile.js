// RouteMeter local metering. Generated credentials are private to this computer.
// Paste into Clash Verge's global extension script. If you already have main(),
// call routeMeter(config) at the END of that main() instead of replacing it.
function main(config) { return routeMeter(config); }

function routeMeter(input) {
  const config = JSON.parse(JSON.stringify(input));
  const name = "RouteMeter Local Meter";
  const port = __ROUTEMETER_PORT__;
  const username = __ROUTEMETER_USER__;
  const password = __ROUTEMETER_PASSWORD__;
  const skip = new Set(["direct", "reject", "reject-drop", "pass", "dns"]);
  const reserved = ["dialer-proxy", "interface-name", "routing-mark"];
  function validate(options, label) {
    for (const field of reserved) {
      if (options[field] !== undefined && options[field] !== "" && !(field === "dialer-proxy" && options[field] === name))
        throw new Error("RouteMeter: " + label + " already uses " + field + ". Keep your original profile; integrate its outermost hop manually.");
    }
  }
  if (!Array.isArray(config.proxies)) config.proxies = [];
  let count = 0;
  for (const proxy of config.proxies) {
    if (!proxy || typeof proxy.type !== "string") throw new Error("RouteMeter: invalid proxy entry");
    if (proxy.name === name) {
      if (proxy.type !== "socks5" || proxy.server !== "127.0.0.1" || proxy.port !== port || proxy.username !== username || proxy.password !== password)
        throw new Error("RouteMeter: reserved proxy name is already in use");
      continue;
    }
    if (skip.has(proxy.type.toLowerCase())) continue;
    validate(proxy, proxy.name || "proxy");
    proxy["dialer-proxy"] = name;
    count++;
  }
  for (const [providerName, provider] of Object.entries(config["proxy-providers"] || {})) {
    if (!provider || typeof provider !== "object") throw new Error("RouteMeter: invalid proxy provider");
    const override = provider.override || {};
    validate(override, providerName);
    // Inline provider payloads must be checked too; never silently remove a chain.
    for (const proxy of provider.payload || []) validate(proxy, proxy.name || providerName);
    provider.override = Object.assign({}, override, { "dialer-proxy": name });
    count++;
  }
  if (count === 0) throw new Error("RouteMeter: no proxy nodes/providers found");
  if (!config.proxies.some(p => p.name === name))
    config.proxies.push({ name, type: "socks5", server: "127.0.0.1", port, username, password, udp: true });
  return config;
}
