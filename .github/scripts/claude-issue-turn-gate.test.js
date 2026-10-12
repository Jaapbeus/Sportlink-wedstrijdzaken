// Unit-tests voor claude-issue-turn-gate.js — draaien zonder GitHub, zonder token, zonder netwerk.
// Uitvoeren: node .github/scripts/claude-issue-turn-gate.test.js   (exit 0 = alles groen)
// Wordt bij elke PR gedraaid door de job 'Build FunctionApp + BlazorAdmin' in build.yml,
// naast .github/scripts/issue-status.test.js (zelfde reden: deze helper draait alleen op
// zijn eigen trigger — labeled issues — dus een fout zou anders pas bij een echte run blijken).

const {
  evaluateGate, setTurnLabel, collectTrustedContext, runTitle, MAX_RUNS, MAX_CONTEXT_BYTES, truncateToBytes,
} = require('./claude-issue-turn-gate.js');

let failures = 0;
function check(name, actual, expected) {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a === e) { console.log(`  PASS  ${name}`); }
  else { console.log(`  FAIL  ${name}\n        verwacht: ${e}\n        kreeg:    ${a}`); failures++; }
}

const ctx = { repo: { owner: 'o', repo: 'r' }, runId: 999999 };
const core = { notice: () => {}, warning: () => {} };

function fakeGithub({
  labels = [], openPrs = [], priorRunTitles = [],
  issue = {}, commentsByNumber = {}, reviews = [], inlineComments = [],
} = {}) {
  const calls = { added: [], removed: [], comments: [] };
  // #1664: een geldig issue is open en heeft precies `turn: claude-code`, tenzij de test anders bepaalt.
  if (!labels.some((n) => n.startsWith('turn: '))) labels = [...labels, 'turn: claude-code'];
  return {
    calls,
    paginate: async (fn, params) => {
      const res = await fn(params);
      return res.data;
    },
    rest: {
      issues: {
        get: async () => ({
          data: { author_association: 'OWNER', state: 'open', labels: labels.map((name) => ({ name })), ...issue },
        }),
        listComments: async ({ issue_number }) => ({ data: commentsByNumber[issue_number] || [] }),
        addLabels: async ({ labels: l }) => { calls.added.push(...l); },
        removeLabel: async ({ name }) => { calls.removed.push(name); },
        createComment: async ({ body }) => { calls.comments.push(body); },
      },
      pulls: {
        list: async () => ({ data: openPrs }),
        listReviews: async () => ({ data: reviews }),
        listReviewComments: async () => ({ data: inlineComments }),
      },
      actions: {
        listWorkflowRuns: async () => ({
          data: priorRunTitles.map((display_title, i) => ({ id: 1000 + i, display_title })),
        }),
      },
    },
  };
}

