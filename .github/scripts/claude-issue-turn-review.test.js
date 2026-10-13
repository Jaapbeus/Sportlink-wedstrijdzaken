// Unit-tests voor claude-issue-turn-review.js (#1727) — zonder GitHub, token of netwerk.
// Uitvoeren: node .github/scripts/claude-issue-turn-review.test.js   (exit 0 = alles groen)
// Draait bij elke PR in de job 'Build FunctionApp + BlazorAdmin' (build.yml), naast de poorttest.

const {
  publishReview, parseReviewOutput, composePrComment, scanPublicText, nextTurn,
} = require('./claude-issue-turn-review.js');
const { reviewMarker } = require('./claude-issue-turn-gate.js');

let failures = 0;
function check(name, actual, expected) {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a === e) { console.log(`  PASS  ${name}`); }
  else { console.log(`  FAIL  ${name}\n        verwacht: ${e}\n        kreeg:    ${a}`); failures++; }
}

const ctx = { repo: { owner: 'o', repo: 'r' }, runId: 1 };
const core = { notice: () => {}, warning: () => {} };
const ISSUE = 1723;
const PR = 1724;
const SHA = 'a'.repeat(40);
const START = '2026-10-13T16:45:00Z';

const aanvraag = (sha = SHA) => ({
  user: { login: 'eigenaar' }, author_association: 'OWNER', created_at: '2026-10-13T16:44:00Z',
  body: `Fase: review\nPR: #${PR}\nImplementer: Codex\nReviewer: Claude Code\nAangeboden head-SHA: ${sha}`,
});
const codexPr = (sha = SHA) => ({
  number: PR, title: `security(#${ISSUE}): iets`, body: '', author_association: 'OWNER', state: 'open',
  head: { ref: `codex/${ISSUE}-tokenformat`, sha, repo: { full_name: 'o/r' } }, base: { ref: 'develop' },
  merged_at: null, closed_at: null, updated_at: '2026-10-13T16:40:00Z',
});

/**
 * Nep-octokit met veranderlijke toestand: labels wijzigen echt bij addLabels/removeLabel, en
 * `onComment` kan de toestand na een comment aanpassen (iemand pusht of trekt de aanvraag in).
 */
function fakeGithub({ labels = ['turn: claude-code', 'source: codex'], issueComments = [aanvraag()], pr = codexPr(), reviews = [], onComment } = {}) {
  const state = { labels: [...labels], issueComments: [...issueComments], prComments: [], pr };
  const calls = { comments: [], added: [], removed: [] };
  return {
    state, calls,
    paginate: async (fn, params) => (await fn(params)).data,
    rest: {
      issues: {
        get: async () => ({ data: { state: 'open', created_at: '2026-10-01T00:00:00Z', author_association: 'OWNER', labels: state.labels.map((name) => ({ name })) } }),
        listComments: async ({ issue_number }) => ({ data: issue_number === ISSUE ? state.issueComments : state.prComments }),
        createComment: async ({ issue_number, body }) => {
          calls.comments.push({ issue_number, body });
          const c = { user: { login: 'github-actions[bot]', type: 'Bot' }, author_association: 'NONE', created_at: '2026-10-13T17:00:00Z', body };
          (issue_number === ISSUE ? state.issueComments : state.prComments).push(c);
          if (onComment) onComment(state, calls.comments.length);
        },
        addLabels: async ({ labels: l }) => { calls.added.push(...l); state.labels.push(...l); },
        removeLabel: async ({ name }) => { calls.removed.push(name); state.labels = state.labels.filter((n) => n !== name); },
      },
      pulls: {
        list: async ({ state: s }) => ({ data: s === 'open' && state.pr.state === 'open' ? [state.pr] : [] }),
        get: async () => ({ data: state.pr }),
        listReviews: async () => ({ data: reviews }),
      },
    },
  };
}

const geldig = (extra = {}) => JSON.stringify({
  uitvoerbaar: true, uitkomst: 'bevindingen', besloten_bevinding: false, samenvatting: 'Twee punten.',
  bevindingen: [{ prioriteit: 'P2', locatie: 'a.js:12', reden: 'Grensgeval ontbreekt.', verificatie: 'Test met lege lijst.' }],
  ...extra,
});
const publiceer = (github, raw = geldig(), agentResult = 'success') => publishReview({
  github, context: ctx, core, issueNumber: ISSUE, prNumber: PR, sha: SHA, ronde: 1, raw, agentResult, startedAt: START,
});

