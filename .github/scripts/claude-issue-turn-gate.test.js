// Unit-tests voor claude-issue-turn-gate.js — draaien zonder GitHub, zonder token, zonder netwerk.
// Uitvoeren: node .github/scripts/claude-issue-turn-gate.test.js   (exit 0 = alles groen)
// Wordt bij elke PR gedraaid door de job 'Build FunctionApp + BlazorAdmin' in build.yml,
// naast .github/scripts/issue-status.test.js (zelfde reden: deze helper draait alleen op
// zijn eigen trigger — labeled issues — dus een fout zou anders pas bij een echte run blijken).

const { evaluateGate, setTurnLabel, MAX_RUNS } = require('./claude-issue-turn-gate.js');

let failures = 0;
function check(name, actual, expected) {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a === e) { console.log(`  PASS  ${name}`); }
  else { console.log(`  FAIL  ${name}\n        verwacht: ${e}\n        kreeg:    ${a}`); failures++; }
}

const ctx = { repo: { owner: 'o', repo: 'r' }, runId: 999999 };
const core = { notice: () => {}, warning: () => {} };

function fakeGithub({ labels = [], openPrs = [], priorRunTitles = [] } = {}) {
  const calls = { added: [], removed: [], comments: [] };
  return {
    calls,
    paginate: async (fn, params) => {
      const res = await fn(params);
      return res.data;
    },
    rest: {
      issues: {
        get: async () => ({ data: { labels: labels.map((name) => ({ name })) } }),
        addLabels: async ({ labels: l }) => { calls.added.push(...l); },
        removeLabel: async ({ name }) => { calls.removed.push(name); },
        createComment: async ({ body }) => { calls.comments.push(body); },
      },
      pulls: {
        list: async () => ({ data: openPrs }),
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
      { number: 501, head: { ref: 'feature/#47-iets' }, title: 'fix(#47): iets', body: '' },
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
      { number: 502, head: { ref: 'feature/#99-anders' }, title: 'fix(#99): iets', body: 'zie ook #48' },
    ];
    const github = fakeGithub({ labels: ['source: owner'], openPrs });
    const result = await evaluateGate({ github, context: ctx, core, issueNumber: 48 });
    check('kruisverwijzing in proza telt niet: fase implementatie', result.phase, 'implementatie');
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
