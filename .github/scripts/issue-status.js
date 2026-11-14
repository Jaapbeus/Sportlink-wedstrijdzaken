// issue-status.js
//
// Gedeelde logica voor de status-labels op issues. Wordt gebruikt door
// label-issue-status.yml, label-awaiting-release.yml en close-released-issues.yml.
//
// Waarom gedeeld: een issue mag hoogstens één 'status: '-label hebben. Zonder één
// centrale plek voor die regel dupliceren drie workflows dezelfde remove/add-dans en
// lopen ze onvermijdelijk uiteen — dan stapelen labels zich op of verdwijnen ze.
// Zie AGENTS.md, "Ontwikkelcyclus" (alinea "Labels").

const STATUS_PREFIX = 'status: ';
const TURN_PREFIX = 'turn: ';

// #1370: Octokit logt sinds ~september 2026 een deprecation-warning op elke onversioneerde
// REST-aanroep ("... is deprecated. It is scheduled to be removed on Fri, 10 Mar 2028").
// Dit blokkeerde nog niets, maar is wél het officiële migratiepad (zie
// https://docs.github.com/en/rest/about-the-rest-api/api-versions). Eén constante hier
// i.p.v. de header los in elke aanroep herhalen — dezelfde reden als STATUS_PREFIX hierboven:
// drie workflows delen deze aanroepen, en een los kopieerbare header-literal zou bij de
// volgende endpoint-toevoeging weer uiteen kunnen lopen.
const API_VERSION_HEADERS = { 'x-github-api-version': '2022-11-28' };

// Statussen die een PR-event niet mag overschrijven. Handmatige blokkades blijven staan;
// awaiting-release is eigendom van de develop-merge/release-workflows.
const PROTECTED = [
  'status: blocked',
  'status: wont-fix',
  'status: waiting-owner',
  'status: awaiting-release',
  // Codex draait als handmatig aangeroepen, read-only reviewsweep zonder webhook — dit label
  // wordt dus altijd door Claude Code (of de eigenaar) gezet/verwijderd, nooit automatisering.
  // Zie AGENTS.md, "Ontwikkelcyclus" (alinea "Labels") (#1336).
  'status: waiting-codex',
];

const EPIC_LABEL = 'epic';

function labelNames(issue) {
  return (issue.labels || []).map(l => (typeof l === 'string' ? l : l.name));
}

function currentStatuses(issue) {
  return labelNames(issue).filter(n => n.startsWith(STATUS_PREFIX));
}

/**
 * Een epic is geen losse release-eenheid: hij wordt nooit via een 'fix(#NNN):'
 * commit-subject of een CHANGELOG-attributie '(#NNN)' afgehandeld, dus
 * close-released-issues.yml verwijdert het 'status: awaiting-release'-label bij
 * een epic nooit vanzelf. label-awaiting-release.yml gebruikt dit om epics over
 * te slaan bij het zetten van dat label — anders blijft het permanent (en
 * onterecht) staan zodra een sub-issue-PR er ook maar in proza naar verwijst (#838).
 */
function isEpic(issue) {
  return labelNames(issue).includes(EPIC_LABEL);
}

