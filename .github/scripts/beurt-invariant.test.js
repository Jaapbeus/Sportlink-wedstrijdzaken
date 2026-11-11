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
const comment = (body, { min = 120, assoc = 'OWNER', login = 'jaap', type = 'User' } = {}) =>
  ({ id: volgId++, body, created_at: minuten(min), author_association: assoc, user: { login, type } });
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
check('leidende beurt wint van namen in de toelichting', verwachteBeurt({ fase: 'review', reviewer: 'Claude Code', volgendeBeurt: 'turn: owner (reviewroute beschikbaar stellen aan Claude Code)' }), 'owner');
check('turn:-voorvoegsel Codex', verwachteBeurt({ fase: 'x', volgendeBeurt: 'turn: codex' }), 'codex');
check('expliciete maar onduidelijke Volgende beurt valt niet terug op de reviewer', verwachteBeurt({ fase: 'review', reviewer: 'Claude Code', volgendeBeurt: 'nader te bepalen' }), null);
check('eindfase met reviewer in de fasetekst geeft geen beurt', verwachteBeurt({ fase: 'gemerged na review', reviewer: 'Claude Code' }), null);

// ── evalueerIssue ──
console.log('evalueerIssue:');
const ev = (o) => codes(evalueerIssue({ now: NU, graceMinuten: 30, prs: [], issueComments: [], labels: [], ...o }));
const aanvraagNl = `Fase: review\nImplementer: Codex\nReviewer: Claude Code\nPR: #7\nAangeboden head-SHA: ${SHA}\nVolgende beurt: Claude Code`;
const EIGENAARSBESLUIT = 'Fase: eigenaarsbesluit\nImplementer: Codex\nVolgende beurt: eigenaar';
const pr7 = (comments = []) => ({ number: 7, headSha: SHA, state: 'open', comments });
check('nieuwste overdracht met onduidelijke Volgende beurt maskeert oudere overdracht (geen vals alarm)', ev({
  labels: ['turn: owner'],
  issueComments: [
    comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 180 }),
    comment('Fase: eigenaarsbesluit\nImplementer: Codex\nVolgende beurt: nader te bepalen', { min: 90 }),
  ],
}), []);

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
check('beurt "geen" (merge door implementer), geen label: geen bevinding', ev({ labels: [], issueComments: [comment('Fase: review\nVolgende beurt: merge door implementer')] }), []);
check('onvertrouwde auteur genegeerd',
  ev({ labels: ['turn: claude-code'], issueComments: [
    comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 200 }),
    comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 100, assoc: 'NONE', login: 'vreemde' }),
    comment(aanvraagNl, { min: 150 }),
  ], prs: [pr7()] }), []);
check('onvertrouwde auteur alleen: geen overdracht, dus geen bevinding',
  ev({ labels: ['turn: claude-code'], issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { assoc: 'NONE', login: 'vreemde' })] }), []);
check('github-actions[bot] (type Bot) telt als vertrouwd',
  ev({ labels: ['turn: claude-code'], issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { assoc: 'NONE', login: 'github-actions[bot]', type: 'Bot' })] }), ['TURN_WIJKT_AF']);
check('eigen signaalcomment genegeerd',
  ev({ labels: ['turn: claude-code'], issueComments: [
    comment(aanvraagNl, { min: 200 }),
    comment(`${SIGNAAL_MARKER} hash=abc -->\nFase: verwerking\nVolgende beurt: Codex`, { min: 100, assoc: 'NONE', login: 'github-actions[bot]', type: 'Bot' }),
  ], prs: [pr7()] }), []);
check('gesloten PR telt niet mee',
  ev({ labels: ['turn: claude-code'], issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 200 })],
    prs: [{ ...pr7([comment(aanvraagNl, { min: 100 })]), state: 'closed' }] }), ['TURN_WIJKT_AF']);

