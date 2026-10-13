// Unit-tests voor issue-router.js en structuurchecks op de workflows van #1721 (fase 1).
// Uitvoeren: node .github/scripts/issue-router.test.js   (exit 0 = alles groen)
// Draait bij elke PR in de job 'Build FunctionApp + BlazorAdmin' (build.yml). De router en de aanroepbare
// beurtworkflow draaien alleen op hun eigen trigger; zonder deze test blijkt een fout pas bij een proef.

const fs = require('fs');
const path = require('path');
const { ROUTES, route, dispatchEvent } = require('./issue-router.js');

let failures = 0;
function check(name, actual, expected) {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a === e) { console.log(`  PASS  ${name}`); }
  else { console.log(`  FAIL  ${name}\n        verwacht: ${e}\n        kreeg:    ${a}`); failures++; }
}

const wfDir = path.join(__dirname, '../workflows');
// Zonder volledige commentaarregels: een toelichting die een verboden term noemt mag de check niet raken.
const read = (f) => fs.readFileSync(path.join(wfDir, f), 'utf8').replace(/^[ \t]*#.*\n/gm, '');
/** De tekst van één job (van `\n  <naam>:\n` tot de volgende job). */
const jobText = (yml, naam) => {
  const m = yml.match(new RegExp(`\\n  ${naam}:\\n([\\s\\S]*?)(?=\\n  [a-z-]+:\\n|$)`));
  return m ? m[1] : '';
};

console.log('route():');
{
  const claude = route({ action: 'labeled', label: 'turn: claude-code', issueNumber: '42' });
  check('turn: claude-code: handlers exclusief + beurt', claude.handlers, ['exclusief', 'beurt']);
  check('turn: claude-code: beurt claude-code, nummer 42', [claude.valid, claude.beurt, claude.issueNumber], [true, 'claude-code', 42]);
  const codex = route({ action: 'labeled', label: 'turn: codex', issueNumber: '42' });
  check('turn: codex: alleen exclusief, geen beurt', [codex.handlers, codex.beurt], [['exclusief'], '']);
  const other = route({ action: 'labeled', label: 'type: bug', issueNumber: '42' });
  check('willekeurig label: geen rij, geen beurt', [other.handlers, other.beurt], [[], '']);
  check('opened: status', route({ action: 'opened', issueNumber: '7' }).handlers, ['status']);
  check('reopened: status', route({ action: 'reopened', issueNumber: '7' }).handlers, ['status']);
  check('opened start nooit een beurt', route({ action: 'opened', label: 'turn: claude-code', issueNumber: '7' }).beurt, '');
  check('label zonder turn:-prefix die erop lijkt', route({ action: 'labeled', label: 'xturn: claude-code', issueNumber: '7' }).beurt, '');
  check('labelnaam met extra spatie telt niet', route({ action: 'labeled', label: 'turn: claude-code ', issueNumber: '7' }).beurt, '');
  check('onbekende action', route({ action: 'unlabeled', label: 'turn: claude-code', issueNumber: '7' }).handlers, []);
  for (const bad of ['', 'abc', '12x', '0', '-5', '1.5', ' 42', '4 2', '1234567890', undefined, null, '42\n', '$(id)']) {
    const r = route({ action: 'labeled', label: 'turn: claude-code', issueNumber: bad });
    check(`ongeldig issuenummer ${JSON.stringify(bad)}: geen route`, [r.valid, r.beurt], [false, '']);
  }
  check('getal als nummer wordt geaccepteerd', route({ action: 'labeled', label: 'turn: claude-code', issueNumber: 42 }).issueNumber, 42);
  check('de dispatch bootst turn: claude-code na', route(dispatchEvent('42')).beurt, 'claude-code');
  check('tabel: precies één beurtrij (nieuwe beurtsoort = één rij)', ROUTES.filter((r) => r.handler === 'beurt').length, 1);
}

console.log('\nissue-router.yml (fase 1):');
{
  const yml = read('issue-router.yml');
  const on = (yml.match(/\non:\n([\s\S]*?)\n\S/) || [])[1] || '';
  check('alleen workflow_dispatch als trigger', /^  workflow_dispatch:/m.test(on) && !/^  (issues|issue_comment|pull_request\w*|push|schedule|workflow_call|workflow_run):/m.test(on), true);
  check('input issue_number: string, verplicht', /issue_number:\n(?:        .*\n)*?        type: string\n        required: true/.test(yml), true);
  check('input dry_run: boolean, standaard false', /dry_run:\n(?:        .*\n)*?        type: boolean\n        required: false\n        default: false/.test(yml), true);
  const beurt = jobText(yml, 'beurt');
  check('aanroep-job gebruikt de lokale beurtworkflow', /\n    uses: \.\/\.github\/workflows\/claude-issue-turn\.yml\n/.test(`\n${beurt}`), true);
  check('lock op de aanroep-job: groep per issue, niet annuleren', /concurrency:\n      group: claude-issue-turn-\$\{\{ inputs\.issue_number \}\}\n      cancel-in-progress: false/.test(beurt), true);
  check('issue_number en dry_run expliciet doorgegeven', /with:\n      issue_number: \$\{\{ inputs\.issue_number \}\}\n      dry_run: \$\{\{ inputs\.dry_run \}\}/.test(beurt), true);
  check('secret expliciet doorgegeven', /secrets:\n      CLAUDE_CODE_OAUTH_TOKEN: \$\{\{ secrets\.CLAUDE_CODE_OAUTH_TOKEN \}\}/.test(beurt), true);
  check('geen secrets: inherit', /secrets:\s*inherit/.test(yml), false);
  check('geen allowed_bots', /allowed_bots/.test(yml), false);
  check('beurt wacht op de route en start alleen bij beurt claude-code', /needs: route\n    if: needs\.route\.outputs\.beurt == 'claude-code'/.test(beurt), true);
  check('router heeft geen run-level concurrency', /^concurrency:/m.test(yml), false);
  check('top-level rechten alleen contents: read', /\npermissions:\n  contents: read\n\njobs:/.test(yml), true);
  check('bovengrens bevat geen checks: write', /checks:\s*write/.test(yml), false);
}

console.log('\nclaude-issue-turn.yml (#1721 fase 1):');
{
  const yml = read('claude-issue-turn.yml');
  const on = (yml.match(/\non:\n([\s\S]*?)\n\S/) || [])[1] || '';
  check('productietrigger issues: labeled ongewijzigd', /  issues:\n    types: \[labeled\]/.test(on), true);
  check('workflow_call erbij', /^  workflow_call:/m.test(on), true);
  check('workflow_call: issue_number string verplicht', /issue_number:\n(?:        .*\n)*?        type: string\n        required: true/.test(on), true);
  check('workflow_call: dry_run boolean standaard false', /dry_run:\n(?:        .*\n)*?        type: boolean\n        required: false\n        default: false/.test(on), true);
  check('workflow_call: secret CLAUDE_CODE_OAUTH_TOKEN verplicht gedeclareerd', /secrets:\n      CLAUDE_CODE_OAUTH_TOKEN:\n        required: true/.test(on), true);
  check('geen workflow_dispatch of andere extra trigger', /^  (workflow_dispatch|pull_request\w*|push|schedule|issue_comment):/m.test(on), false);
  check('top-level groep: per issue bij issues, uniek per run bij een aanroep', /concurrency:\n  group: \$\{\{ github\.event_name == 'issues' && format\('claude-issue-turn-\{0\}', github\.event\.issue\.number\) \|\| format\('claude-issue-turn-test-\{0\}', github\.run_id\) \}\}\n  cancel-in-progress: false/.test(yml), true);
  check('geen concurrency op jobniveau (geen geneste locks)', /\n    concurrency:/.test(yml), false);
  check('geen allowed_bots', /allowed_bots/.test(yml), false);
  check('geen secrets: inherit', /secrets:\s*inherit/.test(yml), false);
  check('issuenummer komt uit de poortoutput, niet meer direct uit het event (prompt en scripts)', /github\.event\.issue\.number \}\}/.test(yml), false);
  check('input gaat via een omgevingsvariabele naar het script (geen injectie)', /INPUT_ISSUE_NUMBER: \$\{\{ inputs\.issue_number \}\}/.test(yml) && !/\$\{\{ inputs\.[a-z_]+ \}\}[^\n]*\n\s+script:/.test(yml), true);
  const gate = jobText(yml, 'gate');
  check('poort draait ook bij een aanroep (niet-issues-event)', /if: "github\.event_name != 'issues' \|\| github\.event\.label\.name == 'turn: claude-code'"/.test(gate), true);
  check('poort heeft checks: read (annotaties) en geen schrijfrecht daarop', /checks: read/.test(gate) && !/checks:\s*write/.test(gate), true);
  check('productie-agent draait niet in de testmodus', /needs\.gate\.outputs\.dry_run != 'true'/.test(jobText(yml, 'claude')), true);
  check('reviewjob draait niet in de testmodus', /needs\.gate\.outputs\.dry_run != 'true'/.test(jobText(yml, 'review')), true);
  check('reviewpublicatie draait niet in de testmodus', /needs\.gate\.outputs\.dry_run != 'true'/.test(jobText(yml, 'review-publiceren')), true);
  const dry = jobText(yml, 'claude-dryrun');
  check('testagent: alleen in de testmodus', /dry_run == 'true'/.test(dry), true);
  check('testagent: wacht op de ledgercontrole', /needs: \[gate, ledger-start\]/.test(dry), true);
  check('testagent: één beurt', /--max-turns 1\n/.test(dry), true);
  check('testagent: geen enkele tool toegestaan', !/--allowedTools/.test(dry) && /--disallowedTools "Bash,Read,Edit,Write,Glob,Grep,NotebookEdit,WebFetch,WebSearch"/.test(dry), true);
  check('testagent: geen schrijfrechten op de repository', !/contents: write|pull-requests: write|issues: write/.test(dry), true);
  check('testagent: vaste prompt zonder gate-context', !/needs\.gate\.outputs\.context/.test(dry), true);
  check('testagent: geen instellingen uit de checkout', /--setting-sources user\n/.test(dry), true);
  const ledger = jobText(yml, 'ledger-start');
  check('ledgercontrole draait op de default branch (geen ref) met de testmodus', !/\bref:/.test(ledger) && /mode: 'test'/.test(ledger), true);
  check('eindlabel geeft dry_run door (alleen uitrekenen)', /dryRun: process\.env\.DRY_RUN === 'true'/.test(jobText(yml, 'eindlabel')), true);
  check('eindlabel wacht op de testagent', /needs: \[gate, claude, claude-dryrun\]/.test(jobText(yml, 'eindlabel')), true);
}

console.log('\nGeen enkele workflow krijgt checks: write (behalve de bestaande allowlist):');
{
  // pre-release-db-check.yml maakt bewust een check-run aan (branch protection, #1009); alleen die.
  const ALLOWED = ['pre-release-db-check.yml'];
  const offenders = fs.readdirSync(wfDir).filter((f) => /\.ya?ml$/.test(f))
    .filter((f) => /^\s*checks:\s*write\b/m.test(read(f))).sort();
  check('checks: write alleen in de allowlist', offenders, ALLOWED);
  check('router en beurtworkflow staan niet in de allowlist', ALLOWED.some((f) => /issue-router|claude-issue-turn/.test(f)), false);
}

console.log(failures === 0 ? '\nAlle checks geslaagd.' : `\n${failures} check(s) gefaald.`);
process.exit(failures === 0 ? 0 : 1);