/**
 * Haalt issue-referenties uit een PR-titel en -body.
 *
 * Twee sterktes, en dat onderscheid is wezenlijk (#630):
 *  - `all`    — elke #NNN, inclusief kale kruisverwijzingen in proza.
 *  - `strong` — alleen "deze PR pakt dat issue aan": het nummer staat in de titel BINNEN
 *               HAAKJES (conventie 'fix(#NNN): ...'), of achter een sluitend keyword in de
 *               body ('Closes #N', 'Fixes #N').
 *
 * Alles wat de staat van een issue verandert (status zetten, heropenen, sluiten) hoort
 * `strong` te gebruiken. PR #633 noemde '#624' alleen in proza en haalde daarmee een
 * correct gesloten issue uit de dood.
 *
 * #1179 — waarom haakjes, en niet "elk nummer in de titel". Die ruimere lezing stond hier
 * eerder en ondermijnde de versmalling die #838/#630 juist bedoelden, nota bene in de
 * workflow die issues SLUIT. Echte titels uit deze repo lopen erop vast:
 *
 *   'refactor(#1122): review epic #986 — endpoint-helper, ...'
 *        #986 is een epic-kruisverwijzing in proza; die mocht nooit meegesloten worden.
 *   'docs(#1051): CHANGELOG-verwijzing naar #1048 gebruikte per ongeluk haakjesnotatie'
 *        #1048 is het onderwerp van de zin, niet het opgeleverde werk.
 *
 * Haakjes als scheidslijn is precies de conventie die AGENTS.md al vastlegt voor de
 * CHANGELOG — '(#N)' is attributie van opgeleverd werk, een kaal '#N' in proza is een
 * kruisverwijzing — en die de bronnen 1 en 2 van close-released-issues.yml al hanteren.
 * Deze functie sluit daar nu op aan in plaats van hem te ondergraven.
 *
 * De regel dekt alle vormen die in deze repo voorkomen, óók de meervoudige:
 *   'fix(#1131): ... (Postgres, #1132)'                    -> 1131, 1132
 *   'feat(#1093): ... + fix(#1112): ...'                   -> 1093, 1112
 *   'chore: sync met main-hotfixes (#1095, #1098, #1101)'  -> 1095, 1098, 1101
 *
 * Bewust conservatief: een kaal nummer in de titel wordt genegeerd. Een gemist issue
 * blijft open staan en valt op; een ten onrechte gesloten issue moet met de hand worden
 * heropend (v2.18.0.1, zie AGENTS.md). Die asymmetrie bepaalt de richting van de twijfel.
 */