(async () => {
  console.log('evaluateGate:');

  // ---------- scope-restrictie: ontbrekende source:-label ----------
  {
    const github = fakeGithub({ labels: ['type: bug', 'priority: medium'] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 42 });
    check('geen source-label: proceed=false', result.proceed, false);
    check('geen source-label: label naar turn: owner', github.calls.added, ['turn: owner']);
    check('geen source-label: toelichtende comment geplaatst', github.calls.comments.length, 1);
  }

  // ---------- scope-restrictie: discipline: architect ----------
  {
    const github = fakeGithub({ labels: ['source: owner', 'discipline: architect'] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 43 });
    check('architect-issue: proceed=false', result.proceed, false);
    check('architect-issue: label naar turn: owner', github.calls.added, ['turn: owner']);
  }

  // ---------- lusbeveiliging: rondelimiet overschreden ----------
  {
    const priorRunTitles = Array.from({ length: MAX_RUNS }, () => 'Claude issue-beurt #44');
    const github = fakeGithub({ labels: ['source: claude-code'], priorRunTitles });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 44 });
    check(`rondelimiet (${MAX_RUNS} eerdere runs): proceed=false`, result.proceed, false);
    check('rondelimiet: label naar turn: owner', github.calls.added, ['turn: owner']);
  }

  // ---------- lusbeveiliging: de huidige run zelf telt niet mee ----------
  {
    const github = fakeGithub({
      labels: ['source: claude-code'],
      priorRunTitles: [], // listWorkflowRuns levert hier geen runs op — alleen de huidige draait
    });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 45 });
    check('geen eerdere runs: proceed=true', result.proceed, true);
    check('geen eerdere runs: fase implementatie', result.phase, 'implementatie');
  }

  // ---------- fasedetectie: geen gekoppelde open PR → implementatie ----------
  {
    const github = fakeGithub({ labels: ['source: claude-code'], openPrs: [] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 46 });
    check('geen PR: proceed=true', result.proceed, true);
    check('geen PR: fase implementatie', result.phase, 'implementatie');
    check('geen PR: geen labelwijziging', github.calls.added, []);
  }

  // ---------- fasedetectie: 'strong' referentie in PR-titel → verwerking ----------
  {
    const openPrs = [
      {
        number: 501, head: { ref: 'feature/#47-iets', repo: { full_name: 'o/r' } },
        title: 'fix(#47): iets', body: '', author_association: 'OWNER',
      },
    ];
    const github = fakeGithub({ labels: ['source: owner'], openPrs });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 47 });
    check('gekoppelde PR: proceed=true', result.proceed, true);
    check('gekoppelde PR: fase verwerking', result.phase, 'verwerking');
    check('gekoppelde PR: pr-nummer doorgegeven', result.prNumber, 501);
    check('gekoppelde PR: branch doorgegeven', result.prBranch, 'feature/#47-iets');
  }

  // ---------- fasedetectie: kale kruisverwijzing in proza is NIET 'strong' ----------
  {
    const openPrs = [
      {
        number: 502, head: { ref: 'feature/#99-anders', repo: { full_name: 'o/r' } },
        title: 'fix(#99): iets', body: 'zie ook #48', author_association: 'OWNER',
      },
    ];
    const github = fakeGithub({ labels: ['source: owner'], openPrs });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 48 });
    check('kruisverwijzing in proza telt niet: fase implementatie', result.phase, 'implementatie');
  }

  // ---------- #1612: PR-selectie alleen uit vertrouwde bronnen ----------
  const ownPr = (n, extra = {}) => ({
    number: 600, head: { ref: `feature/#${n}-eigen`, repo: { full_name: 'o/r' } },
    title: `fix(#${n}): eigen werk`, body: '', author_association: 'OWNER', ...extra,
  });

  {
    const fork = ownPr(50, {
      number: 601, head: { ref: 'feature/#50-eigen', repo: { full_name: 'aanvaller/r' } },
      author_association: 'NONE',
    });
    const github = fakeGithub({ labels: ['source: owner'], openPrs: [fork] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 50 });
    check('fork-PR telt niet: fase implementatie', result.phase, 'implementatie');
  }

  {
    const outsider = ownPr(51, { author_association: 'CONTRIBUTOR' });
    const github = fakeGithub({ labels: ['source: owner'], openPrs: [outsider] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 51 });
    check('PR van niet-bevoegde auteur telt niet', result.phase, 'implementatie');
  }

  {
    const wrongBranch = ownPr(52, { head: { ref: 'develop', repo: { full_name: 'o/r' } } });
    const github = fakeGithub({ labels: ['source: owner'], openPrs: [wrongBranch] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 52 });
    check('branch buiten feature/#<nr>- telt niet (ook develop niet)', result.phase, 'implementatie');
  }

  {
    const attacker = ownPr(53, {
      number: 602, head: { ref: 'feature/#53-x', repo: { full_name: 'aanvaller/r' } },
      author_association: 'NONE',
    });
    const github = fakeGithub({ labels: ['source: owner'], openPrs: [attacker, ownPr(53)] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 53 });
    check('nieuwere aanvaller-PR wint niet van de eigen PR', result.prNumber, 600);
  }

  // ---------- #1633: de eigen PR van de Claude GitHub App wordt herkend ----------
  {
    const botPr = ownPr(55, {
      number: 610, author_association: 'NONE', user: { login: 'claude[bot]', type: 'Bot' },
    });
    const github = fakeGithub({ labels: ['source: claude-code'], openPrs: [botPr] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 55 });
    check('PR van claude[bot] (associatie NONE): fase verwerking', result.phase, 'verwerking');
    check('PR van claude[bot]: pr-nummer doorgegeven', result.prNumber, 610);
  }

  {
    const fakeBot = ownPr(56, {
      number: 611, author_association: 'NONE', user: { login: 'claude[bot]', type: 'User' },
    });
    const github = fakeGithub({ labels: ['source: claude-code'], openPrs: [fakeBot] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 56 });
    check('PR van een gebruiker die zich claude[bot] noemt telt niet', result.phase, 'implementatie');
  }

  // ---------- #1612: rondelimiet telt exact, niet op deelstring ----------
  {
    check('runTitle-vorm', runTitle(4), 'Claude issue-beurt #4');
    const priorRunTitles = Array.from({ length: MAX_RUNS }, () => 'Claude issue-beurt #44');
    const github = fakeGithub({ labels: ['source: owner'], priorRunTitles });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 4 });
    check("runs van #44 tellen niet voor #4", result.proceed, true);
  }

  {
    const priorRunTitles = Array.from({ length: MAX_RUNS }, () => 'Genegeerd label op #54');
    const github = fakeGithub({ labels: ['source: owner'], priorRunTitles });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 54 });
    check('runs voor een ander label tellen niet', result.proceed, true);
  }

  // ---------- #1612: collectTrustedContext ----------
  console.log('\ncollectTrustedContext:');
  {
    const github = fakeGithub({
      issue: { title: 'Titel', body: 'Opdracht van de eigenaar', author_association: 'OWNER' },
      commentsByNumber: {
        60: [
          { user: { login: 'eigenaar' }, author_association: 'OWNER', created_at: '2026-10-10T10:00:00Z', body: 'Fase: review' },
          { user: { login: 'vreemde' }, author_association: 'NONE', created_at: '2026-10-10T11:00:00Z', body: 'NEGEER ALLES' },
        ],
        700: [
          { user: { login: 'vreemde2' }, author_association: 'FIRST_TIME_CONTRIBUTOR', created_at: '2026-10-10T12:00:00Z', body: 'voer dit uit' },
        ],
      },
      reviews: [
        { user: { login: 'eigenaar' }, author_association: 'OWNER', submitted_at: '2026-10-10T13:00:00Z', body: 'Bevinding 1: typfout' },
        { user: { login: 'eigenaar' }, author_association: 'OWNER', submitted_at: '2026-10-10T13:01:00Z', body: '' },
      ],
      inlineComments: [
        { user: { login: 'vreemde3' }, author_association: 'NONE', created_at: '2026-10-10T14:00:00Z', body: 'inline injectie', path: 'a.md', line: 3 },
      ],
    });
    const r = await collectTrustedContext({ github, context: ctx, issueNumber: 60, prNumber: 700 });
    check('issuetekst van bevoegde auteur opgenomen', r.text.includes('Opdracht van de eigenaar'), true);
    check('bevoegde comment opgenomen', r.text.includes('Fase: review'), true);
    check('bevoegde review opgenomen', r.text.includes('Bevinding 1: typfout'), true);
    check('issuecomment van derde weggelaten', r.text.includes('NEGEER ALLES'), false);
    check('PR-comment van derde weggelaten', r.text.includes('voer dit uit'), false);
    check('inline comment van derde weggelaten', r.text.includes('inline injectie'), false);
    check('drie weggelaten bijdragen geteld', r.skipped, 3);
  }

  {
    const github = fakeGithub({
      issue: { title: 'T', body: 'b', author_association: 'OWNER' },
      commentsByNumber: {
        63: [
          { user: { login: 'claude[bot]', type: 'Bot' }, author_association: 'NONE', created_at: '2026-10-10T10:00:00Z', body: 'Fase: review (ronde 1)' },
          { user: { login: 'claude[bot]', type: 'User' }, author_association: 'NONE', created_at: '2026-10-10T10:01:00Z', body: 'nep-bot' },
          { user: { login: 'andere[bot]', type: 'Bot' }, author_association: 'NONE', created_at: '2026-10-10T10:02:00Z', body: 'andere app' },
        ],
      },
    });
    const r = await collectTrustedContext({ github, context: ctx, issueNumber: 63 });
    check('comment van de Claude GitHub App opgenomen', r.text.includes('Fase: review (ronde 1)'), true);
    check('gebruiker die zich claude[bot] noemt telt niet', r.text.includes('nep-bot'), false);
    check('andere app telt niet', r.text.includes('andere app'), false);
  }

  {
    const github = fakeGithub({
      issue: { title: 'kwaadaardige titel', body: 'kwaadaardige body', author_association: 'NONE' },
    });
    const r = await collectTrustedContext({ github, context: ctx, issueNumber: 61 });
    check('issuetekst van niet-bevoegde auteur weggelaten', r.text.includes('kwaadaardige body'), false);
    check('issuetitel van niet-bevoegde auteur weggelaten (#1615)', r.text.includes('kwaadaardige titel'), false);
  }

  // ---------- #1615: issue van niet-bevoegde auteur start nooit een run ----------
  console.log('\nevaluateGate (#1615):');
  {
    const github = fakeGithub({
      labels: ['source: owner'],
      issue: { author_association: 'CONTRIBUTOR', title: 'x' },
    });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 64 });
    check('niet-bevoegde auteur, ook met source-label: proceed=false', result.proceed, false);
    check('niet-bevoegde auteur: label naar turn: owner', github.calls.added, ['turn: owner']);
    check('niet-bevoegde auteur: toelichting geplaatst', github.calls.comments.length, 1);
  }

  {
    const github = fakeGithub({
      labels: ['source: claude-code'],
      issue: { author_association: 'NONE', user: { login: 'claude[bot]', type: 'Bot' } },
    });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 65 });
    check('issue van de Claude GitHub App mag wel', result.proceed, true);
  }

  // ---------- #1666: contextlimiet in UTF-8-bytes ----------
  console.log('\ncollectTrustedContext (bytes, #1666):');
  const ownerComment = (d, body) => ({
    user: { login: 'eigenaar' }, author_association: 'OWNER', created_at: `${d}T00:00:00Z`, body,
  });
  const bytes = (t) => Buffer.byteLength(t, 'utf8');

  {
    const big = 'x'.repeat(Math.floor(MAX_CONTEXT_BYTES / 2));
    const github = fakeGithub({
      issue: { title: 'T', body: 'b', author_association: 'OWNER' },
      commentsByNumber: {
        62: ['2026-10-01', '2026-10-02', '2026-10-03'].map((d, i) => ownerComment(d, `c${i}${big}`)),
      },
    });
    const r = await collectTrustedContext({ github, context: ctx, issueNumber: 62 });
    check('omvang in bytes begrensd', bytes(r.text) <= MAX_CONTEXT_BYTES, true);
    check('oudste valt af, nieuwste blijft', [r.text.includes('c0x'), r.text.includes('c2x')], [false, true]);
    check('afgekapte bijdragen geteld', r.truncated >= 1, true);
  }

  {
    // 40.000 tekens van 3 bytes = 120.000 bytes: past in tekens, niet in bytes.
    const multi = '€'.repeat(40000);
    const github = fakeGithub({
      issue: { title: 'T', body: 'b', author_association: 'OWNER' },
      commentsByNumber: { 66: [ownerComment('2026-10-01', multi)] },
    });
    const r = await collectTrustedContext({ github, context: ctx, issueNumber: 66 });
    check('multibyte: tekenaantal onder de oude limiet', r.text.length < 60000 + 2000, true);
    check('multibyte: bytes onder het plafond', bytes(r.text) <= MAX_CONTEXT_BYTES, true);
    check('multibyte: afkapmarkering aanwezig', r.text.includes('AFGEKAPT wegens omvang'), true);
    check('multibyte: geen ongeldig teken door afkappen', r.text.includes('\uFFFD'), false);
    check('multibyte: enige (nieuwste) bijdrage blijft deels beschikbaar', r.text.includes('€€€€'), true);
  }

  {
    // Enorme issuetekst naast twee reviewcomments: de laatste review blijft volledig behouden.
    const github = fakeGithub({
      issue: { title: 'Titel', body: 'i'.repeat(300000), author_association: 'OWNER' },
      commentsByNumber: {
        67: [ownerComment('2026-10-01', 'OUDE-REVIEW'), ownerComment('2026-10-02', 'NIEUWSTE-REVIEW')],
      },
    });
    const r = await collectTrustedContext({ github, context: ctx, issueNumber: 67 });
    check('grote issuetekst: bytes onder het plafond', bytes(r.text) <= MAX_CONTEXT_BYTES, true);
    check('grote issuetekst: nieuwste review volledig aanwezig', r.text.includes('NIEUWSTE-REVIEW'), true);
    check('grote issuetekst: oudere review ook aanwezig', r.text.includes('OUDE-REVIEW'), true);
    check('grote issuetekst: afkapping gemarkeerd', r.text.includes('AFGEKAPT wegens omvang'), true);
    check('grote issuetekst: melding in kop', r.text.includes('issuetekst is afgekapt'), true);
  }

  {
    check('truncateToBytes: korte tekst ongewijzigd', truncateToBytes('abc', 100), 'abc');
    const cut = truncateToBytes('€'.repeat(100), 200);
    check('truncateToBytes: limiet gerespecteerd', bytes(cut) <= 200, true);
    check('truncateToBytes: markering', cut.includes('AFGEKAPT'), true);
  }

  // ---------- #1664: actuele toestand van het issue ----------
  console.log('\nevaluateGate (#1664 actuele beurt):');
  {
    const github = fakeGithub({ labels: ['source: owner', 'turn: claude-code'], issue: { state: 'closed' } });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 70 });
    check('gesloten issue: proceed=false', result.proceed, false);
    check('gesloten issue: geen labelwijziging', [github.calls.added, github.calls.removed], [[], []]);
    check('gesloten issue: geen comment (niet heropend, niets geplaatst)', github.calls.comments, []);
  }
  {
    const github = fakeGithub({ labels: ['source: owner', 'turn: owner'] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 71 });
    check('turn: owner (beurt teruggenomen): proceed=false', result.proceed, false);
    check('turn: owner: labels ongewijzigd', [github.calls.added, github.calls.removed], [[], []]);
  }
  {
    const github = fakeGithub({ labels: ['source: owner', 'turn: claude-code', 'turn: codex'] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 72 });
    check('twee turn-labels: proceed=false', result.proceed, false);
    check('twee turn-labels: labels ongewijzigd', [github.calls.added, github.calls.removed], [[], []]);
  }
  {
    const github = fakeGithub({ labels: ['source: owner', 'turn: claude-code'] });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 73 });
    check('open issue met alleen turn: claude-code: proceed=true', result.proceed, true);
    check('open issue met alleen turn: claude-code: implementatie', result.phase, 'implementatie');
  }

  // ---------- #1665: meerdere gekoppelde PR's ----------
  console.log('\nevaluateGate (#1665 meerdere PR-koppelingen):');
  {
    const a = ownPr(80, { number: 801 });
    const b = ownPr(80, { number: 802, head: { ref: 'feature/#80-andere', repo: { full_name: 'o/r' } } });
    const results = [];
    for (const openPrs of [[a, b], [b, a]]) {
      const github = fakeGithub({ labels: ['source: owner'], openPrs });
      const result = await evaluateGate({ github, context: ctx, core, issueNumber: 80 });
      results.push(result);
      check('twee geldige PR\'s: proceed=false', result.proceed, false);
      check('twee geldige PR\'s: reden noemt meerdere PR-koppelingen', /meerdere PR-koppelingen/.test(result.reason), true);
      check('twee geldige PR\'s: geen fase en geen labelwijziging', [result.phase, github.calls.added, github.calls.removed, github.calls.comments], [undefined, [], [], []]);
    }
    check('twee geldige PR\'s: uitkomst onafhankelijk van API-volgorde', results[0], results[1]);
  }
  {
    const geldig = ownPr(81, { number: 811 });
    const ongeldig = ownPr(81, { number: 812, author_association: 'NONE' });
    for (const openPrs of [[geldig, ongeldig], [ongeldig, geldig]]) {
      const github = fakeGithub({ labels: ['source: owner'], openPrs });
      const result = await evaluateGate({ github, context: ctx, core, issueNumber: 81 });
      check('één geldige + één ongeldige PR: verwerking op de geldige', [result.proceed, result.phase, result.prNumber], [true, 'verwerking', 811]);
    }
  }

  // ---------- #1686: reviewteruggave verwijst naar een besloten reviewnotitie ----------
  console.log('\nevaluateGate (#1686 besloten reviewnotitie):');
  const prComment = (body, tijd, extra = {}) => ({
    user: { login: 'eigenaar' }, author_association: 'OWNER', created_at: tijd, body, ...extra,
  });
  const gewoon = 'Onafhankelijke review ronde 1 — reviewer Codex.\n\nUitkomst: één P2-bevinding in `a.js:12`.';
  const besloten = (variant) => `Onafhankelijke review ronde 1 — reviewer Codex.\n\nUitkomst: één P1-bevinding; details in de ${variant}.`;
  const runMetComments = async (n, comments) => {
    const github = fakeGithub({
      labels: ['source: owner'], openPrs: [ownPr(n, { number: 900 + n })], commentsByNumber: { [900 + n]: comments },
    });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: n });
    return { github, result };
  };
  for (const variant of ['besloten lokale reviewnotitie', 'besloten reviewnotitie', 'Besloten notitie']) {
    const { github, result } = await runMetComments(90, [prComment(besloten(variant), '2026-10-12T10:00:00Z')]);
    check(`'${variant}': geen run`, [result.proceed, result.phase], [false, undefined]);
    check(`'${variant}': reden noemt de besloten reviewnotitie`, /besloten reviewnotitie/.test(result.reason), true);
    check(`'${variant}': één comment, beurt naar turn: owner`, [github.calls.comments.length, github.calls.added, github.calls.removed],
      [1, ['turn: owner'], ['turn: claude-code']]);
    check(`'${variant}': comment bevat de inhoud van de bevinding niet`, github.calls.comments[0].includes('P1'), false);
  }
  {
    const { github, result } = await runMetComments(91, [prComment(gewoon, '2026-10-12T10:00:00Z')]);
    check('gewone teruggave: verwerking ongewijzigd', [result.proceed, result.phase, result.prNumber], [true, 'verwerking', 991]);
    check('gewone teruggave: geen comment of labelwijziging', [github.calls.comments, github.calls.added], [[], []]);
  }
  {
    const { result } = await runMetComments(92, []);
    check('geen teruggave: verwerking ongewijzigd', [result.proceed, result.phase], [true, 'verwerking']);
  }
  {
    // API-volgorde bewust omgekeerd: de tijdstempel bepaalt welke teruggave de nieuwste is.
    const { github, result } = await runMetComments(93, [
      prComment('Onafhankelijke review ronde 2 — reviewer Codex.\n\nUitkomst: opgelost, geen nieuwe bevindingen.', '2026-10-12T11:00:00Z'),
      prComment(besloten('besloten reviewnotitie'), '2026-10-12T10:00:00Z'),
    ]);
    check('oudere teruggave met verwijzing, nieuwere zonder: ongewijzigd', [result.proceed, result.phase], [true, 'verwerking']);
    check('oudere teruggave met verwijzing: geen comment', github.calls.comments, []);
  }
  {
    const { github, result } = await runMetComments(94, [
      prComment(besloten('besloten reviewnotitie'), '2026-10-12T10:00:00Z', { user: { login: 'vreemde' }, author_association: 'NONE' }),
    ]);
    check('verwijzing van niet-bevoegde auteur telt niet', [result.proceed, result.phase], [true, 'verwerking']);
    check('niet-bevoegde auteur: geen comment of labelwijziging', [github.calls.comments, github.calls.added], [[], []]);
  }
  {
    const { result } = await runMetComments(95, [
      prComment(besloten('besloten reviewnotitie'), '2026-10-12T10:00:00Z'),
      prComment('Verwerking Codex-review ronde 1: de besloten notitie is verwerkt. Uitkomst: opgelost.', '2026-10-12T11:00:00Z'),
    ]);
    check('verwerkingscomment telt niet als teruggave: nieuwste teruggave blijft blokkeren', result.proceed, false);
  }

  // ---------- setTurnLabel: remove+add, oude turn-labels weg ----------
  {
    const github = fakeGithub({ labels: ['turn: codex', 'type: bug'] });
    await setTurnLabel({ github, context: ctx, core, issueNumber: 49, label: 'turn: owner' });
    check('setTurnLabel: nieuw label toegevoegd', github.calls.added, ['turn: owner']);
    check('setTurnLabel: oud turn-label verwijderd', github.calls.removed, ['turn: codex']);
  }

  console.log(failures === 0 ? '\nAlle checks geslaagd.' : `\n${failures} check(s) gefaald.`);
  process.exit(failures === 0 ? 0 : 1);
})();