// ── vertrouwde bots (tweede review, bevinding 1) ──
console.log('vertrouwde bots:');
const BOT = { assoc: 'NONE', login: 'claude[bot]', type: 'Bot' };
const REVIEW_CODEX = `Fase: review\nImplementer: Claude Code\nReviewer: Codex\nPR: #7\nAangeboden head-SHA: ${SHA}\nVolgende beurt: turn: codex`;
check('claude[bot] (type Bot): overdracht telt, label wijkt af', ev({ labels: ['turn: claude-code'], prs: [pr7()], issueComments: [comment(REVIEW_CODEX, { ...BOT })] }), ['TURN_WIJKT_AF']);
check('claude[bot] (type Bot): label klopt', ev({ labels: ['turn: codex'], prs: [pr7()], issueComments: [comment(REVIEW_CODEX, { ...BOT })] }), []);
check('claude[bot] eigenaarsbesluit, label wijkt af', ev({ labels: ['turn: codex'], issueComments: [comment('Fase: eigenaarsbesluit\nImplementer: Claude Code\nVolgende beurt: turn: owner', { ...BOT })] }), ['TURN_WIJKT_AF']);
check('gebruiker "claude[bot]" met type User is niet vertrouwd', ev({ labels: ['turn: claude-code'], prs: [pr7()], issueComments: [comment(REVIEW_CODEX, { ...BOT, type: 'User' })] }), []);
check('gebruiker "github-actions[bot]" met type User is niet vertrouwd', ev({ labels: ['turn: claude-code'], issueComments: [comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { assoc: 'NONE', login: 'github-actions[bot]', type: 'User' })] }), []);

// ── leidende beurt en onduidelijke Volgende beurt (bevinding 2) ──
console.log('beurt uit Volgende beurt:');
const REVIEWROUTE = 'Fase: review\nReviewer: Claude Code\nImplementer: Codex\nVolgende beurt: turn: owner (reviewroute beschikbaar stellen aan Claude Code)';
check('leidende owner, label owner: geen bevinding', ev({ labels: ['turn: owner'], issueComments: [comment(REVIEWROUTE)] }), []);
check('leidende owner, label claude-code: TURN_WIJKT_AF', ev({ labels: ['turn: claude-code'], issueComments: [comment(REVIEWROUTE)] }), ['TURN_WIJKT_AF']);
check('onduidelijke Volgende beurt: geen verwachting, dus geen TURN_WIJKT_AF', ev({ labels: ['turn: codex'], issueComments: [comment('Fase: review\nReviewer: Claude Code\nVolgende beurt: nader te bepalen')] }), []);

// ── SHA-controle voor beide reviewers (bevinding 3) ──
console.log('SHA-controle:');
const REVIEW_CODEX_SHA = (sha) => REVIEW_CODEX.replace(SHA, sha);
const shaDetail = (o) => evalueerIssue({ now: NU, graceMinuten: 30, prs: [], issueComments: [], labels: [], ...o }).filter((f) => f.code === 'REVIEW_SHA_ONGELDIG').map((f) => f.detail);
check('Reviewer Codex, 38-hex SHA: REVIEW_SHA_ONGELDIG', ev({ labels: ['turn: codex'], prs: [pr7()], issueComments: [comment(REVIEW_CODEX_SHA('a'.repeat(38)))] }), ['REVIEW_SHA_ONGELDIG']);
check('Reviewer Codex, volledige SHA: geen bevinding', ev({ labels: ['turn: codex'], prs: [pr7()], issueComments: [comment(REVIEW_CODEX)] }), []);
check('Reviewer Codex, head verschoven: REVIEW_SHA_ONGELDIG', ev({ labels: ['turn: codex'], prs: [{ ...pr7(), headSha: SHA2 }], issueComments: [comment(REVIEW_CODEX)] }), ['REVIEW_SHA_ONGELDIG']);
check('38-hex zonder gekoppelde PR: reden=lengte', shaDetail({ labels: ['turn: claude-code'], issueComments: [comment(aanvraagNl.replace(SHA, 'a'.repeat(38)))] }), ['reden=lengte']);
check('ontbrekende SHA: reden=ontbreekt', shaDetail({ labels: ['turn: claude-code'], issueComments: [comment('Fase: review\nReviewer: Claude Code\nPR: #7')] }), ['reden=ontbreekt']);
check('head verschoven: reden=verouderd', shaDetail({ labels: ['turn: claude-code'], issueComments: [comment(aanvraagNl)], prs: [{ ...pr7(), headSha: SHA2 }] }), ['reden=verouderd']);