function extractIssueRefs(title, body) {
  const safeTitle = title || '';
  const safeBody = body || '';
  const text = `${safeTitle}\n${safeBody}`;

  const all = [...new Set(
    [...text.matchAll(/#(\d+)/g)].map(m => parseInt(m[1], 10))
  )];

  // Alleen nummers binnen haakjes tellen als titel-attributie; zie de toelichting hierboven.
  // Eerst elke haakjesgroep pakken, daarna de nummers daarbinnen — zo werkt zowel
  // 'fix(#1131):' als '(Postgres, #1132)' als '(#1095, #1098, #1101)'.
  const titleNumbers = [...safeTitle.matchAll(/\(([^()]*)\)/g)]
    .flatMap(group => [...group[1].matchAll(/#(\d+)/g)].map(m => parseInt(m[1], 10)));
  const closingNumbers = [...safeBody.matchAll(/\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+#(\d+)/gi)]
    .map(m => parseInt(m[1], 10));

  return {
    all,
    strong: new Set([...titleNumbers, ...closingNumbers]),
  };
}

/**
 * PR-tekst mag alleen issues selecteren vanuit deze repository, met bewezen schrijfrechten
 * of de expliciet toegestane ingebouwde Dependabot-identiteit. Bij twijfel geen wijziging.
 */
async function trustedPullRequestOrigin({ github, context, core, pr }) {
  const repositoryId = context.payload.repository?.id;
  const baseId = pr?.base?.repo?.id;
  const headId = pr?.head?.repo?.id;
  const author = pr?.user?.login;
  if (!Number.isInteger(repositoryId) || baseId !== repositoryId || headId !== repositoryId || !author) {
    core.notice('PR-herkomst is niet als repositorybranch bewezen; issuestatus ongewijzigd.');
    return false;
  }

  // GitHub levert de auteuridentiteit; geen algemene vrijstelling voor andere bots of forks.
  if (pr.user.type === 'Bot' && author === 'dependabot[bot]') return true;

  try {
    const response = await github.request('GET /repos/{owner}/{repo}/collaborators/{username}/permission', {
      owner: context.repo.owner,
      repo: context.repo.repo,
      username: author,
      headers: API_VERSION_HEADERS,
    });
    if (['write', 'admin'].includes(response.data?.permission)) return true;
  } catch {
    core.warning('PR-auteursrechten konden niet worden gecontroleerd; issuestatus niet bijgewerkt. Controleer of het workflowtoken de repositorypermissies mag lezen en voer de workflow daarna opnieuw uit.');
    return false;
  }

  core.notice('PR-auteur heeft geen bewezen schrijfrechten; issuestatus ongewijzigd.');
  return false;
}

/** Past de PR-statusovergang pas toe na de vertrouwenspoort voor PR-tekst. */
async function updatePullRequestIssueStatuses({ github, context, core }) {
  const pr = context.payload.pull_request;
  const action = context.payload.action;
  if (!pr || !['opened', 'reopened', 'ready_for_review', 'converted_to_draft', 'closed'].includes(action)) {
    return 'skipped';
  }
  // De develop-mergeworkflow beheert awaiting-release en verwijdert ook beurtlabels.
  if (action === 'closed' && pr.merged) return 'merged';
  if (!await trustedPullRequestOrigin({ github, context, core, pr })) return 'untrusted';

  const status = action === 'closed' ? 'status: triage'
    : action === 'converted_to_draft' ? 'status: in-progress'
    : action === 'ready_for_review' ? 'status: review-needed'
    : pr.draft ? 'status: in-progress' : 'status: review-needed';
  const { strong } = extractIssueRefs(pr.title, pr.body);
  if (strong.size === 0) {
    core.notice('Geen sterke issueverwijzing in PR-tekst; issuestatus ongewijzigd.');
    return 'unlinked';
  }
  for (const issueNumber of strong) {
    await setIssueStatus({ github, context, core, issueNumber, status });
  }
  return 'updated';
}

/**
 * Status die bij de toestand van één PR hoort (#1867). Gemerged = wachten op de release;
 * anders draft = in-progress en ready = review-needed. Een gesloten, niet-gemerged PR levert
 * geen status op: de reconciliatie hoort alleen open PR's te beoordelen.
 */
function statusVoorPullRequest(pr) {
  if (!pr) return null;
  if (pr.merged || pr.merged_at) return 'status: awaiting-release';
  if (pr.state && pr.state !== 'open') return null;
  return pr.draft ? 'status: in-progress' : 'status: review-needed';
}

const FASE_GEMERGED_MARKER = (prNumber) => `<!-- fase-gemerged pr=${prNumber} -->`;
const GELDIGE_MERGE_SHA = /^[0-9a-f]{40}$/i;

/**
 * Plaatst op het issue één idempotente fasecomment `Fase: gemerged` met PR-nummer en
 * volledige merge-SHA (#1867). Het formaat moet het overdrachtscontract van beurt-invariant.js
 * halen: `isOverdracht` eist naast de fase minstens één herkend veld (implementer, reviewer, volgende
 * beurt of aangeboden SHA); `Merge-SHA` is dat niet. Daarom staat er `Volgende beurt: geen`, anders
 * negeert de bewaking deze comment en blijft een oudere overdracht leidend. Een tweede run voor
 * dezelfde PR plaatst niets; een ontbrekende of onvolledige merge-SHA plaatst niets.
 *
 * @returns {Promise<'posted'|'unchanged'|'skipped'>}
 */
async function plaatsFasecommentGemerged({ github, context, core, issueNumber, prNumber, mergeSha }) {
  if (!Number.isInteger(prNumber) || !GELDIGE_MERGE_SHA.test(String(mergeSha || ''))) {
    core.warning(`#${issueNumber}: PR-nummer of merge-SHA ontbreekt — fasecomment niet geplaatst`);
    return 'skipped';
  }
  const marker = FASE_GEMERGED_MARKER(prNumber);
  const comments = await github.paginate(github.rest.issues.listComments, {
    owner: context.repo.owner,
    repo: context.repo.repo,
    issue_number: issueNumber,
    per_page: 100,
    headers: API_VERSION_HEADERS,
  });
  if (comments.some(c => typeof c.body === 'string' && c.body.includes(marker))) {
    return 'unchanged';
  }
  await github.rest.issues.createComment({
    owner: context.repo.owner,
    repo: context.repo.repo,
    issue_number: issueNumber,
    body: `${marker}\nFase: gemerged\nPR: #${prNumber}\nMerge-SHA: ${mergeSha}\nVolgende beurt: geen\n\n`
      + 'De PR zit in `develop`; alleen de release resteert. Er is geen volgende beurt en het issue draagt geen `turn:`-label.',
    headers: API_VERSION_HEADERS,
  });
  core.notice(`#${issueNumber}: fase 'gemerged' vastgelegd (PR #${prNumber})`);
  return 'posted';
}

/**
 * Handmatig startbare herstelroute (#1867): leidt de status van één issue af uit zijn enige sterk
 * gekoppelde OPEN PR. Zonder koppeling, met meerdere koppelingen, voor een gesloten issue of een
 * PR met onbewezen herkomst verandert er niets. Beschermde statussen blijven staan en
 * `turn:`-labels worden hier nooit aangeraakt. Idempotent via setIssueStatus.
 *
 * @returns {Promise<'closed'|'unlinked'|'ambiguous'|'untrusted'|'skipped'|'set'|'unchanged'|'protected'>}
 */
async function reconcileIssueStatus({ github, context, core, issueNumber }) {
  let issue;
  try {
    issue = (await github.rest.issues.get({
      owner: context.repo.owner,
      repo: context.repo.repo,
      issue_number: issueNumber,
      headers: API_VERSION_HEADERS,
    })).data;
  } catch {
    core.warning(`#${issueNumber}: kon issue niet ophalen — niets gewijzigd`);
    return 'skipped';
  }
  if (issue.pull_request) return 'skipped';
  if (issue.state === 'closed') {
    core.notice(`#${issueNumber}: issue is gesloten — niets gewijzigd`);
    return 'closed';
  }

  const open = await github.paginate(github.rest.pulls.list, {
    owner: context.repo.owner,
    repo: context.repo.repo,
    state: 'open',
    per_page: 100,
    headers: API_VERSION_HEADERS,
  });
  const gekoppeld = open.filter(pr => extractIssueRefs(pr.title, pr.body).strong.has(issueNumber));
  if (gekoppeld.length === 0) {
    core.notice(`#${issueNumber}: geen sterk gekoppelde open PR — niets gewijzigd`);
    return 'unlinked';
  }
  if (gekoppeld.length > 1) {
    core.notice(`#${issueNumber}: ${gekoppeld.length} sterk gekoppelde open PR's — niets gewijzigd`);
    return 'ambiguous';
  }

  const pr = gekoppeld[0];
  if (!await trustedPullRequestOrigin({ github, context, core, pr })) return 'untrusted';
  const status = statusVoorPullRequest(pr);
  if (!status) return 'skipped';
  return setIssueStatus({ github, context, core, issueNumber, status });
}

/**
 * Zet precies één status-label op een issue en verwijdert de andere.
 *
 * @param {object}  opts.github        octokit-client van actions/github-script
 * @param {object}  opts.context       context van actions/github-script
 * @param {object}  opts.core          core van actions/github-script
 * @param {number}  opts.issueNumber
 * @param {string}  opts.status        het gewenste label, bijv. 'status: review-needed'
 * @param {boolean} opts.onlyIfNone    alleen zetten als er nog géén status-label staat
 * @param {boolean} opts.respectProtected  laat blocked/wont-fix/waiting-owner staan (default true)
 * @returns {Promise<'set'|'unchanged'|'protected'|'skipped'>}
 */
async function setIssueStatus({
  github,
  context,
  core,
  issueNumber,
  status,
  onlyIfNone = false,
  respectProtected = true,
}) {
  let issue;
  try {
    issue = (await github.rest.issues.get({
      owner: context.repo.owner,
      repo: context.repo.repo,
      issue_number: issueNumber,
      headers: API_VERSION_HEADERS,
    })).data;
  } catch {
    core.warning(`#${issueNumber}: kon issue niet ophalen — overgeslagen`);
    return 'skipped';
  }

  // issues.get retourneert ook PR's — die hebben geen lifecycle-status.
  if (issue.pull_request) {
    return 'skipped';
  }

  const current = currentStatuses(issue);

  if (onlyIfNone && current.length > 0) {
    core.notice(`#${issueNumber}: heeft al '${current.join(', ')}' — ongewijzigd`);
    return 'unchanged';
  }

  if (respectProtected) {
    const held = current.filter(n => PROTECTED.includes(n));
    if (held.length > 0) {
      core.notice(`#${issueNumber}: '${held.join(', ')}' is handmatig gezet — niet overschreven`);
      return 'protected';
    }
  }

  if (current.length === 1 && current[0] === status) {
    return 'unchanged';
  }

  // Eerst toevoegen, dan de oude verwijderen: bij een falende run houdt het issue zo
  // altijd minstens één status in plaats van even helemaal geen.
  if (status) {
    await github.rest.issues.addLabels({
      owner: context.repo.owner,
      repo: context.repo.repo,
      issue_number: issueNumber,
      labels: [status],
      headers: API_VERSION_HEADERS,
    });
  }

  for (const stale of current.filter(n => n !== status)) {
    await github.rest.issues.removeLabel({
      owner: context.repo.owner,
      repo: context.repo.repo,
      issue_number: issueNumber,
      name: stale,
      headers: API_VERSION_HEADERS,
    }).catch(() => {});
  }

  core.notice(
    status
      ? `#${issueNumber}: status → '${status}'${current.length ? ` (was: ${current.join(', ')})` : ''}`
      : `#${issueNumber}: status-labels verwijderd (${current.join(', ')})`
  );
  return 'set';
}

/** Verwijdert alle status-labels — voor een issue dat definitief gesloten wordt. */
async function clearIssueStatus({ github, context, core, issueNumber }) {
  return setIssueStatus({
    github,
    context,
    core,
    issueNumber,
    status: null,
    respectProtected: false,
  });
}

/**
 * Verwijdert alle `turn:`-labels van een issue (#1669). Na een merge naar develop is niemand meer
 * aan zet: alleen de release resteert. Zonder deze stap bleef `turn: owner` (of `codex`) na de
 * overdracht op een issue staan dat al op `status: awaiting-release` wacht.
 *
 * @returns {Promise<string[]>} de verwijderde labels
 */
async function clearTurnLabels({ github, context, core, issueNumber }) {
  let issue;
  try {
    issue = (await github.rest.issues.get({
      owner: context.repo.owner,
      repo: context.repo.repo,
      issue_number: issueNumber,
      headers: API_VERSION_HEADERS,
    })).data;
  } catch (e) {
    core.warning(`#${issueNumber}: kon issue niet ophalen (${e.message}) — turn-labels niet opgeschoond`);
    return [];
  }
  if (issue.pull_request) return [];

  const turns = labelNames(issue).filter(n => n.startsWith(TURN_PREFIX));
  const removed = [];
  const failed = [];
  for (const stale of turns) {
    try {
      await github.rest.issues.removeLabel({
        owner: context.repo.owner,
        repo: context.repo.repo,
        issue_number: issueNumber,
        name: stale,
        headers: API_VERSION_HEADERS,
      });
      removed.push(stale);
    } catch (e) {
      // Aantoonbaar al afwezig (404) is idempotent en onschuldig; elke andere fout is een mislukking.
      if (e && e.status === 404) {
        removed.push(stale);
      } else {
        failed.push(stale);
        core.warning(`#${issueNumber}: '${stale}' verwijderen mislukt (${e && e.message})`);
      }
    }
  }
  if (removed.length > 0) core.notice(`#${issueNumber}: turn-labels verwijderd (${removed.join(', ')})`);
  if (failed.length > 0) {
    // Alle labels zijn geprobeerd; nu hard falen zodat de workflowrun niet onterecht groen eindigt.
    throw new Error(`#${issueNumber}: turn-label(s) niet verwijderd: ${failed.join(', ')}`);
  }
  return removed;
}

module.exports = {
  STATUS_PREFIX,
  TURN_PREFIX,
  PROTECTED,
  EPIC_LABEL,
  API_VERSION_HEADERS,
  labelNames,
  currentStatuses,
  isEpic,
  extractIssueRefs,
  trustedPullRequestOrigin,
  updatePullRequestIssueStatuses,
  statusVoorPullRequest,
  FASE_GEMERGED_MARKER,
  plaatsFasecommentGemerged,
  reconcileIssueStatus,
  setIssueStatus,
  clearIssueStatus,
  clearTurnLabels,
};
