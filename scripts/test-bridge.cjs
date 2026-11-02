const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const template = fs.readFileSync('Sources/RouteMeter/Resources/statistics-push.js', 'utf8');
const source = template.replaceAll('__PORT__','18764').replaceAll('__LOCAL_KEY__','test-local-key');
const fields = {name:'direct',type:'tcp',rx_transfer:1,tx_transfer:2,rx_current_rate:3,tx_current_rate:4};
async function run(result, rejectFetch = false) {
  let done = 0, sent;
  const context = {
    $configuration: {sendMessage: async message => {
      assert.equal(message.action,'get_traffic_statistics'); return result;
    }},
    $task: {fetch: async request => { sent = request; if (rejectFetch) throw Error('offline'); return {}; }},
    $done: () => done++
  };
  vm.runInNewContext(source, context, {timeout:1000});
  await new Promise(resolve => setImmediate(resolve));
  assert.equal(done,1);
  return sent;
}
(async () => {
  const request = await run({ret:[{...fields,secret:'must-not-leave',host:'private.invalid'}]});
  assert.equal(request.url,'http://127.0.0.1:18764/statistics');
  assert.equal(request.headers['X-Route-Meter-Key'],'test-local-key');
  assert.deepEqual(JSON.parse(request.body).statistics,[fields]);
  assert.equal(await run({error:'unavailable'}),undefined);
  assert.equal(await run({ret:{unexpected:true}}),undefined);
  await run({ret:[fields]},true);
  console.log('PASS bridge allowlist, loopback target, authorization, API errors, unavailable receiver');
})().catch(error => { console.error(error); process.exit(1); });
