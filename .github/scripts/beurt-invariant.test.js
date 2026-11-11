// Unit-tests voor beurt-invariant.js (#1858) — zonder GitHub, token of netwerk.
// Uitvoeren: node .github/scripts/beurt-invariant.test.js   (exit 0 = alles groen)
// Wordt bij elke PR gedraaid door de job 'Build FunctionApp + BlazorAdmin' in build.yml.
const {
  SIGNAAL_MARKER, parseOverdracht, verwachteBeurt, evalueerIssue, isOverdracht, signaalBody, run,
} = require('./beurt-invariant.js');

let failures = 0;
function check(name, actual, expected) {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a === e) console.log(`  PASS  ${name}`);
  else { console.log(`  FAIL  ${name}\n        verwacht: ${e}\n        kreeg:    ${a}`); failures++; }
}

const SHA = 'a'.repeat(40);
const SHA2 = 'b'.repeat(40);
const NU = Date.parse('2026-11-11T12:00:00Z');
const minuten = (m) => new Date(NU - m * 60000).toISOString();
let volgId = 1;
const comment = (body, { min = 120, assoc = 'OWNER', login = 'jaap' } = {}) =>
  ({ id: volgId++, body, created_at: minuten(min), author_association: assoc, user: { login } });
const codes = (f) => f.map((x) => x.code);

// ── parseOverdracht ──
console.log('parseOverdracht:');
const nl = parseOverdracht(`**Fase:** review\n**Implementer:** Codex\n**Reviewer:** Claude Code\n**PR:** #1860\n**Aangeboden head-SHA:** \`${SHA}\`\n**Ronde:** 1 van 3\n**Volgende beurt:** Claude Code`);
check('Nederlandse regelvorm', nl, { fase: 'review', volgendeBeurt: 'Claude Code', reviewer: 'Claude Code', implementer: 'Codex', sha: SHA, pr: 1860, ronde: 1 });
const en = parseOverdracht(`Phase: review. Implementer: Codex. Reviewer: Claude Code. Round: 1 of 3. Frozen head: ${SHA}.`);
check('Engelse inline-vorm', en, { fase: 'review', volgendeBeurt: null, reviewer: 'Claude Code', implementer: 'Codex', sha: SHA, pr: null, ronde: 1 });
check('PR-verwijzing inline', parseOverdracht('Phase: processing; PR: #42; Next turn: Codex').pr, 42);
check('afgekapte SHA blijft ruw bewaard', parseOverdracht(`Fase: review\nAangeboden head-SHA: ${'c'.repeat(38)}`).sha, 'c'.repeat(38));
check('Offered head SHA, hoofdletterongevoelig', parseOverdracht(`phase: review\noffered head sha: ${SHA}`).sha, SHA);
check('comment zonder fase', parseOverdracht('Gewoon een opmerking over de fase van de maan').fase, null);

// ── verwachteBeurt ──
console.log('verwachteBeurt:');
check('expliciet Volgende beurt wint boven fase', verwachteBeurt({ fase: 'review', reviewer: 'Claude Code', volgendeBeurt: 'Codex' }), 'codex');
check('Next turn Claude Code', verwachteBeurt({ fase: 'x', volgendeBeurt: 'Claude Code' }), 'claude-code');
check('eigenaar', verwachteBeurt({ fase: 'x', volgendeBeurt: 'eigenaar' }), 'owner');
check('merge door implementer = geen', verwachteBeurt({ fase: 'x', volgendeBeurt: 'merge door implementer' }), 'geen');
check('fase review = reviewer', verwachteBeurt({ fase: 'review', reviewer: 'Claude Code' }), 'claude-code');
check('fase verwerking = implementer', verwachteBeurt({ fase: 'verwerking', implementer: 'Codex' }), 'codex');
check('fase processing = implementer', verwachteBeurt({ fase: 'processing', implementer: 'Claude Code' }), 'claude-code');
check('fase eigenaarsbesluit = owner', verwachteBeurt({ fase: 'eigenaarsbesluit' }), 'owner');
check('onbekende fase = null', verwachteBeurt({ fase: 'gemerged' }), null);
check('twee beurten in één tekst = onduidelijk', verwachteBeurt({ fase: 'x', volgendeBeurt: 'Codex of Claude Code' }), null);