// ── TURN_BIJ_EINDE en MEERDERE_TURN met wachttijd (bevinding 4) ──
console.log('einde en wachttijd:');
const MERGE_DOOR_IMPLEMENTER = 'Fase: review\nVolgende beurt: merge door implementer';
check('merge door implementer, label blijft staan: TURN_BIJ_EINDE', ev({ labels: ['turn: codex'], issueComments: [comment(MERGE_DOOR_IMPLEMENTER)] }), ['TURN_BIJ_EINDE']);
check('merge door implementer binnen grace: geen bevinding', ev({ labels: ['turn: codex'], issueComments: [comment(MERGE_DOOR_IMPLEMENTER, { min: 5 })] }), []);
check('merge door implementer, label net gewisseld: geen bevinding', ev({ labels: ['turn: codex'], laatsteLabelWijziging: minuten(3), issueComments: [comment(MERGE_DOOR_IMPLEMENTER)] }), []);
check('eindfase gemerged, label blijft staan: TURN_BIJ_EINDE', ev({ labels: ['turn: claude-code'], issueComments: [comment('Fase: gemerged\nImplementer: Codex')] }), ['TURN_BIJ_EINDE']);
check('eindfase gemerged zonder label: geen bevinding', ev({ labels: ['status: awaiting-release'], issueComments: [comment('Fase: gemerged\nImplementer: Codex')] }), []);
check('twee turn-labels, label net gewisseld: geen MEERDERE_TURN', ev({ labels: ['turn: codex', 'turn: owner'], laatsteLabelWijziging: minuten(3), issueComments: [comment(aanvraagNl)], prs: [pr7()] }), []);
check('twee turn-labels, label oud: MEERDERE_TURN', ev({ labels: ['turn: codex', 'turn: owner'], laatsteLabelWijziging: minuten(60), issueComments: [comment(aanvraagNl)], prs: [pr7()] }), ['MEERDERE_TURN']);

// ── geciteerde regels (bevinding 5) ──
console.log('citaten:');
const OUD_GECITEERD = '> Fase: verwerking\n> Implementer: Codex\n> Volgende beurt: Codex\n\nDank, ik pak het op.';
check('geciteerde fase telt niet als fase', parseOverdracht(OUD_GECITEERD).fase, null);
check('nieuwer antwoord dat een oudere overdracht citeert maskeert de echte nieuwste niet',
  ev({ labels: ['turn: claude-code'], prs: [pr7()], issueComments: [
    comment('Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex', { min: 300 }),
    comment(aanvraagNl, { min: 200 }),
    comment(OUD_GECITEERD, { min: 100 }),
  ] }), []);

