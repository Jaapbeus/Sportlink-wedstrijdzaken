// Unit-tests voor turn-label-exclusief.js (#1686) — zonder GitHub, token of netwerk.
// Uitvoeren: node .github/scripts/turn-label-exclusief.test.js   (exit 0 = alles groen)
// Wordt bij elke PR gedraaid door de job 'Build FunctionApp + BlazorAdmin' in build.yml.
const helper = require('./turn-label-exclusief.js');
const { maakTurnExclusief } = helper;

// Een exceptie in de helper wordt per scenario een FAIL in plaats van een afgebroken suite: zo meldt
// elk scenario zijn eigen uitkomst (ook bij mutatietests).
const kiesTeBehoudenLabel = (args) => { try { return helper.kiesTeBehoudenLabel(args); } catch (e) { return `EXCEPTIE: ${e.message}`; } };
async function draai(args) {
  try { return await maakTurnExclusief(args); } catch (e) { return { exceptie: e.message }; }
}

let failures = 0;
function check(name, actual, expected) {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a === e) console.log(`  PASS  ${name}`);
  else { console.log(`  FAIL  ${name}\n        verwacht: ${e}\n        kreeg:    ${a}`); failures++; }
}

const ctx = { repo: { owner: 'o', repo: 'r' } };
const core = { notice: () => {}, warning: () => {} };
let volgId = 1;
const ev = (label, tijd, event = 'labeled') => ({ id: volgId++, event, created_at: tijd, label: { name: label } });
const turns = (labels) => labels.filter((n) => n.startsWith('turn: '));