// ── evalueerIssue ──
console.log('evalueerIssue:');
const ev = (o) => codes(evalueerIssue({ now: NU, graceMinuten: 30, prs: [], issueComments: [], labels: [], ...o }));
const aanvraagNl = `Fase: review\nImplementer: Codex\nReviewer: Claude Code\nPR: #7\nAangeboden head-SHA: ${SHA}\nVolgende beurt: Claude Code`;
const EIGENAARSBESLUIT = 'Fase: eigenaarsbesluit\nImplementer: Codex\nVolgende beurt: eigenaar';
const pr7 = (comments = []) => ({ number: 7, headSha: SHA, state: 'open', comments });

check('Nederlandse aanvraag, label klopt: geen bevinding', ev({ labels: ['turn: claude-code'], issueComments: [comment(aanvraagNl)], prs: [pr7()] }), []);
check('Engelse inline aanvraag, label klopt', ev({ labels: ['turn: claude-code'], prs: [pr7()],
  issueComments: [comment(`Phase: review. Implementer: Codex. Reviewer: Claude Code. Round: 1 of 3. Frozen head: ${SHA}. PR: #7`)] }), []);
check('aanvraag alleen op de PR, nieuwste issuecomment zegt processing/Codex: geen bevinding',
  ev({ labels: ['turn: claude-code'],
    issueComments: [comment('Phase: processing. Implementer: Codex. Next turn: Codex', { min: 300 })],
    prs: [pr7([comment(`Phase: review. Implementer: Codex. Reviewer: Claude Code. Round: 1 of 3. Frozen head: ${SHA}. PR: #7`, { min: 200 })])] }), []);
check('label claude-code, nieuwste overdracht zegt Codex, ouder dan grace: TURN_WIJKT_AF',
  ev({ labels: ['turn: claude-code'], issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 120 })] }), ['TURN_WIJKT_AF']);
check('zelfde afwijking binnen grace: geen bevinding',
  ev({ labels: ['turn: claude-code'], issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 5 })] }), []);
check('afwijking, overdracht oud maar label net gewisseld: geen bevinding',
  ev({ labels: ['turn: claude-code'], laatsteLabelWijziging: minuten(3),
    issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 120 })] }), []);
check('38-hex SHA: REVIEW_SHA_ONGELDIG',
  ev({ labels: ['turn: claude-code'], prs: [pr7()],
    issueComments: [comment(aanvraagNl.replace(SHA, 'a'.repeat(38)))] }), ['REVIEW_SHA_ONGELDIG']);
check('ontbrekende SHA: REVIEW_SHA_ONGELDIG',
  ev({ labels: ['turn: claude-code'], issueComments: [comment('Fase: review\nReviewer: Claude Code\nPR: #7')] }), ['REVIEW_SHA_ONGELDIG']);
check('head verschoven: REVIEW_SHA_ONGELDIG',
  ev({ labels: ['turn: claude-code'], issueComments: [comment(aanvraagNl)], prs: [{ ...pr7(), headSha: SHA2 }] }), ['REVIEW_SHA_ONGELDIG']);
check('status: waiting-codex: VEROUDERDE_STATUS', ev({ labels: ['status: waiting-codex'] }), ['VEROUDERDE_STATUS']);
check('twee turn-labels: MEERDERE_TURN', ev({ labels: ['turn: codex', 'turn: owner'], issueComments: [comment(aanvraagNl)], prs: [pr7()] }), ['MEERDERE_TURN']);
check('verwachte beurt maar geen turn-label: GEEN_TURN', ev({ labels: [], issueComments: [comment(EIGENAARSBESLUIT)] }), ['GEEN_TURN']);
check('awaiting-release zonder turn: geen GEEN_TURN',
  ev({ labels: ['status: awaiting-release'], issueComments: [comment(EIGENAARSBESLUIT)] }), []);
