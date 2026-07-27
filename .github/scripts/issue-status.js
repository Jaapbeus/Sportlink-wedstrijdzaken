// issue-status.js
//
// Gedeelde logica voor de status-labels op issues. Wordt gebruikt door
// label-issue-status.yml, label-awaiting-release.yml en close-released-issues.yml.
//
// Waarom gedeeld: een issue mag hoogstens één 'status: '-label hebben. Zonder één
// centrale plek voor die regel dupliceren drie workflows dezelfde remove/add-dans en
// lopen ze onvermijdelijk uiteen — dan stapelen labels zich op of verdwijnen ze.
// Zie CLAUDE.md, sectie "Issue-lifecycle".

const STATUS_PREFIX = 'status: ';

// Statussen die een mens bewust zet. Automatisering overschrijft die niet: een issue dat
// op 'blocked' of 'waiting-owner' staat, is dat niet minder zodra er een PR opengaat.
const PROTECTED = [
  'status: blocked',
  'status: wont-fix',
  'status: waiting-owner',
];

function labelNames(issue) {
  return (issue.labels || []).map(l => (typeof l === 'string' ? l : l.name));
}

function currentStatuses(issue) {
  return labelNames(issue).filter(n => n.startsWith(STATUS_PREFIX));
}

/**
 * Haalt issue-referenties uit een PR-titel en -body.
 *
 * Twee sterktes, en dat onderscheid is wezenlijk (#630):
 *  - `all`    — elke #NNN, inclusief kale kruisverwijzingen in proza.
 *  - `strong` — alleen "deze PR pakt dat issue aan": het nummer staat in de TITEL
 *               (conventie 'fix(#NNN): ...') of achter een sluitend keyword in de body
 *               ('Closes #N', 'Fixes #N').
 *
 * Alles wat de staat van een issue verandert (status zetten, heropenen) hoort `strong`
 * te gebruiken. PR #633 noemde '#624' alleen in proza en haalde daarmee een correct
 * gesloten issue uit de dood.
 */
function extractIssueRefs(title, body) {
  const safeTitle = title || '';
  const safeBody = body || '';
  const text = `${safeTitle}\n${safeBody}`;

  const all = [...new Set(
    [...text.matchAll(/#(\d+)/g)].map(m => parseInt(m[1], 10))
  )];

  const titleNumbers = [...safeTitle.matchAll(/#(\d+)/g)].map(m => parseInt(m[1], 10));
  const closingNumbers = [...safeBody.matchAll(/\b(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)\s*:?\s+#(\d+)/gi)]
    .map(m => parseInt(m[1], 10));

  return {
    all,
    strong: new Set([...titleNumbers, ...closingNumbers]),
  };
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
    })).data;
  } catch (e) {
    core.warning(`#${issueNumber}: kon issue niet ophalen (${e.message}) — overgeslagen`);
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
    });
  }

  for (const stale of current.filter(n => n !== status)) {
    await github.rest.issues.removeLabel({
      owner: context.repo.owner,
      repo: context.repo.repo,
      issue_number: issueNumber,
      name: stale,
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

module.exports = {
  STATUS_PREFIX,
  PROTECTED,
  labelNames,
  currentStatuses,
  extractIssueRefs,
  setIssueStatus,
  clearIssueStatus,
};
