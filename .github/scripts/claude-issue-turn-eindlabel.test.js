// Unit-tests voor claude-issue-turn-eindlabel.js (#1692, #1702) — zonder GitHub, token of netwerk.
// Uitvoeren: node .github/scripts/claude-issue-turn-eindlabel.test.js   (exit 0 = alles groen)
// Draait bij elke PR in de job 'Build FunctionApp + BlazorAdmin' (build.yml), naast de poorttest.

const { applyEndLabel, requestedEndLabel, ownerOnlyReason, WAIT_COMMENT_HEADING } = require('./claude-issue-turn-eindlabel.js');

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
  base: { ref: 'develop' }, changed_files: 1,
  state: 'open', merged_at: null, closed_at: null, updated_at: '2026-10-12T14:45:00Z', ...extra,
});

/**
 * Nep-octokit. `states` is een lijst toestanden die issues.get achtereenvolgens teruggeeft (de laatste
 * blijft gelden), zodat een verandering tússen controle en schrijven te simuleren is. `removeErrors`
 * laat removeLabel alleen voor de genoemde labelnaam falen, zodat precies één DELETE (bijvoorbeeld
 * de terugdraai-DELETE van het eigen eindlabel) een fout geeft en de andere slagen.
 */
function fakeGithub({
  states = [{ labels: ['turn: claude-code', 'status: in-progress'] }],
  comments = [bot('2026-10-12T14:48:00Z', 'Fase: review\nVolgende beurt: turn: codex')],
  startPr = null, closedPrs = [], removeErrors = {},
  files = [{ filename: 'BlazorAdmin/Pages/Teams.razor.cs' }],
} = {}) {
  const calls = { added: [], removed: [], removeAttempts: [], gets: 0, comments: [] };
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
        createComment: async ({ body }) => { calls.comments.push(body); },
        addLabels: async ({ labels }) => { calls.added.push(...labels); },
        removeLabel: async ({ name }) => {
          calls.removeAttempts.push(name);
          if (removeErrors[name]) throw removeErrors[name];
          calls.removed.push(name);
        },
      },
      pulls: {
        get: async () => ({ data: startPr }),
        listFiles: async () => ({ data: files }),
        list: async ({ state, page }) => ({ data: state === 'closed' && page === 1 ? closedPrs : [] }),
      },
    },
  };
}