check('beurt "geen" (merge door implementer): geen verwachting', ev({ labels: ['turn: codex'], issueComments: [comment('Fase: review\nVolgende beurt: merge door implementer')] }), []);
check('onvertrouwde auteur genegeerd',
  ev({ labels: ['turn: claude-code'], issueComments: [
    comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 200 }),
    comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 100, assoc: 'NONE', login: 'vreemde' }),
    comment(aanvraagNl, { min: 150 }),
  ], prs: [pr7()] }), []);
check('onvertrouwde auteur alleen: geen overdracht, dus geen bevinding',
  ev({ labels: ['turn: claude-code'], issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { assoc: 'NONE', login: 'vreemde' })] }), []);
check('github-actions[bot] telt als vertrouwd',
  ev({ labels: ['turn: claude-code'], issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { assoc: 'NONE', login: 'github-actions[bot]' })] }), ['TURN_WIJKT_AF']);
check('eigen signaalcomment genegeerd',
  ev({ labels: ['turn: claude-code'], issueComments: [
    comment(aanvraagNl, { min: 200 }),
    comment(`${SIGNAAL_MARKER} hash=abc -->\nFase: verwerking\nVolgende beurt: Codex`, { min: 100, assoc: 'NONE', login: 'github-actions[bot]' }),
  ], prs: [pr7()] }), []);
check('gesloten PR telt niet mee',
  ev({ labels: ['turn: claude-code'], issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 200 })],
    prs: [{ ...pr7([comment(aanvraagNl, { min: 100 })]), state: 'closed' }] }), ['TURN_WIJKT_AF']);

// ── review-verwerking (P1) ──
console.log('strikte overdracht:');
const LABELCORRECTIE = 'Labelcorrectie: de turn-labels stonden niet goed. Bied een review aan met eerst een fasecomment (Phase: review, PR, Frozen head) en zet pas daarna turn: claude-code';
check('proza dat de velden noemt is geen fase', parseOverdracht(LABELCORRECTIE).fase, null);
check('Labelcorrectie nieuwer dan echte aanvraag: aanvraag blijft beslissend (head verschoven)',
  ev({ labels: ['turn: claude-code'], prs: [{ ...pr7(), headSha: SHA2 }],
    issueComments: [comment(aanvraagNl, { min: 200 }), comment(LABELCORRECTIE, { min: 100 })] }), ['REVIEW_SHA_ONGELDIG']);
check('Labelcorrectie nieuwer: label wijkt af van de echte aanvraag (Codex) blijft zichtbaar',
  ev({ labels: ['turn: claude-code'],
    issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 200 }), comment(LABELCORRECTIE, { min: 100 })] }), ['TURN_WIJKT_AF']);
check('fase zonder ander veld is geen overdracht', isOverdracht(parseOverdracht('Fase: verwerking')), false);
check('fase met onbekende beurt is geen overdracht', isOverdracht({ fase: 'iets', implementer: 'Codex' }), false);
check('eindfase gemerged met implementer is wel een overdracht', isOverdracht({ fase: 'gemerged', implementer: 'Codex' }), true);
check('kale Fase: verwerking maskeert de echte overdracht niet',
  ev({ labels: ['turn: claude-code'],
    issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 200 }), comment('Fase: verwerking', { min: 100 })] }), ['TURN_WIJKT_AF']);
check('fase aan het begin van een zin telt (inline)', parseOverdracht('Klaar. Fase: review. Reviewer: Claude Code').fase, 'review');

