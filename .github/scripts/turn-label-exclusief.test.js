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

// Stateful: removeLabel/addLabel wijzigen de labels. `hooks.naGet(n, labels)` mag per get-aanroep
// (1-gebaseerd) de labels wijzigen, om een andere schrijver tussen twee lezingen na te bootsen;
// `hooks.naRemove(naam, labels)` idem na een verwijdering.
function fakeGithub(labels, events, { removeStatus = {}, isPr = false, hooks = {} } = {}) {
  const calls = { removed: [], gets: 0 };
  const huidig = [...labels];
  return {
    calls,
    huidig,
    paginate: async (fn, params) => (await fn(params)).data,
    rest: {
      issues: {
        get: async () => {
          calls.gets += 1;
          if (hooks.naGet) hooks.naGet(calls.gets, huidig);
          return { data: { labels: huidig.map((name) => ({ name })), pull_request: isPr ? {} : undefined } };
        },
        listEvents: async () => ({ data: events }),
        removeLabel: async ({ name }) => {
          const st = removeStatus[name];
          if (st) {
            // 404 = het label was al weg (dan staat het ook niet meer op het issue)
            if (st === 404) { const j = huidig.indexOf(name); if (j >= 0) huidig.splice(j, 1); }
            const e = new Error('fout ' + st); e.status = st; throw e;
          }
          const i = huidig.indexOf(name);
          if (i >= 0) huidig.splice(i, 1);
          calls.removed.push(name);
          if (hooks.naRemove) hooks.naRemove(name, huidig);
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


  console.log('\nraces (Codex r1 #1687):');
  // (a) een label komt bij tússen de eerste labellezing en de events: het ontbreekt in de events
  //     (nog niet zichtbaar) en moet als nieuwste blijven staan; het oudere label gaat weg.
  {
    const events = [ev('turn: owner', '2026-10-12T10:00:00Z')];
    const gh = fakeGithub(['turn: owner', 'turn: claude-code'], events, {
      hooks: { naGet: (n, labels) => { if (n === 2 && !labels.includes('turn: codex')) labels.push('turn: codex'); } },
    });
    // eerste get ziet owner+claude-code (2 labels); na de events komt er codex bij
    const res = await maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1 });
    check('label tussen lezingen: het nieuwste (zonder event) blijft, de rest is weg', { b: res.behouden, over: gh.huidig }, { b: 'turn: codex', over: ['turn: codex'] });
  }
  // (b) na de verwijdering zet een andere schrijver (zonder vervolgrun, bijv. via GITHUB_TOKEN) nog een label bij:
  //     de hercontrole ziet twee labels, doet een nieuwe poging en meldt pas succes bij exclusiviteit.
  {
    let toegevoegd = false;
    const events = [ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:05:00Z')];
    const gh = fakeGithub(['turn: owner', 'turn: codex'], events, {
      hooks: { naRemove: (naam, labels) => { if (!toegevoegd) { toegevoegd = true; labels.push('turn: claude-code'); } } },
    });
    const res = await maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1 });
    check('schrijver na de verwijdering: hercontrole herstelt exclusiviteit (precies één label)', gh.huidig.filter((n) => n.startsWith('turn: ')).length, 1);
    check('hercontrole meldt het resterende label', res.behouden !== null, true);
  }
  // (c) een schrijver blijft labels toevoegen: na het maximum aantal pogingen faalt de run (geen vals succes).
  {
    const events = [ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:05:00Z')];
    let teller = 0;
    const gh = fakeGithub(['turn: owner', 'turn: codex'], events, {
      hooks: { naRemove: (naam, labels) => { teller += 1; labels.push('turn: nieuw-' + teller); } },
    });
    await verwachtFout('blijvend niet exclusief: de helper faalt hard', maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1, maxPogingen: 3 }), 'niet exclusief');
  }
  // (d) kies: label zonder event is het nieuwste
  check('kies: label zonder event wint van gelabelde',
    kiesTeBehoudenLabel({ turnLabels: ['turn: owner', 'turn: codex'], events: [ev('turn: owner', '2026-10-12T10:00:00Z')] }), 'turn: codex');

  console.log(failures === 0 ? '\nALLE TESTS GESLAAGD' : `\n${failures} TEST(S) GEFAALD`);
  process.exit(failures === 0 ? 0 : 1);
})();