// ── gelijke tijdstippen ──
console.log('gelijke tijdstippen:');
const NAAR_CODEX = 'Fase: verwerking\nImplementer: Codex\nVolgende beurt: Codex';
const NAAR_CLAUDE = 'Fase: verwerking\nImplementer: Claude Code\nVolgende beurt: Claude Code';
check('gelijke tijd: later gelezen comment wint (klopt met label)', ev({ labels: ['turn: claude-code'], issueComments: [comment(NAAR_CODEX, { min: 100 }), comment(NAAR_CLAUDE, { min: 100 })] }), []);
check('gelijke tijd: omgekeerde volgorde geeft andere uitkomst', ev({ labels: ['turn: claude-code'], issueComments: [comment(NAAR_CLAUDE, { min: 100 }), comment(NAAR_CODEX, { min: 100 })] }), ['TURN_WIJKT_AF']);
check('gelijke tijd: PR-comment wint van issuecomment', ev({ labels: ['turn: claude-code'], issueComments: [comment(NAAR_CODEX, { min: 100 })], prs: [pr7([comment(NAAR_CLAUDE, { min: 100 })])] }), []);

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
// Overdracht die een niet meer open PR noemt (pre-review #1858): afgerond werk, dus geen GEEN_TURN.
const OVERDRACHT_PR9 = 'Fase: verwerking\nImplementer: Claude Code\nPR: #9\nVolgende beurt: Claude Code';
check('GEEN_TURN vervalt als de genoemde PR afgerond is', ev({ labels: [], afgerondePrs: [9], issueComments: [comment(OVERDRACHT_PR9, { min: 60 * 24 * 30 })] }), []);
check('GEEN_TURN blijft als een andere PR afgerond is', ev({ labels: [], afgerondePrs: [8], issueComments: [comment(OVERDRACHT_PR9, { min: 60 * 24 * 30 })] }), ['GEEN_TURN']);
check('afgeronde PR verbergt TURN_WIJKT_AF niet', ev({ labels: ['turn: codex'], afgerondePrs: [9], issueComments: [comment(OVERDRACHT_PR9)] }), ['TURN_WIJKT_AF']);

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
function fakeGithub({ issues, prs = [], comments = {}, faalOp = null, events = [], prStatus = {} }) {
  const calls = { createComment: [], addLabels: 0, removeLabel: 0, setLabels: 0, pullsGet: [] };
  const rest = {
    issues: {
      listForRepo: 'listForRepo', listComments: 'listComments', listEvents: 'listEvents',
      createComment: async (a) => { calls.createComment.push(a); comments[a.issue_number] = (comments[a.issue_number] || []).concat([{ id: volgId++, body: a.body, created_at: new Date(NU).toISOString(), author_association: 'NONE', user: { login: 'github-actions[bot]', type: 'Bot' } }]); return {}; },
      addLabels: async () => { calls.addLabels++; }, removeLabel: async () => { calls.removeLabel++; }, setLabels: async () => { calls.setLabels++; },
    },
    pulls: {
      list: 'pullsList',
      // prStatus[nr]: 'open' | 'closed', of een HTTP-status (getal) die als fout wordt gegooid; ontbreekt = 404.
      get: async (a) => {
        calls.pullsGet.push(a.pull_number);
        const st = prStatus[a.pull_number];
        if (typeof st !== 'string') throw Object.assign(new Error('HTTP-fout'), { status: st || 404 });
        return { data: { number: a.pull_number, state: st } };
      },
    },
  };
  return {
    calls, rest,
    paginate: async (fn, a) => {
      if (fn === 'listForRepo') return issues;
      if (fn === 'pullsList') return prs;
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
    const gh2 = fakeGithub({ issues: [issue(1, ['turn: claude-code', 'status: waiting-codex'])], comments: { 1: gh.calls.createComment.length ? [oudeAfwijking].concat([{ id: 99, body: gh.calls.createComment[0].body, created_at: minuten(1), author_association: 'NONE', user: { login: 'github-actions[bot]', type: 'Bot' } }]) : [] } });
    await run({ github: gh2, context: ctx, core, now: new Date(NU) });
    check('gewijzigde bevinding: nieuwe comment', gh2.calls.createComment.length, 1);
    check('run wijzigt nooit labels', [gh.calls, gh2.calls].map((c) => c.addLabels + c.removeLabel + c.setLabels), [0, 0]);
  }
  {
    // Terugkerende afwijking: signaal (hash H) -> opgelost -> zelfde afwijking opnieuw -> nieuwe comment.
    const signaal = (min) => ({ id: volgId++, body: signaalBody(1, [{ code: 'TURN_WIJKT_AF', detail: 'label=claude-code;verwacht=codex', tekst: 'x' }]), created_at: minuten(min), author_association: 'NONE', user: { login: 'github-actions[bot]', type: 'Bot' } });
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
    // Overdracht alleen op de gekoppelde PR (oud, issue zelf zonder comments en zonder label): het issue wordt toch beoordeeld.
    const prComment = comment(EIGENAARSBESLUIT, { min: 60 * 24 * 30 });
    const gh = fakeGithub({
      issues: [{ ...issue(1, []), comments: 0 }],
      prs: [{ number: 7, title: 'fix(#1): iets', body: '', head: { sha: SHA } }],
      comments: { 1: [], 7: [prComment] },
    });
    const uitkomst = await run({ github: gh, context: ctx, core: maakCore(), now: new Date(NU) });
    check('PR-comment (30 dagen oud) zonder label: GEEN_TURN gesignaleerd', [uitkomst.beoordeeld, gh.calls.createComment.map((c) => c.issue_number)], [1, [1]]);
    const zonderPr = fakeGithub({ issues: [{ ...issue(2, []), comments: 0 }], comments: { 2: [] } });
    const leeg = await run({ github: zonderPr, context: ctx, core: maakCore(), now: new Date(NU) });
    check('open issue zonder label, PR of comments wordt niet opgehaald', leeg.beoordeeld, 0);
  }
  {
    // Alleen een claude[bot]-overdracht (type Bot) op het issue: label wijkt af, dus één signaal.
    const gh = fakeGithub({ issues: [issue(1, ['turn: claude-code'])], comments: { 1: [comment(NAAR_CODEX, { min: 120, ...BOT })] } });
    await run({ github: gh, context: ctx, core: maakCore(), now: new Date(NU) });
    check('run: claude[bot]-overdracht wordt gelezen', gh.calls.createComment.length, 1);
  }
  {
    const gh = fakeGithub({ issues: [issue(1, ['turn: codex']), issue(2, ['turn: claude-code'])], comments: { 1: [], 2: [oudeAfwijking] }, faalOp: 1 });
    const core = maakCore();
    await run({ github: gh, context: ctx, core, now: new Date(NU) });
    check('API-fout bij één issue: andere issue wordt wel beoordeeld', gh.calls.createComment.map((c) => c.issue_number), [2]);
    check('API-fout geeft waarschuwing en een falende run', [core.warnings.length, core.failed !== null], [1, true]);
  }
  {
    // Oude overdracht (60 dagen) op een issue zonder turn-label: GEEN_TURN wordt toch gevonden (P2 review ronde 1).
    const c = comment(EIGENAARSBESLUIT, { min: 60 * 24 * 60 });
    const gh = fakeGithub({ issues: [{ ...issue(3, []), comments: 1 }], comments: { 3: [c] } });
    await run({ github: gh, context: ctx, core: maakCore(), now: new Date(NU) });
    check('oude overdracht zonder label: GEEN_TURN gesignaleerd', gh.calls.createComment.length, 1);
  }
  {
    // Oude overdracht noemt PR #9 zonder open gekoppelde PR en zonder turn-label (pre-review #1858).
    const oud = () => comment(OVERDRACHT_PR9, { min: 60 * 24 * 30 });
    const draai = async (prStatus) => {
      const gh = fakeGithub({ issues: [{ ...issue(4, []), comments: 1 }], comments: { 4: [oud()] }, prStatus });
      const core = maakCore();
      await run({ github: gh, context: ctx, core, now: new Date(NU) });
      return [gh.calls.createComment.length, gh.calls.pullsGet, core.failed !== null];
    };
    check('genoemde PR gesloten of gemerged: geen GEEN_TURN', await draai({ 9: 'closed' }), [0, [9], false]);
    check('genoemde PR open maar niet sterk gekoppeld: GEEN_TURN', await draai({ 9: 'open' }), [1, [9], false]);
    check('genoemd nummer is geen PR (404): GEEN_TURN', await draai({}), [1, [9], false]);
    check('andere API-fout bij de PR-opvraging: issue mislukt, run faalt', await draai({ 9: 500 }), [0, [9], true]);
    const gekoppeld = fakeGithub({
      issues: [{ ...issue(5, []), comments: 1 }],
      prs: [{ number: 9, title: 'fix(#5): iets', body: '', head: { sha: SHA } }],
      comments: { 5: [oud()], 9: [] },
    });
    await run({ github: gekoppeld, context: ctx, core: maakCore(), now: new Date(NU) });
    check('genoemde PR is open en gekoppeld: geen extra opvraging, GEEN_TURN', [gekoppeld.calls.createComment.length, gekoppeld.calls.pullsGet], [1, []]);
  }

  console.log(failures === 0 ? '\nAlle checks geslaagd.' : `\n${failures} check(s) gefaald.`);
  process.exit(failures === 0 ? 0 : 1);
})();