// ── GEEN_TURN: wachttijd en uitzonderingen (P2) ──
console.log('GEEN_TURN:');
check('fasecomment 30 seconden oud, nog geen label: geen bevinding', ev({ labels: [], issueComments: [{ ...comment(EIGENAARSBESLUIT), created_at: new Date(NU - 30000).toISOString() }] }), []);
check('GEEN_TURN ouder dan grace: bevinding', ev({ labels: [], issueComments: [comment(EIGENAARSBESLUIT, { min: 45 })] }), ['GEEN_TURN']);
check('GEEN_TURN: label net gewisseld (binnen grace): geen bevinding', ev({ labels: [], laatsteLabelWijziging: minuten(5), issueComments: [comment(EIGENAARSBESLUIT, { min: 120 })] }), []);
for (const st of ['blocked', 'wont-fix', 'waiting-owner']) {
  check(`status: ${st} zonder turn: geen GEEN_TURN`, ev({ labels: [`status: ${st}`], issueComments: [comment(EIGENAARSBESLUIT, { min: 120 })] }), []);
}

// ── PR-herkenning en verschoven head (P2) ──
console.log('verschoven head, issue-aanvraag:');
const proza = `Fase: review\nImplementer: Codex\nReviewer: Claude Code\nPR #7, issue #1\nAangeboden head-SHA: ${SHA}\nVolgende beurt: Claude Code`;
check('PR zonder dubbele punt wordt herkend', parseOverdracht(proza).pr, 7);
check('PR via /pull/-URL', parseOverdracht(`Fase: review\nReviewer: Claude Code\nzie https://github.com/o/r/pull/12`).pr, 12);
check('issue-aanvraag "PR #7" met verschoven head: REVIEW_SHA_ONGELDIG',
  ev({ labels: ['turn: claude-code'], issueComments: [comment(proza)], prs: [{ ...pr7(), headSha: SHA2 }] }), ['REVIEW_SHA_ONGELDIG']);
check('issue-aanvraag zonder PR-nummer, één gekoppelde PR met verschoven head',
  ev({ labels: ['turn: claude-code'], issueComments: [comment(proza.replace('PR #7, issue #1', 'issue #1'))], prs: [{ ...pr7(), headSha: SHA2 }] }), ['REVIEW_SHA_ONGELDIG']);
check('issue-aanvraag zonder PR-nummer, één PR met gelijke head: geen bevinding',
  ev({ labels: ['turn: claude-code'], issueComments: [comment(proza.replace('PR #7, issue #1', 'issue #1'))], prs: [pr7()] }), []);
const zonderPr = proza.replace('PR #7, issue #1', 'issue #1');
check('meerdere PR\'s, SHA past bij geen enkele head: bevinding',
  ev({ labels: ['turn: claude-code'], issueComments: [comment(zonderPr)], prs: [{ ...pr7(), headSha: SHA2 }, { number: 8, headSha: 'c'.repeat(40), state: 'open' }] }), ['REVIEW_SHA_ONGELDIG']);
check('meerdere PR\'s, SHA past bij één head: geen bevinding',
  ev({ labels: ['turn: claude-code'], issueComments: [comment(zonderPr)], prs: [pr7(), { number: 8, headSha: 'c'.repeat(40), state: 'open' }] }), []);

// ── signaalBody ──
console.log('signaalBody:');
const f1 = [{ code: 'TURN_WIJKT_AF', detail: 'label=a;verwacht=b', tekst: 'x' }];
const f2 = [{ code: 'TURN_WIJKT_AF', detail: 'label=a;verwacht=c', tekst: 'x' }];
const hashUit = (b) => (/hash=([0-9a-f]{64})/.exec(b) || [])[1];
check('marker aanwezig en hash is sha256', Boolean(hashUit(signaalBody(5, f1))), true);
check('hash stabiel voor dezelfde bevinding', hashUit(signaalBody(5, f1)), hashUit(signaalBody(9, f1)));
check('hash wijzigt bij ander detail', hashUit(signaalBody(5, f1)) === hashUit(signaalBody(5, f2)), false);
check('tekst meldt dat labels niet zijn gewijzigd', signaalBody(5, f1).includes('geen labels gewijzigd'), true);

