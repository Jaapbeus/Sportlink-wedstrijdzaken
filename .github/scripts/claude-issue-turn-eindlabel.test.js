// Unit-tests voor claude-issue-turn-eindlabel.js (#1692) — zonder GitHub, token of netwerk.
// Uitvoeren: node .github/scripts/claude-issue-turn-eindlabel.test.js   (exit 0 = alles groen)
// Draait bij elke PR in de job 'Build FunctionApp + BlazorAdmin' (build.yml), naast de poorttest.

const { applyEndLabel, requestedEndLabel } = require('./claude-issue-turn-eindlabel.js');

let failures = 0;
function check(name, actual, expected) {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a === e) { console.log(`  PASS  ${name}`); }
  else { console.log(`  FAIL  ${name}\n        verwacht: ${e}\n        kreeg:    ${a}`); failures++; }
}

const ctx = { repo: { owner: 'o', repo: 'r' }, runId: 1 };
const core = { notice: () => {}, warning: () => {} };
const START = '2026-10-12T14:40:00Z';
const ISSUE = 1675;

const bot = (created_at, body) => ({
  user: { login: 'claude[bot]', type: 'Bot' }, author_association: 'NONE', created_at, body,
});
const pr = (number, extra = {}) => ({
  number, title: `fix(#${ISSUE}): werk`, body: '', author_association: 'OWNER',
  head: { ref: `feature/#${ISSUE}-werk`, repo: { full_name: 'o/r' } },
  state: 'open', merged_at: null, closed_at: null, updated_at: '2026-10-12T14:45:00Z', ...extra,
});

/**
 * Nep-octokit. `states` is een lijst toestanden die issues.get achtereenvolgens teruggeeft (de laatste
 * blijft gelden), zodat een verandering tússen controle en schrijven te simuleren is.
 */
function fakeGithub({
  states = [{ labels: ['turn: claude-code', 'status: in-progress'] }],
  comments = [bot('2026-10-12T14:48:00Z', 'Fase: review\nVolgende beurt: turn: codex')],
  startPr = null, closedPrs = [],
} = {}) {
  const calls = { added: [], removed: [], gets: 0 };
  return {
    calls,
    paginate: async (fn, params) => (await fn(params)).data,
    rest: {
      issues: {
        get: async () => {
          const s = states[Math.min(calls.gets, states.length - 1)];
          calls.gets += 1;
          return { data: { state: 'open', created_at: '2026-10-01T00:00:00Z', ...s, labels: s.labels.map((name) => ({ name })) } };
        },
        listComments: async () => ({ data: comments }),
        addLabels: async ({ labels }) => { calls.added.push(...labels); },
        removeLabel: async ({ name }) => { calls.removed.push(name); },
      },
      pulls: {
        get: async () => ({ data: startPr }),
        list: async ({ state, page }) => ({ data: state === 'closed' && page === 1 ? closedPrs : [] }),
      },
    },
  };
}

const run = (github, extra = {}) => applyEndLabel({
  github, context: ctx, core, issueNumber: ISSUE, startedAt: START, ...extra,
});

