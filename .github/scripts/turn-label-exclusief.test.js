// Unit-tests voor turn-label-exclusief.js (#1686) — zonder GitHub, token of netwerk.
// Uitvoeren: node .github/scripts/turn-label-exclusief.test.js   (exit 0 = alles groen)
// Wordt bij elke PR gedraaid door de job 'Build FunctionApp + BlazorAdmin' in build.yml.
const { kiesTeBehoudenLabel, maakTurnExclusief } = require('./turn-label-exclusief.js');

let failures = 0;
function check(name, actual, expected) {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a === e) console.log(`  PASS  ${name}`);
  else { console.log(`  FAIL  ${name}\n        verwacht: ${e}\n        kreeg:    ${a}`); failures++; }
}

const ctx = { repo: { owner: 'o', repo: 'r' } };
const core = { notice: () => {}, warning: () => {} };
const ev = (label, tijd, event = 'labeled') => ({ event, created_at: tijd, label: { name: label } });

function fakeGithub(labels, events, { removeStatus = {}, isPr = false } = {}) {
  const calls = { removed: [] };
  return {
    calls,
    paginate: async (fn, params) => (await fn(params)).data,
    rest: {
      issues: {
        get: async () => ({ data: { labels: labels.map((name) => ({ name })), pull_request: isPr ? {} : undefined } }),
        listEvents: async () => ({ data: events }),
        removeLabel: async ({ name }) => {
          const st = removeStatus[name];
          if (st) { const e = new Error('fout ' + st); e.status = st; throw e; }
          calls.removed.push(name);
        },
      },
    },
  };
}

async function verwachtFout(naam, promise, tekst) {
  let msg = null;
  try { await promise; } catch (e) { msg = e.message; }
  check(naam, msg !== null && msg.includes(tekst), true);
}

(async () => {
  console.log('kiesTeBehoudenLabel:');
  check('geen turn-label: null', kiesTeBehoudenLabel({ turnLabels: [] }), null);
  check('één turn-label: dat label', kiesTeBehoudenLabel({ turnLabels: ['turn: owner'] }), 'turn: owner');
  check('twee labels: het meest recente labeled-event wint',
    kiesTeBehoudenLabel({
      turnLabels: ['turn: owner', 'turn: codex'],
      events: [ev('turn: owner', '2026-10-12T10:36:36Z'), ev('turn: codex', '2026-10-12T10:43:54Z')],
    }), 'turn: codex');
  check('twee labels, omgekeerde volgorde: nog steeds het meest recente',
    kiesTeBehoudenLabel({
      turnLabels: ['turn: owner', 'turn: codex'],
      events: [ev('turn: codex', '2026-10-12T10:30:00Z'), ev('turn: owner', '2026-10-12T10:36:36Z')],
    }), 'turn: owner');
  check('unlabeled-events tellen niet mee',
    kiesTeBehoudenLabel({
      turnLabels: ['turn: owner', 'turn: codex'],
      events: [ev('turn: codex', '2026-10-12T10:30:00Z'), ev('turn: owner', '2026-10-12T10:50:00Z', 'unlabeled'), ev('turn: owner', '2026-10-12T10:36:36Z')],
    }), 'turn: owner');
  check('gelijke tijdstempel: het later genoemde event wint',
    kiesTeBehoudenLabel({
      turnLabels: ['turn: owner', 'turn: claude-code'],
      events: [ev('turn: claude-code', '2026-10-12T10:31:57Z'), ev('turn: owner', '2026-10-12T10:31:57Z')],
    }), 'turn: owner');
  check('zonder bruikbare events: het ontvangen label',
    kiesTeBehoudenLabel({ turnLabels: ['turn: owner', 'turn: codex'], events: [], ontvangen: 'turn: codex' }), 'turn: codex');

  console.log('\nmaakTurnExclusief:');
  let gh = fakeGithub(['turn: owner', 'turn: codex', 'type: bug'],
    [ev('turn: owner', '2026-10-12T10:36:36Z'), ev('turn: codex', '2026-10-12T10:43:54Z')]);
  let res = await maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1, ontvangenLabel: 'turn: codex' });
  check('twee labels: oudste verwijderd, nieuwste blijft', { res, removed: gh.calls.removed },
    { res: { behouden: 'turn: codex', verwijderd: ['turn: owner'] }, removed: ['turn: owner'] });

  gh = fakeGithub(['turn: codex', 'type: bug'], []);
  res = await maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1 });
  check('één label: niets te doen', { res, removed: gh.calls.removed }, { res: { behouden: 'turn: codex', verwijderd: [] }, removed: [] });

  gh = fakeGithub(['type: bug'], []);
  res = await maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1 });
  check('geen turn-label: niets te doen', res, { behouden: null, verwijderd: [] });

  gh = fakeGithub(['turn: owner', 'turn: codex'], [], { isPr: true });
  res = await maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1 });
  check('een PR wordt overgeslagen', { res, removed: gh.calls.removed }, { res: { behouden: null, verwijderd: [] }, removed: [] });

  gh = fakeGithub(['turn: owner', 'turn: codex', 'turn: claude-code'],
    [ev('turn: claude-code', '2026-10-12T10:00:00Z'), ev('turn: owner', '2026-10-12T10:10:00Z'), ev('turn: codex', '2026-10-12T10:20:00Z')]);
  res = await maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1 });
  check('drie labels: alleen het nieuwste blijft', { b: res.behouden, removed: gh.calls.removed.sort() },
    { b: 'turn: codex', removed: ['turn: claude-code', 'turn: owner'] });

  gh = fakeGithub(['turn: owner', 'turn: codex'],
    [ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:10:00Z')], { removeStatus: { 'turn: owner': 500 } });
  await verwachtFout('500 bij verwijderen: de helper faalt hard', maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1 }), 'turn: owner');

  gh = fakeGithub(['turn: owner', 'turn: codex'],
    [ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:10:00Z')], { removeStatus: { 'turn: owner': 404 } });
  res = await maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1 });
  check('404 (al afwezig) is idempotent', res, { behouden: 'turn: codex', verwijderd: ['turn: owner'] });

  console.log(failures === 0 ? '\nALLE TESTS GESLAAGD' : `\n${failures} TEST(S) GEFAALD`);
  process.exit(failures === 0 ? 0 : 1);
})();