// ── run ──
console.log('run:');
function fakeGithub({ issues, prs = [], comments = {}, recent = [], faalOp = null, events = [] }) {
  const calls = { createComment: [], addLabels: 0, removeLabel: 0, setLabels: 0 };
  const rest = {
    issues: {
      listForRepo: 'listForRepo', listComments: 'listComments', listCommentsForRepo: 'listCommentsForRepo', listEvents: 'listEvents',
      createComment: async (a) => { calls.createComment.push(a); comments[a.issue_number] = (comments[a.issue_number] || []).concat([{ id: volgId++, body: a.body, created_at: new Date(NU).toISOString(), author_association: 'NONE', user: { login: 'github-actions[bot]' } }]); return {}; },
      addLabels: async () => { calls.addLabels++; }, removeLabel: async () => { calls.removeLabel++; }, setLabels: async () => { calls.setLabels++; },
    },
    pulls: { list: 'pullsList' },
  };
  return {
    calls, rest,
    paginate: async (fn, a) => {
      if (fn === 'listForRepo') return issues;
      if (fn === 'pullsList') return prs;
      if (fn === 'listCommentsForRepo') return recent;
      if (fn === 'listEvents') return events;
      if (faalOp === a.issue_number) throw new Error('API-fout');
      return comments[a.issue_number] || [];
    },
  };
}
const ctx = { repo: { owner: 'o', repo: 'r' } };
const maakCore = () => { const c = { infos: [], warnings: [], failed: null, info: (m) => c.infos.push(m), warning: (m) => c.warnings.push(m), setFailed: (m) => { c.failed = m; } }; return c; };
const issue = (number, labels) => ({ number, title: 't', labels: labels.map((name) => ({ name })) });
const oudeAfwijking = comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 120 });