(async () => {
  console.log('requestedEndLabel:');
  check('beurtregel codex van de bot', requestedEndLabel([bot('2026-10-12T14:50:00Z', 'x\nVolgende beurt: turn: codex')], START).label, 'turn: codex');
  check('beurtregel met backticks', requestedEndLabel([bot('2026-10-12T14:50:00Z', 'Volgende beurt: `turn: owner`')], START).label, 'turn: owner');
  check('nieuwste comment wint', requestedEndLabel([
    bot('2026-10-12T14:50:00Z', 'Volgende beurt: turn: codex'),
    bot('2026-10-12T14:55:00Z', 'Volgende beurt: turn: owner'),
  ], START).label, 'turn: owner');
  check('comment van vóór de run telt niet', requestedEndLabel([bot('2026-10-12T14:00:00Z', 'Volgende beurt: turn: codex')], START), { label: 'turn: owner', source: 'geen beurtregel gevonden' });
  check('mens met beurtregel telt niet', requestedEndLabel([{
    user: { login: 'eigenaar', type: 'User' }, author_association: 'OWNER', created_at: '2026-10-12T14:50:00Z', body: 'Volgende beurt: turn: codex',
  }], START).label, 'turn: owner');
  check('nep-bot (type User) telt niet', requestedEndLabel([{
    user: { login: 'claude[bot]', type: 'User' }, author_association: 'NONE', created_at: '2026-10-12T14:50:00Z', body: 'Volgende beurt: turn: codex',
  }], START).label, 'turn: owner');
  check('beurt aan zichzelf is ongeldig → owner', requestedEndLabel([bot('2026-10-12T14:50:00Z', 'Volgende beurt: turn: claude-code')], START).label, 'turn: owner');
  check('regel midden in een zin telt niet', requestedEndLabel([bot('2026-10-12T14:50:00Z', 'Zet daarna Volgende beurt: turn: codex')], START).label, 'turn: owner');

  console.log('\napplyEndLabel — toestand ongewijzigd:');
  {
    const github = fakeGithub({ startPr: pr(501) });
    const r = await run(github, { startPrNumber: 501 });
    check('ongewijzigd: geschreven', [r.written, r.label], [true, 'turn: codex']);
    check('ongewijzigd: codex toegevoegd', github.calls.added, ['turn: codex']);
    check('ongewijzigd: claude-code verwijderd', github.calls.removed, ['turn: claude-code']);
  }
  {
    const github = fakeGithub({ comments: [] });
    const r = await run(github);
    check('geen beurtregel: owner geschreven', [r.written, r.label, r.source], [true, 'turn: owner', 'geen beurtregel gevonden']);
  }
  {
    // Een PR die al vóór de run gesloten was (zonder merge) blokkeert niet.
    const github = fakeGithub({ closedPrs: [pr(490, { state: 'closed', closed_at: '2026-10-11T10:00:00Z' })] });
    const r = await run(github);
    check('oude afgewezen PR: geschreven', r.written, true);
  }

  console.log('\napplyEndLabel — toestand veranderd (niets schrijven):');
  const blocked = async (name, opts, extra, reasonPart) => {
    const github = fakeGithub(opts);
    const r = await run(github, extra);
    check(`${name}: niet geschreven`, r.written, false);
    check(`${name}: geen labelwijziging`, [github.calls.added, github.calls.removed], [[], []]);
    check(`${name}: reden gemeld`, (r.reason || '').includes(reasonPart), true);
  };
  await blocked('PR van de start gemerged (#1675)', { startPr: pr(501, { state: 'closed', merged_at: '2026-10-12T14:49:37Z' }) }, { startPrNumber: 501 }, 'gemerged');
  await blocked('PR van de start gesloten', { startPr: pr(501, { state: 'closed', closed_at: '2026-10-12T14:49:37Z' }) }, { startPrNumber: 501 }, 'gesloten');
  await blocked('awaiting-release', { states: [{ labels: ['turn: claude-code', 'status: awaiting-release'] }] }, {}, 'awaiting-release');
  await blocked('turn-label opgeruimd', { states: [{ labels: ['status: in-progress'] }] }, {}, 'niet meer exclusief');
  await blocked('turn-label gewisseld naar owner', { states: [{ labels: ['turn: owner'] }] }, {}, 'niet meer exclusief');
  await blocked('tweede turn-label erbij', { states: [{ labels: ['turn: claude-code', 'turn: codex'] }] }, {}, 'niet meer exclusief');
  await blocked('issue gesloten', { states: [{ state: 'closed', labels: ['turn: claude-code'] }] }, {}, 'niet meer open');
  await blocked('door de bot geopende PR intussen gemerged', {
    closedPrs: [pr(510, { state: 'closed', merged_at: '2026-10-12T14:49:37Z', closed_at: '2026-10-12T14:49:37Z' })],
  }, {}, 'gemerged');
  await blocked('gekoppelde PR tijdens de run gesloten', {
    closedPrs: [pr(511, { state: 'closed', closed_at: '2026-10-12T14:49:00Z' })],
  }, {}, 'tijdens de run gesloten');
  await blocked('starttijd ontbreekt', {}, { startedAt: '' }, 'starttijd');

  console.log('\napplyEndLabel — verandering tijdens het schrijven:');
  {
    const github = fakeGithub({
      states: [
        { labels: ['turn: claude-code'] }, // controle vóór schrijven
        { labels: ['turn: claude-code'] }, // setTurnLabel leest zelf ook
        { labels: ['turn: codex', 'status: awaiting-release'] }, // controle ná schrijven
      ],
    });
    const r = await run(github);
    check('merge tijdens schrijven: teruggedraaid', [r.written, r.rolledBack], [false, true]);
    check('merge tijdens schrijven: eigen label weer weg', github.calls.removed, ['turn: claude-code', 'turn: codex']);
  }
  {
    const github = fakeGithub({ states: [{ labels: ['turn: claude-code'] }, { labels: ['turn: claude-code'] }, { labels: ['turn: codex'] }] });
    const r = await run(github);
    check('na schrijven ongewijzigd: niet teruggedraaid', [r.written, r.rolledBack], [true, undefined]);
  }

  // De vergelijk-en-zet werkt alleen als de agent zelf geen labelpad heeft en de job altijd draait.
  console.log('\nclaude-issue-turn.yml (#1692):');
  {
    const yml = require('fs').readFileSync(require('path').join(__dirname, '../workflows/claude-issue-turn.yml'), 'utf8');
    check('agent heeft geen gh issue edit in --allowedTools', /Bash\(gh issue edit/.test(yml), false);
    check('prompt laat de agent geen label zetten', /--add-label|--remove-label/.test(yml), false);
    check('job eindlabel draait ook na een falende agent', /\n  eindlabel:[\s\S]*?\n    if: always\(\) && needs\.gate\.outputs\.proceed == 'true'\n/.test(yml), true);
    check('job eindlabel gebruikt de vergelijk-en-zet', /claude-issue-turn-eindlabel\.js/.test(yml), true);
    check('poort geeft de starttijd door', /started_at: \$\{\{ steps\.evaluate\.outputs\.started_at \}\}/.test(yml), true);
  }

  console.log(failures === 0 ? '\nAlle checks geslaagd.' : `\n${failures} check(s) gefaald.`);
  process.exit(failures === 0 ? 0 : 1);
})();