// Toestanden: vóór het schrijven claude-code, daarna het eigen eindlabel.
const after = (label) => [{ labels: ['turn: claude-code'] }, { labels: [label] }];

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
  check('#1702: merge door implementer is een geldige uitkomst', requestedEndLabel([bot('2026-10-12T14:50:00Z', 'Fase: verwerking\nVolgende beurt: merge door implementer')], START), { label: 'merge door implementer', source: 'beurtregel' });
  check('#1702: merge door implementer met backticks', requestedEndLabel([bot('2026-10-12T14:50:00Z', 'Volgende beurt: `merge door implementer`')], START).label, 'merge door implementer');
  check('#1702: andere vrije tekst blijft ongeldig → owner', requestedEndLabel([bot('2026-10-12T14:50:00Z', 'Volgende beurt: merge door eigenaar')], START).label, 'turn: owner');
  check('regel midden in een zin telt niet', requestedEndLabel([bot('2026-10-12T14:50:00Z', 'Zet daarna Volgende beurt: turn: codex')], START).label, 'turn: owner');

  console.log('\napplyEndLabel — toestand ongewijzigd:');
  {
    const github = fakeGithub({ startPr: pr(501), states: after('turn: codex') });
    const r = await run(github, { startPrNumber: 501 });
    check('ongewijzigd: geschreven', [r.written, r.label], [true, 'turn: codex']);
    check('ongewijzigd: codex toegevoegd', github.calls.added, ['turn: codex']);
    check('ongewijzigd: claude-code verwijderd', github.calls.removed, ['turn: claude-code']);
  }
  {
    const github = fakeGithub({ comments: [], states: after('turn: owner') });
    const r = await run(github);
    check('geen beurtregel: owner geschreven', [r.written, r.label, r.source], [true, 'turn: owner', 'geen beurtregel gevonden']);
  }
  {
    // Een PR die al vóór de run gesloten was (zonder merge) blokkeert niet.
    const github = fakeGithub({ closedPrs: [pr(490, { state: 'closed', closed_at: '2026-10-11T10:00:00Z' })], states: after('turn: codex') });
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
        { labels: ['turn: codex', 'status: awaiting-release'] }, // controle ná schrijven
      ],
    });
    const r = await run(github);
    check('merge tijdens schrijven: teruggedraaid', [r.written, r.rolledBack], [false, true]);
    check('merge tijdens schrijven: eigen label weer weg', github.calls.removed, ['turn: claude-code', 'turn: codex']);
  }
  {
    const github = fakeGithub({ states: [{ labels: ['turn: claude-code'] }, { labels: ['turn: codex'] }] });
    const r = await run(github);
    check('na schrijven ongewijzigd: niet teruggedraaid', [r.written, r.rolledBack], [true, undefined]);
  }

  {
    // Review P2-1: een beurtwijziging na de voorcontrole mag niet worden overschreven.
    const github = fakeGithub({ states: [{ labels: ['turn: claude-code'] }, { labels: ['turn: owner'] }] });
    const r = await run(github);
    check('beurt gewijzigd na voorcontrole: niet als geschreven gemeld', r.written, false);
    check('beurt gewijzigd: owner-label blijft staan', github.calls.removed.filter((n) => n === 'turn: owner'), []);
    check('beurt gewijzigd: reden vermeldt turn-label', /turn-label/.test(r.reason), true);
  }
  {
    // Review P2-2 (ronde 2): de eerste DELETE (`turn: claude-code`) slaagt, de nacontrole ziet
    // awaiting-release en uitsluitend de terugdraai-DELETE van het eigen eindlabel geeft HTTP 500.
    // Die fout moet propageren; geen succesmelding voor het terugdraaien.
    const github = fakeGithub({
      states: [{ labels: ['turn: claude-code'] }, { labels: ['turn: codex', 'status: awaiting-release'] }],
      removeErrors: { 'turn: codex': Object.assign(new Error('boom'), { status: 500 }) },
    });
    let thrown = null, result;
    try { result = await run(github); } catch (err) { thrown = err; }
    check('rollback-500: beide DELETE-pogingen gedaan', github.calls.removeAttempts, ['turn: claude-code', 'turn: codex']);
    check('rollback-500: alleen claude-code daadwerkelijk verwijderd', github.calls.removed, ['turn: claude-code']);
    check('rollback-500: fout propageert met status 500', thrown?.status, 500);
    check('rollback-500: geen resultaat met rolledBack', result, undefined);
  }
  {
    // 404 op de terugdraai-DELETE: het label is al weg, dus idempotent succes.
    const github = fakeGithub({
      states: [{ labels: ['turn: claude-code'] }, { labels: ['turn: codex', 'status: awaiting-release'] }],
      removeErrors: { 'turn: codex': Object.assign(new Error('nf'), { status: 404 }) },
    });
    const r = await run(github);
    check('rollback-404: beide DELETE-pogingen gedaan', github.calls.removeAttempts, ['turn: claude-code', 'turn: codex']);
    check('rollback-404: idempotent teruggedraaid', [r.written, r.rolledBack], [false, true]);
  }

  console.log('\napplyEndLabel — wacht op merge door de implementer (#1702):');
  const waitBot = [bot('2026-10-12T14:48:00Z', 'Fase: verwerking\nVolgende beurt: merge door implementer')];
  {
    const github = fakeGithub({
      comments: waitBot, startPr: pr(501),
      states: [{ labels: ['turn: claude-code', 'status: in-progress'] }, { labels: ['status: in-progress'] }],
    });
    const r = await run(github, { startPrNumber: 501 });
    check('wacht op merge: vastgelegd', [r.written, r.label], [true, 'merge door implementer']);
    check('wacht op merge: geen enkel label toegevoegd', github.calls.added, []);
    check('wacht op merge: alleen claude-code verwijderd', github.calls.removed, ['turn: claude-code']);
    check('wacht op merge: één comment', github.calls.comments.length, 1);
    check('wacht op merge: comment noemt PR en kop', github.calls.comments[0].includes(`**${WAIT_COMMENT_HEADING}** (PR #501)`), true);
    check('wacht op merge: comment bevat geen beurtregel', /^\s*Volgende beurt:/m.test(github.calls.comments[0]), false);
  }
  const waitToOwner = async (name, opts, extra, sourcePart) => {
    const github = fakeGithub({ comments: waitBot, states: after('turn: owner'), ...opts });
    const r = await run(github, extra);
    check(`${name}: owner geschreven`, [r.written, r.label], [true, 'turn: owner']);
    check(`${name}: owner-label toegevoegd`, github.calls.added, ['turn: owner']);
    check(`${name}: geen wacht-comment`, github.calls.comments, []);
    check(`${name}: reden in source`, (r.source || '').includes(sourcePart), true);
  };
  await waitToOwner('zonder start-PR (implementatiefase)', {}, {}, 'geen gekoppelde PR');
  await waitToOwner('PR naar main', { startPr: pr(501, { base: { ref: 'main' } }) }, { startPrNumber: 501 }, 'niet naar `develop`');
  await waitToOwner('raakt AGENTS.md', { startPr: pr(501), files: [{ filename: 'FunctionApp/AGENTS.md' }] }, { startPrNumber: 501 }, 'werkinstructies');
  await waitToOwner('raakt een skill', { startPr: pr(501), files: [{ filename: '.agents/skills/autonoom/SKILL.md' }] }, { startPrNumber: 501 }, 'skills');
  await waitToOwner('raakt een migratie', { startPr: pr(501), files: [{ filename: 'Database.Postgres/migrations/V99__x.sql' }] }, { startPrNumber: 501 }, 'databaseschema');
  await waitToOwner('raakt SQL Server-schema', { startPr: pr(501), files: [{ filename: 'Database/dbo/Tables/X.sql' }] }, { startPrNumber: 501 }, 'databaseschema');
  await waitToOwner('raakt Azure-infra', { startPr: pr(501), files: [{ filename: 'infrastructure/main.bicep' }] }, { startPrNumber: 501 }, 'Azure');
  await waitToOwner('hernoemd vanuit een eigenaarspad', { startPr: pr(501), files: [{ filename: 'docs/x.md', previous_filename: 'CLAUDE.md' }] }, { startPrNumber: 501 }, 'werkinstructies');
  await waitToOwner('onvolledige bestandslijst', { startPr: pr(501, { changed_files: 5 }) }, { startPrNumber: 501 }, 'onvolledig');
  {
    // Toestand veranderd vóór het vastleggen: niets doen, ook geen comment.
    const github = fakeGithub({ comments: waitBot, startPr: pr(501), states: [{ labels: ['turn: owner'] }] });
    const r = await run(github, { startPrNumber: 501 });
    check('wacht op merge, beurt gewijzigd: niets geschreven', [r.written, github.calls.removed, github.calls.comments], [false, [], []]);
  }
  {
    // Iemand zet tussen het weghalen en de nacontrole een label: geen comment, label blijft staan.
    const github = fakeGithub({
      comments: waitBot, startPr: pr(501),
      states: [{ labels: ['turn: claude-code'] }, { labels: ['turn: codex'] }],
    });
    const r = await run(github, { startPrNumber: 501 });
    check('wacht op merge, label erbij na weghalen: niet als vastgelegd gemeld', r.written, false);
    check('wacht op merge, label erbij: geen comment', github.calls.comments, []);
    check('wacht op merge, label erbij: andermans label niet verwijderd', github.calls.removed, ['turn: claude-code']);
    check('wacht op merge, label erbij: reden gemeld', /turn-label/.test(r.reason), true);
  }
  {
    // Merge tussen weghalen en nacontrole: geen comment (de PR is al gemerged).
    const github = fakeGithub({
      comments: waitBot, startPr: pr(501),
      states: [{ labels: ['turn: claude-code'] }, { labels: ['status: awaiting-release'] }],
    });
    const r = await run(github, { startPrNumber: 501 });
    check('wacht op merge, merge in het venster: geen comment', [r.written, github.calls.comments], [false, []]);
  }
  console.log('\napplyEndLabel — reviewfase (#1727):');
  {
    const codexPr = pr(724, { head: { ref: `codex/${ISSUE}-werk`, repo: { full_name: 'o/r' } } });
    const github = fakeGithub({
      comments: [bot('2026-10-12T14:48:00Z', 'Fase: verwerking\nUitkomst: geen bevindingen\nVolgende beurt: turn: codex')],
      startPr: codexPr, states: after('turn: codex'),
    });
    const r = await run(github, { startPrNumber: 724, phase: 'review' });
    check('review: beurt terug naar Codex', [r.written, r.label, github.calls.added], [true, 'turn: codex', ['turn: codex']]);
  }
  {
    const github = fakeGithub({ comments: waitBot, startPr: pr(724), states: after('turn: owner') });
    const r = await run(github, { startPrNumber: 724, phase: 'review' });
    check('review: merge door implementer wordt owner', [r.written, r.label, github.calls.comments], [true, 'turn: owner', []]);
    check('review: reden in source', /reviewrun/.test(r.source), true);
  }

  {
    // #1727 ronde 1 (P2): label uit de reviewpublicatie, gebonden aan de beoordeelde SHA.
    const sha = 'a'.repeat(40);
    const codexPr = pr(724, { head: { ref: `codex/${ISSUE}-werk`, sha, repo: { full_name: 'o/r' } } });
    const ok = fakeGithub({ comments: [], startPr: codexPr, states: after('turn: codex') });
    const r1 = await run(ok, { startPrNumber: 724, phase: 'review', requestedLabel: 'turn: codex', expectedSha: sha });
    check('reviewpublicatie: label zonder agentcomment', [r1.written, r1.label, r1.source], [true, 'turn: codex', 'reviewpublicatie']);
    const gewijzigd = fakeGithub({
      comments: [], states: after('turn: codex'),
      startPr: pr(724, { head: { ref: `codex/${ISSUE}-werk`, sha: 'b'.repeat(40), repo: { full_name: 'o/r' } } }),
    });
    const r2 = await run(gewijzigd, { startPrNumber: 724, phase: 'review', requestedLabel: 'turn: codex', expectedSha: sha });
    check('reviewpublicatie: head gewijzigd → niets geschreven', [r2.written, gewijzigd.calls.added, gewijzigd.calls.removed], [false, [], []]);
    check('reviewpublicatie: reden noemt de SHA', /beoordeelde SHA/.test(r2.reason), true);
    const ongeldig = fakeGithub({ comments: [], startPr: codexPr, states: after('turn: owner') });
    const r3 = await run(ongeldig, { startPrNumber: 724, requestedLabel: 'turn: claude-code', expectedSha: sha });
    check('reviewpublicatie: ongeldig label wordt owner', [r3.written, r3.label], [true, 'turn: owner']);
  }

  console.log('\nownerOnlyReason:');
  check('gewone code: geen uitzondering', ownerOnlyReason('Planner.Shared/X.cs'), null);
  check('workflow is geen uitzondering meer (#1690)', ownerOnlyReason('.github/workflows/build.yml'), null);
  check('docs met AGENTS in de naam: geen uitzondering', ownerOnlyReason('docs/AGENTS-UITLEG.md'), null);
  check('geneste CLAUDE.md: werkinstructies', (ownerOnlyReason('FunctionApp/CLAUDE.md') || '').startsWith('werkinstructies'), true);

  // ---------- #1721 fase 1: testmodus schrijft niets ----------
  console.log('\ndryRun (#1721):');
  {
    const github = fakeGithub({ comments: [bot('2026-10-12T14:48:00Z', 'Volgende beurt: turn: codex')] });
    const r = await applyEndLabel({ github, context: ctx, core, issueNumber: ISSUE, startPrNumber: null, startedAt: START, dryRun: true });
    check('dryRun: berekent het label, schrijft niets', [r.written, r.dryRun, r.label], [false, true, 'turn: codex']);
    check('dryRun: geen label toegevoegd of verwijderd, geen comment', [github.calls.added, github.calls.removed, github.calls.comments], [[], [], []]);
    const echt = fakeGithub({ comments: [bot('2026-10-12T14:48:00Z', 'Volgende beurt: turn: codex')], states: [{ labels: ['turn: claude-code'] }, { labels: ['turn: codex'] }] });
    const r2 = await applyEndLabel({ github: echt, context: ctx, core, issueNumber: ISSUE, startPrNumber: null, startedAt: START });
    check('zonder dryRun: schrijft wel', [r2.written, echt.calls.added], [true, ['turn: codex']]);
  }
  {
    const github = fakeGithub({ comments: [bot('2026-10-12T14:48:00Z', 'Volgende beurt: merge door implementer')], states: [{ labels: ['turn: claude-code'] }] });
    const r = await applyEndLabel({ github, context: ctx, core, issueNumber: ISSUE, startPrNumber: null, startedAt: START, dryRun: true });
    check('dryRun bij "wacht op merge": geen label en geen comment', [github.calls.removed, github.calls.comments], [[], []]);
  }

  // De vergelijk-en-zet werkt alleen als de agent zelf geen labelpad heeft en de job altijd draait.
  console.log('\nclaude-issue-turn.yml (#1692):');
  {
    const yml = require('fs').readFileSync(require('path').join(__dirname, '../workflows/claude-issue-turn.yml'), 'utf8');
    check('agent heeft geen gh issue edit in --allowedTools', /Bash\(gh issue edit/.test(yml), false);
    check('prompt laat de agent geen label zetten', /--add-label|--remove-label/.test(yml), false);
    check('job eindlabel draait ook na een falende agent', /\n  eindlabel:[\s\S]*?\n    if: always\(\) && needs\.gate\.outputs\.proceed == 'true' && needs\.gate\.outputs\.phase != 'review'\n/.test(yml), true);

    // #1727 (Codex-review ronde 1, P1): de reviewjob is read-only buiten de uitgecheckte code om.
    const job = (naam) => {
      const m = yml.match(new RegExp(`\\n  ${naam}:\\n([\\s\\S]*?)(?=\\n  [a-z-]+:\\n|$)`));
      return m ? m[1] : '';
    };
    const review = job('review');
    const allowed = (review.match(/--allowedTools "([^"]*)"/) || [])[1] || '';
    check('reviewjob gevonden', review.length > 0, true);
    check('reviewjob: workflowtoken i.p.v. App-token', /github_token: \$\{\{ github\.token \}\}/.test(review), true);
    check('reviewjob: geen id-token (geen OIDC-ruil)', /id-token:/.test(review), false);
    check('reviewjob: geen enkel schrijfrecht', /:\s*write\b/.test((review.match(/permissions:\n((?:      .*\n)+)/) || [])[1] || 'write'), false);
    check('reviewjob: geen instellingen uit de checkout', /--setting-sources user\n/.test(review), true);
    check('reviewjob: geen schrijvende of publicerende tools',
      /\bEdit\b|\bWrite\b|gh pr comment|gh issue comment|git commit|git push|git add|dotnet|node |python3|bash /.test(allowed), false);
    const schema = (review.match(/--json-schema '([^']*)'/) || [])[1] || '';
    check('reviewjob: JSON-schema gelijk aan REVIEW_SCHEMA',
      schema && JSON.stringify(JSON.parse(schema)) === JSON.stringify(require('./claude-issue-turn-review.js').REVIEW_SCHEMA), true);
    const publiceren = job('review-publiceren');
    check('publicatiejob draait op de default branch (geen ref)', /persist-credentials: false/.test(publiceren) && !/\bref:/.test(publiceren), true);
    check('publicatiejob gebruikt claude-issue-turn-review.js', /claude-issue-turn-review\.js/.test(publiceren), true);
    check('implementatiejob laadt ook geen instellingen uit de checkout', /--setting-sources user\n/.test(job('claude')), true);
    check('job eindlabel gebruikt de vergelijk-en-zet', /claude-issue-turn-eindlabel\.js/.test(yml), true);
    check('poort geeft de starttijd door', /started_at: \$\{\{ steps\.evaluate\.outputs\.started_at \}\}/.test(yml), true);
    // #1702: een schone verwerkingsronde eindigt niet meer standaard bij de eigenaar.
    check('prompt kent de uitkomst merge door implementer', /Volgende beurt: merge door implementer/.test(yml), true);
    check('prompt geeft geen-codewijziging niet meer blind aan de eigenaar', /Geen codewijziging nodig \(geen bevindingen, of alle "niet overgenomen"\)/.test(yml), false);

    // #1702 ronde 1 (Codex P2): de beurtkeuze in de verwerkingsfase moet eenduidig zijn. Een schone
    // tweede ronde (geen bevindingen, geen wijziging, geen eigenaarspunt) mag niet óók aan "tweede
    // ronde → turn: owner" voldoen. Model van de prompt: de takken van stap 5 in volgorde, met per tak
    // de uitkomst (laatste `Volgende beurt:` in de tak).
    const start = yml.indexOf('Bepaal daarna de volgende beurt');
    const end = yml.indexOf('Tel de rondes', start);
    const block = start >= 0 && end > start ? yml.slice(start, end) : '';
    const branches = block.split(/\n\s+- /).slice(1).map((text) => {
      const beurten = [...text.matchAll(/Volgende beurt: ([a-z: ]+?)`/g)].map((m) => m[1]);
      return { text: text.replace(/\s+/g, ' '), outcome: beurten[beurten.length - 1] || null };
    });
    check('verwerkingsblok gevonden met takken', branches.length >= 4, true);
    check('prompt legt een voorrangsvolgorde vast (eerste tak die geldt)', /eerste tak die geldt/i.test(block.replace(/\s+/g, ' ')), true);
    const mergeIdx = branches.findIndex((b) => b.outcome === 'merge door implementer');
    const rondeOwner = branches.map((b, i) => ({ ...b, i })).filter((b) => b.outcome === 'turn: owner' && /tweede (review)?ronde/i.test(b.text));
    check('er is een tak voor tweede ronde → turn: owner', rondeOwner.length, 1);
    check('tak tweede ronde → owner vereist het ontbreken van een schone review',
      rondeOwner.every((b) => /(geen|zonder) schone review/i.test(b.text)), true);
    check('merge door implementer gaat in de volgorde vóór tweede ronde → owner',
      mergeIdx >= 0 && rondeOwner.every((b) => b.i > mergeIdx), true);
    check('rondenummer staat niet als losse óf-voorwaarde naast het eigenaarspunt',
      branches.some((b) => /eigenaarspunt/.test(b.text) && /óf dit was al de tweede reviewronde/.test(b.text)), false);
    check('schone-reviewtak noemt dat het rondenummer niet meetelt',
      mergeIdx >= 0 && /ongeacht het rondenummer/i.test(branches[mergeIdx].text), true);
  }

  console.log(failures === 0 ? '\nAlle checks geslaagd.' : `\n${failures} check(s) gefaald.`);
  process.exit(failures === 0 ? 0 : 1);
})();