(async () => {
  console.log('parseReviewOutput:');
  check('geldige uitvoer', parseReviewOutput(geldig()).ok, true);
  check('lege uitvoer', parseReviewOutput('').ok, false);
  check('geen JSON', parseReviewOutput('{kapot').ok, false);
  check('onbekend veld', parseReviewOutput(geldig({ label: 'turn: owner' })).ok, false);
  check('ongeldige prioriteit', parseReviewOutput(geldig({ bevindingen: [{ prioriteit: 'P0', locatie: 'x', reden: 'y', verificatie: 'z' }] })).ok, false);
  check('uitkomst spreekt bevindingen tegen', parseReviewOutput(geldig({ uitkomst: 'geen bevindingen' })).ok, false);
  {
    const p = parseReviewOutput(geldig({ bevindingen: [{ prioriteit: 'P1', locatie: 'a.js\n:1 <!-- x -->', reden: 'r', verificatie: 'v' }] }));
    check('locatie op één regel, geen HTML-commentaar', p.data.bevindingen[0].locatie, 'a.js :1  x');
  }

  console.log('\nnextTurn en tekst:');
  check('normaal: Codex', nextTurn({ uitvoerbaar: true, besloten: false }), 'turn: codex');
  check('besloten bevinding: eigenaar', nextTurn({ uitvoerbaar: true, besloten: true }), 'turn: owner');
  check('niet uitvoerbaar: eigenaar', nextTurn({ uitvoerbaar: false, besloten: false }), 'turn: owner');
  {
    const body = composePrComment({ ronde: 1, sha: SHA, data: parseReviewOutput(geldig()).data });
    check('PR-comment begint met reviewteruggave', body.startsWith('Reviewteruggave — reviewronde 1\n'), true);
    check('PR-comment bevat de markering voor de SHA', body.includes(reviewMarker(SHA)), true);
  }
  // `.invalid` is een gereserveerd domein (RFC 2606): nooit een echt adres, maar niet toegestaan.
  check('scan: e-mailadres', scanPublicText('mail naar lid@club.invalid'), 'e-mailadres');
  check('scan: toegestaan testdomein', scanPublicText('trainer@voorbeeld.nl en x@allstars-fc.test'), null);
  check('scan: GUID', scanPublicText('tenant 12345678-1234-1234-1234-123456789abc'), 'GUID');
  check('scan: versienummer is geen treffer', scanPublicText('v3.14.0.0 en 10.0.x'), null);

  console.log('\npublishReview:');
  {
    const github = fakeGithub();
    const r = await publiceer(github);
    check('gelukt: gepubliceerd en label Codex', [r.published, r.label, r.labelWritten], [true, 'turn: codex', true]);
    check('gelukt: eerst PR, dan issue', github.calls.comments.map((c) => c.issue_number), [PR, ISSUE]);
    check('gelukt: issuecomment eindigt op de beurt', github.calls.comments[1].body.endsWith('Volgende beurt: turn: codex'), true);
    check('gelukt: labels', [github.calls.added, github.calls.removed], [['turn: codex'], ['turn: claude-code']]);
  }
  {
    const github = fakeGithub();
    const r = await publiceer(github, geldig({ besloten_bevinding: true }));
    check('besloten bevinding: label eigenaar', [r.published, r.label], [true, 'turn: owner']);
  }
  const nietsGedaan = (naam, github, r) => {
    check(`${naam}: niets gepubliceerd`, [r.published, github.calls.comments.length], [false, 0]);
    check(`${naam}: geen labelwijziging`, [github.calls.added, github.calls.removed], [[], []]);
  };
  { const g = fakeGithub(); nietsGedaan('agent faalde', g, await publiceer(g, geldig(), 'failure')); }
  { const g = fakeGithub(); nietsGedaan('ongeldige uitvoer', g, await publiceer(g, '{}')); }
  { const g = fakeGithub(); nietsGedaan('niet-publieke waarde', g, await publiceer(g, geldig({ samenvatting: 'zie lid@club.invalid' }))); }
  { const g = fakeGithub({ pr: codexPr('b'.repeat(40)) }); nietsGedaan('head gewijzigd vóór publicatie', g, await publiceer(g)); }
  { const g = fakeGithub({ labels: ['turn: owner', 'source: codex'] }); nietsGedaan('beurt teruggenomen', g, await publiceer(g)); }
  {
    const intrekking = { user: { login: 'eigenaar' }, author_association: 'OWNER', created_at: '2026-10-13T16:50:00Z', body: 'Fase: implementatie\nVolgende beurt: turn: codex' };
    const g = fakeGithub({ issueComments: [aanvraag(), intrekking] });
    nietsGedaan('aanvraag ingetrokken', g, await publiceer(g));
  }
  {
    const g = fakeGithub({ reviews: [{ user: { login: 'eigenaar' }, author_association: 'OWNER', state: 'PENDING', commit_id: SHA, body: '' }] });
    nietsGedaan('lopende formele review op de SHA', g, await publiceer(g));
  }
  {
    // Codex pusht tussen de PR-comment en het label: comments staan er (gelden voor die SHA), label niet.
    const g = fakeGithub({ onComment: (state, n) => { if (n === 1) state.pr = codexPr('c'.repeat(40)); } });
    const r = await publiceer(g);
    check('head gewijzigd na publicatie: geen label', [r.published, r.labelWritten, g.calls.added], [true, false, []]);
    check('head gewijzigd na publicatie: beurt blijft claude-code', g.state.labels.includes('turn: claude-code'), true);
  }

  {
    // Aanvraag ingetrokken tussen publicatie en label (zelfde SHA, beurt ongewijzigd): alleen de
    // hercontrole van de aanvraag vangt dit; de vergelijk-en-zet kijkt naar beurt, PR en SHA.
    const intrekking = { user: { login: 'eigenaar' }, author_association: 'OWNER', created_at: '2026-10-13T17:30:00Z', body: 'Fase: implementatie\nVolgende beurt: turn: codex' };
    const g = fakeGithub({ onComment: (state, n) => { if (n === 1) state.issueComments.push(intrekking); } });
    const r = await publiceer(g);
    check('aanvraag ingetrokken na publicatie: geen label', [r.published, r.labelWritten, g.calls.added], [true, false, []]);
    check('aanvraag ingetrokken na publicatie: reden genoemd', /ingetrokken/.test(r.reason), true);
  }

  console.log(failures === 0 ? '\nAlle checks geslaagd.' : `\n${failures} check(s) gefaald.`);
  process.exit(failures === 0 ? 0 : 1);
})();
