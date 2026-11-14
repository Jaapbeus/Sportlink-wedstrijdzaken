const assert = require('node:assert/strict');
const { ciBlock, parseProtocolFields } = require('./samenwerking-poort');
const { evaluateGate } = require('./claude-issue-turn-gate');
const { checkRequest } = require('./claude-issue-turn-review');
const shadow = { protocol_version: 1, mode: 'shadow', authority: 'local' };
const active = { ...shadow, mode: 'active' };
const sha = 'a'.repeat(40);
assert.equal(ciBlock({}, shadow), null);
assert.ok(ciBlock({}, active));
for (const bad of [null, {}, { ...shadow, protocol_version: 2 },
  { ...shadow, authority: 'ci' }, { ...shadow, mode: 'onbekend' }, { ...shadow, bypass: true }]) {
  assert.ok(ciBlock({}, bad));
}
for (const text of ['Request-ID: r1', 'Reviewer-sessie: claude-een',
  'Reviewroute: local', 'Protocol-versie: 1', 'Request-ID:',
  'Request-ID: een\nRequest-ID: twee']) {
  assert.ok(ciBlock(parseProtocolFields(text), shadow));
}
assert.equal(parseProtocolFields('Request-ID: een\nRequest-ID: twee').protocolInvalid, true);
(async () => {
  let writes = 0;
  const body = `Fase: review\nReviewer: Claude Code\nImplementer: Codex\nPR: #43\nAangeboden head-SHA: ${sha}\nRequest-ID: r1\nReviewer-sessie: claude-een\nReviewroute: local\nProtocol-versie: 1`;
  const github = {
    paginate: async (fn, args) => (await fn(args)).data,
    rest: {
      issues: {
        get: async () => ({ data: { state: 'open', author_association: 'OWNER', labels: [
          { name: 'turn: claude-code' }, { name: 'source: codex' }] } }),
        listComments: async () => ({ data: [{ id: 1, body, author_association: 'OWNER', created_at: '2026-01-01' }] }),
        createComment: async () => { writes++; }, addLabels: async () => { writes++; },
        removeLabel: async () => { writes++; },
      },
      pulls: { list: async () => ({ data: [] }) },
    },
  };
  const context = { repo: { owner: 'o', repo: 'r' }, eventName: 'issues', runId: 1 };
  const result = await evaluateGate({ github, context, core: { notice() {}, warning() {} }, issueNumber: 42 });
  assert.equal(result.proceed, false);
  assert.match(result.reason, /lokale reviewsessie/);
  const publication = await checkRequest({ github, context, issueNumber: 42, prNumber: 43, sha,
    expected: { commentId: 1, implementer: 'Codex' } });
  assert.match(publication, /lokale reviewsessie/);
  assert.equal(writes, 0, 'Geen overname, publicatie of labelwijziging bij een lokale route');
  console.log('Samenwerkingspoort: configuratie, routering, poort en publicatie groen.');
})().catch(error => { console.error(error); process.exitCode = 1; });
