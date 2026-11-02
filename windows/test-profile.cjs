const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
let code = fs.readFileSync(path.join(__dirname, 'RouteMeter.Windows/relay-profile.js'), 'utf8');
code = code.replace('__ROUTEMETER_PORT__', '18765').replace('__ROUTEMETER_USER__', '"test-user"').replace('__ROUTEMETER_PASSWORD__', '"test-only-password"');
const context = vm.createContext({}); vm.runInContext(code, context);
const run = config => JSON.parse(JSON.stringify(context.main(config)));
const input = { proxies: [{ name: 'proxy-a', type: 'ss', server: 'example.invalid', port: 443 }, { name: 'local-direct', type: 'direct' }], 'proxy-groups': [{ name: 'select', type: 'select', proxies: ['proxy-a', 'DIRECT'] }], rules: ['MATCH,select'], dns: { enable: true }, 'proxy-providers': { sample: { type: 'http', url: 'https://example.invalid/profile', override: { udp: true } } } };
const saved = JSON.stringify(input); const result = run(input);
assert.equal(JSON.stringify(input), saved, 'input is not mutated');
assert.deepEqual(result.rules, input.rules); assert.deepEqual(result['proxy-groups'], input['proxy-groups']); assert.deepEqual(result.dns, input.dns);
assert.equal(result.proxies[0]['dialer-proxy'], 'RouteMeter Local Meter'); assert.equal(result.proxies[1]['dialer-proxy'], undefined);
assert.equal(result['proxy-providers'].sample.override.udp, true); assert.equal(result['proxy-providers'].sample.override['dialer-proxy'], 'RouteMeter Local Meter');
assert.equal(result.proxies.at(-1).server, '127.0.0.1'); assert.equal(result.proxies.at(-1).udp, true);
assert.deepEqual(run(result), result, 'idempotent on reload');
for (const field of ['dialer-proxy', 'interface-name', 'routing-mark']) {
  const conflict = structuredClone(input); conflict.proxies[0][field] = 'existing-value';
  assert.throws(() => run(conflict), /already uses/, 'custom network chain is preserved by refusing integration');
}
const conflict = structuredClone(input); conflict['proxy-providers'].sample.override['dialer-proxy'] = 'existing-chain'; assert.throws(() => run(conflict), /already uses/);
assert.throws(() => run({ proxies: [{ name: 'RouteMeter Local Meter', type: 'ss' }] }), /reserved/);
assert.throws(() => run({ proxies: [{ name: 'only-direct', type: 'direct' }] }), /no proxy/);
console.log('PASS profile script: inline nodes, providers, direct bypass, rule preservation, credentials placeholders, idempotency, conflict refusal.');