(async () => {
  {
    const gh = fakeGithub({ issues: [issue(1, ['turn: claude-code'])], comments: { 1: [oudeAfwijking] } });
    const core = maakCore();
    await run({ github: gh, context: ctx, core, now: new Date(NU) });
    check('afwijking: één signaalcomment', gh.calls.createComment.length, 1);
    check('signaalcomment noemt de marker', gh.calls.createComment[0].body.startsWith(SIGNAAL_MARKER), true);
    await run({ github: gh, context: ctx, core, now: new Date(NU) });
    check('dedup: zelfde hash, geen nieuwe comment', gh.calls.createComment.length, 1);
    // Andere afwijking op hetzelfde issue ⇒ andere hash ⇒ nieuwe comment.
    gh.rest.issues.listForRepo = 'listForRepo';
    const gh2 = fakeGithub({ issues: [issue(1, ['turn: claude-code', 'status: waiting-codex'])], comments: { 1: gh.calls.createComment.length ? [oudeAfwijking].concat([{ id: 99, body: gh.calls.createComment[0].body, created_at: minuten(1), author_association: 'NONE', user: { login: 'github-actions[bot]' } }]) : [] } });
    await run({ github: gh2, context: ctx, core, now: new Date(NU) });
    check('gewijzigde bevinding: nieuwe comment', gh2.calls.createComment.length, 1);
    check('run wijzigt nooit labels', [gh.calls, gh2.calls].map((c) => c.addLabels + c.removeLabel + c.setLabels), [0, 0]);
  }
  {
    // Terugkerende afwijking: signaal (hash H) -> opgelost -> zelfde afwijking opnieuw -> nieuwe comment.
    const signaal = (min) => ({ id: volgId++, body: signaalBody(1, [{ code: 'TURN_WIJKT_AF', detail: 'label=claude-code;verwacht=codex', tekst: 'x' }]), created_at: minuten(min), author_association: 'NONE', user: { login: 'github-actions[bot]' } });
    const labelEvent = (min) => ({ event: 'labeled', label: { name: 'turn: claude-code' }, created_at: minuten(min) });
    const lopend = fakeGithub({ issues: [issue(1, ['turn: claude-code'])], comments: { 1: [oudeAfwijking, signaal(100)] }, events: [labelEvent(150)] });
    await run({ github: lopend, context: ctx, core: maakCore(), now: new Date(NU) });
    check('zelfde hash, melding nieuwer dan overdracht en labelwissel: geen nieuwe comment', lopend.calls.createComment.length, 0);
    const terug = fakeGithub({ issues: [issue(1, ['turn: claude-code'])], comments: { 1: [oudeAfwijking, signaal(100)] }, events: [labelEvent(150), labelEvent(50)] });
    await run({ github: terug, context: ctx, core: maakCore(), now: new Date(NU) });
    check('zelfde hash maar label opnieuw gezet na de melding: nieuwe comment', terug.calls.createComment.length, 1);
    const nieuweOverdracht = fakeGithub({ issues: [issue(1, ['turn: claude-code'])], comments: { 1: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 60 }), signaal(100)] } });
    await run({ github: nieuweOverdracht, context: ctx, core: maakCore(), now: new Date(NU) });
    check('zelfde hash maar nieuwere overdracht dan de melding: nieuwe comment', nieuweOverdracht.calls.createComment.length, 1);
  }
  {
    const gh = fakeGithub({ issues: [issue(1, ['turn: claude-code'])], comments: { 1: [oudeAfwijking] } });
    const core = maakCore();
    await run({ github: gh, context: ctx, core, now: new Date(NU), dryRun: true });
    check('dryRun plaatst niets', gh.calls.createComment.length, 0);
  }
  {
    // Gekoppelde PR met de aanvraag; label klopt: geen comment. Strong ref via haakjes in de titel.
    const gh = fakeGithub({
      issues: [issue(1, ['turn: claude-code'])],
      prs: [{ number: 7, title: 'fix(#1): iets', body: '', head: { sha: SHA } }],
      comments: { 1: [comment('Fase: verwerking\nImplementer: Codex\nNext turn: Codex', { min: 300 })], 7: [comment(aanvraagNl, { min: 100 })] },
    });
    await run({ github: gh, context: ctx, core: maakCore(), now: new Date(NU) });
    check('aanvraag op gekoppelde PR gelezen: geen signaal', gh.calls.createComment.length, 0);
  }
  {
    const gh = fakeGithub({ issues: [issue(1, ['turn: codex']), issue(2, ['turn: claude-code'])], comments: { 1: [], 2: [oudeAfwijking] }, faalOp: 1 });
    const core = maakCore();
    await run({ github: gh, context: ctx, core, now: new Date(NU) });
    check('API-fout bij één issue: andere issue wordt wel beoordeeld', gh.calls.createComment.map((c) => c.issue_number), [2]);
    check('API-fout geeft waarschuwing en een falende run', [core.warnings.length, core.failed !== null], [1, true]);
  }
  {
    // Recente overdracht op een issue zonder turn-label: GEEN_TURN wordt toch gevonden.
    const c = comment(EIGENAARSBESLUIT, { min: 60 });
    const gh = fakeGithub({ issues: [issue(3, [])], comments: { 3: [c] }, recent: [{ ...c, issue_url: 'https://api.github.com/repos/o/r/issues/3' }] });
    await run({ github: gh, context: ctx, core: maakCore(), now: new Date(NU) });
    check('recente overdracht zonder label: GEEN_TURN gesignaleerd', gh.calls.createComment.length, 1);
  }

  console.log(failures === 0 ? '\nAlle checks geslaagd.' : `\n${failures} check(s) gefaald.`);
  process.exit(failures === 0 ? 0 : 1);
})();