// Stateful nabootsing van GitHub: labels én events. Elke labelwijziging (door de helper of door een
// "andere schrijver" via st.voegToe/st.haalWeg) voegt een event toe, zoals GitHub dat doet; een
// verwijdering van een afwezig label geeft 404. Hooks bootsen een andere schrijver na op een exact moment:
//   naGet(n, st)      na de n-de labellezing (de teruggegeven lezing bevat de wijziging nog niet)
//   naEvents(n, st)   na de n-de eventlezing (idem)
//   voorRemove(naam, st)  vlak vóór een verwijdering door de helper
// `vertraagdeEvents` = events die GitHub wel heeft verwerkt maar (nog) niet in de eventlijst toont.
function fakeGithub(labels, events, { removeStatus = {}, isPr = false, hooks = {}, vertraagdeEvents = false } = {}) {
  const calls = { removed: [], gets: 0, eventReads: 0 };
  const st = { labels: [...labels], events: [...events], klok: Date.parse('2026-10-12T12:00:00Z') };
  const nu = () => { st.klok += 1000; return new Date(st.klok).toISOString(); };
  st.voegToe = (naam, { zichtbaar = true } = {}) => {
    if (st.labels.includes(naam)) return;
    st.labels.push(naam);
    if (zichtbaar) st.events.push(ev(naam, nu()));
  };
  st.haalWeg = (naam, { zichtbaar = true } = {}) => {
    const i = st.labels.indexOf(naam);
    if (i < 0) return false;
    st.labels.splice(i, 1);
    if (zichtbaar) st.events.push(ev(naam, nu(), 'unlabeled'));
    return true;
  };
  return {
    calls,
    st,
    paginate: async (fn, params) => (await fn(params)).data,
    rest: {
      issues: {
        get: async () => {
          calls.gets += 1;
          const data = { labels: st.labels.map((name) => ({ name })), pull_request: isPr ? {} : undefined };
          if (hooks.naGet) hooks.naGet(calls.gets, st);
          return { data };
        },
        listEvents: async () => {
          calls.eventReads += 1;
          const data = st.events.map((e) => ({ ...e }));
          if (hooks.naEvents) hooks.naEvents(calls.eventReads, st);
          return { data };
        },
        removeLabel: async ({ name }) => {
          if (hooks.voorRemove) hooks.voorRemove(name, st);
          const status = removeStatus[name];
          if (status) { const e = new Error('fout ' + status); e.status = status; throw e; }
          if (!st.haalWeg(name, { zichtbaar: !vertraagdeEvents })) {
            const e = new Error('Label does not exist'); e.status = 404; throw e;
          }
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
  if (msg === null || !msg.includes(tekst)) console.log(`        foutmelding: ${msg}`);
}

(async () => {
  console.log('kiesTeBehoudenLabel (zuiver; consistente momentopname vereist):');
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
  check('gelijke tijdstempel: het later genoemde event wint',
    kiesTeBehoudenLabel({
      turnLabels: ['turn: owner', 'turn: claude-code'],
      events: [ev('turn: claude-code', '2026-10-12T10:31:57Z'), ev('turn: owner', '2026-10-12T10:31:57Z')],
    }), 'turn: owner');
  check('de LAATSTE toevoeging telt, niet een historische (verwijderd en opnieuw gezet)',
    kiesTeBehoudenLabel({
      turnLabels: ['turn: owner', 'turn: claude-code'],
      events: [ev('turn: claude-code', '2026-10-12T09:00:00Z'), ev('turn: owner', '2026-10-12T10:00:00Z'),
        ev('turn: claude-code', '2026-10-12T10:05:00Z', 'unlabeled'), ev('turn: claude-code', '2026-10-12T11:00:00Z')],
    }), 'turn: claude-code');
  check('twijfel — label staat er, maar het laatste event zegt verwijderd: null (niets kiezen)',
    kiesTeBehoudenLabel({
      turnLabels: ['turn: owner', 'turn: claude-code'],
      events: [ev('turn: claude-code', '2026-10-12T09:00:00Z'), ev('turn: claude-code', '2026-10-12T09:30:00Z', 'unlabeled'), ev('turn: owner', '2026-10-12T10:00:00Z')],
    }), null);
  check('twijfel — label zonder enig event: null (een ontbrekend event bewijst geen recentheid)',
    kiesTeBehoudenLabel({ turnLabels: ['turn: owner', 'turn: codex'], events: [ev('turn: owner', '2026-10-12T10:00:00Z')] }), null);
  check('twijfel — events tonen een label dat er niet (meer) staat: null',
    kiesTeBehoudenLabel({
      turnLabels: ['turn: owner', 'turn: codex'],
      events: [ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:01:00Z'), ev('turn: claude-code', '2026-10-12T10:02:00Z')],
    }), null);

  console.log('\nmaakTurnExclusief:');
  let gh = fakeGithub(['turn: owner', 'turn: codex', 'type: bug'],
    [ev('turn: owner', '2026-10-12T10:36:36Z'), ev('turn: codex', '2026-10-12T10:43:54Z')]);
  let res = await draai({ github: gh, context: ctx, core, issueNumber: 1, ontvangenLabel: 'turn: codex' });
  check('twee labels: oudste verwijderd, nieuwste blijft', { res, removed: gh.calls.removed, over: gh.st.labels },
    { res: { behouden: 'turn: codex', verwijderd: ['turn: owner'] }, removed: ['turn: owner'], over: ['turn: codex', 'type: bug'] });

  gh = fakeGithub(['turn: codex', 'type: bug'], []);
  res = await draai({ github: gh, context: ctx, core, issueNumber: 1 });
  check('één label: niets te doen (geen events nodig)', { res, removed: gh.calls.removed, eventReads: gh.calls.eventReads },
    { res: { behouden: 'turn: codex', verwijderd: [] }, removed: [], eventReads: 0 });

  gh = fakeGithub(['type: bug'], []);
  res = await draai({ github: gh, context: ctx, core, issueNumber: 1 });
  check('geen turn-label: niets te doen', res, { behouden: null, verwijderd: [] });

  gh = fakeGithub(['turn: owner', 'turn: codex'], [], { isPr: true });
  res = await draai({ github: gh, context: ctx, core, issueNumber: 1 });
  check('een PR wordt overgeslagen', { res, removed: gh.calls.removed }, { res: { behouden: null, verwijderd: [] }, removed: [] });

  gh = fakeGithub(['turn: owner', 'turn: codex', 'turn: claude-code'],
    [ev('turn: claude-code', '2026-10-12T10:00:00Z'), ev('turn: owner', '2026-10-12T10:10:00Z'), ev('turn: codex', '2026-10-12T10:20:00Z')]);
  res = await draai({ github: gh, context: ctx, core, issueNumber: 1 });
  check('drie labels: alleen het nieuwste blijft', { b: res.behouden, removed: [...gh.calls.removed].sort(), over: turns(gh.st.labels) },
    { b: 'turn: codex', removed: ['turn: claude-code', 'turn: owner'], over: ['turn: codex'] });

  gh = fakeGithub(['turn: owner', 'turn: codex'],
    [ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:10:00Z')], { removeStatus: { 'turn: owner': 500 } });
  await verwachtFout('500 bij verwijderen: de helper faalt hard', maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1 }), 'turn: owner');

  {
    // 404: een andere schrijver haalde het label net vóór onze verwijdering weg.
    const gh = fakeGithub(['turn: owner', 'turn: codex'],
      [ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:10:00Z')],
      { hooks: { voorRemove: (naam, st) => { if (naam === 'turn: owner') st.haalWeg('turn: owner'); } } });
    const res = await draai({ github: gh, context: ctx, core, issueNumber: 1 });
    check('404 (al afwezig) is idempotent', { res, over: turns(gh.st.labels) },
      { res: { behouden: 'turn: codex', verwijderd: ['turn: owner'] }, over: ['turn: codex'] });
  }

  console.log('\nraces (Codex r1 en r2 #1687):');
  // Codex r2 (P2): historische labeled/unlabeled-events voor claude-code; claude-code wordt opnieuw gezet
  // vlak ná het ophalen van de events. Dat nieuwste label mag niet verdwijnen; exclusiviteit blijft vereist.
  {
    const events = [ev('turn: claude-code', '2026-10-12T09:00:00Z'), ev('turn: claude-code', '2026-10-12T09:10:00Z', 'unlabeled'),
      ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:05:00Z')];
    const gh = fakeGithub(['turn: owner', 'turn: codex'], events, {
      hooks: { naEvents: (n, st) => { if (n === 1) st.voegToe('turn: claude-code'); } },
    });
    const res = await draai({ github: gh, context: ctx, core, issueNumber: 1 });
    check('r2: opnieuw gezet label na de eventlezing blijft staan, als enige', { b: res.behouden, over: turns(gh.st.labels) },
      { b: 'turn: claude-code', over: ['turn: claude-code'] });
    check('r2: het opnieuw gezette label is nooit verwijderd', gh.calls.removed.includes('turn: claude-code'), false);
  }
  // Variant: dezelfde hernieuwde toevoeging, maar tússen de labellezing en de eventlezing.
  {
    const events = [ev('turn: claude-code', '2026-10-12T09:00:00Z'), ev('turn: claude-code', '2026-10-12T09:10:00Z', 'unlabeled'),
      ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:05:00Z')];
    const gh = fakeGithub(['turn: owner', 'turn: codex'], events, {
      hooks: { naGet: (n, st) => { if (n === 2) st.voegToe('turn: claude-code'); } },
    });
    const res = await draai({ github: gh, context: ctx, core, issueNumber: 1 });
    check('r2-variant: toevoeging tussen labels en events: nieuwste blijft, als enige', { b: res.behouden, over: turns(gh.st.labels), weg: gh.calls.removed.includes('turn: claude-code') },
      { b: 'turn: claude-code', over: ['turn: claude-code'], weg: false });
  }
  // Achterlopende eventlijst: het label is opnieuw gezet, maar de events tonen alleen de oude toevoeging
  // en verwijdering. Dat is twijfel: niets verwijderen, opnieuw lezen, en na het maximum hard falen.
  {
    const events = [ev('turn: claude-code', '2026-10-12T09:00:00Z'), ev('turn: claude-code', '2026-10-12T09:10:00Z', 'unlabeled'),
      ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:05:00Z')];
    const gh = fakeGithub(['turn: owner', 'turn: codex', 'turn: claude-code'], events);
    await verwachtFout('achterlopende events: harde fout', maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1 }), 'niet exclusief');
    check('achterlopende events: niets verwijderd', { removed: gh.calls.removed, over: turns(gh.st.labels) },
      { removed: [], over: ['turn: owner', 'turn: codex', 'turn: claude-code'] });
  }
  // Codex r1 (a): een label komt bij tussen de eerste labellezing en de events.
  {
    const gh = fakeGithub(['turn: owner', 'turn: claude-code'],
      [ev('turn: claude-code', '2026-10-12T09:50:00Z'), ev('turn: owner', '2026-10-12T10:00:00Z')], {
        hooks: { naGet: (n, st) => { if (n === 1) st.voegToe('turn: codex'); } },
      });
    const res = await draai({ github: gh, context: ctx, core, issueNumber: 1 });
    check('r1a: label tussen lezingen: het nieuwste blijft, de rest is weg', { b: res.behouden, over: turns(gh.st.labels) }, { b: 'turn: codex', over: ['turn: codex'] });
  }
  // Codex r1 (b): na onze verwijdering zet een andere schrijver nog een label bij (zonder vervolgrun):
  // de hercontrole ziet twee labels en houdt het nieuwste.
  {
    let toegevoegd = false;
    const gh = fakeGithub(['turn: owner', 'turn: codex'],
      [ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:05:00Z')], {
        hooks: { naEvents: (n, st) => { if (n === 1 && !toegevoegd) { toegevoegd = true; st.voegToe('turn: claude-code'); } } },
      });
    const res = await draai({ github: gh, context: ctx, core, issueNumber: 1 });
    check('r1b: schrijver tijdens de run: precies één label, het nieuwste', { b: res.behouden, over: turns(gh.st.labels) },
      { b: 'turn: claude-code', over: ['turn: claude-code'] });
  }
  // Codex r1 (c): een schrijver blijft labels toevoegen: na het maximum faalt de run (geen vals succes).
  {
    let teller = 0;
    const gh = fakeGithub(['turn: owner', 'turn: codex'],
      [ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:05:00Z')], {
        hooks: { voorRemove: (naam, st) => { teller += 1; st.voegToe('turn: nieuw-' + teller); } },
      });
    await verwachtFout('r1c: blijvend niet exclusief: de helper faalt hard', maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1, maxPogingen: 3 }), 'niet exclusief');
  }
  // Restvenster: een label dat wij gaan verwijderen wordt vlak vóór onze verwijdering weggehaald én
  // opnieuw gezet. Wij verwijderen dan mogelijk die nieuwe toevoeging. Niet vast te stellen wie de
  // laatste schrijver was ⇒ geen stil succes, maar een harde fout met het label in de melding.
  {
    const gh = fakeGithub(['turn: owner', 'turn: codex'],
      [ev('turn: owner', '2026-10-12T10:00:00Z'), ev('turn: codex', '2026-10-12T10:05:00Z')], {
        hooks: { voorRemove: (naam, st) => { if (naam === 'turn: owner' && !st.gedaan) { st.gedaan = true; st.haalWeg('turn: owner'); st.voegToe('turn: owner'); } } },
      });
    await verwachtFout('restvenster: mogelijk een nieuwe toevoeging verwijderd ⇒ harde fout', maakTurnExclusief({ github: gh, context: ctx, core, issueNumber: 1 }), 'turn: owner');
  }

  console.log(failures === 0 ? '\nALLE TESTS GESLAAGD' : `\n${failures} TEST(S) GEFAALD`);
  process.exit(failures === 0 ? 0 : 1);
})();
