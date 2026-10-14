// issue-router.js
//
// Routetabel van .github/workflows/issue-router.yml (#1721, plan v4). De router is het herbruikbare
// orkestratiepunt voor issue-events: een nieuwe beurtsoort is één rij in ROUTES plus één aanroep-job.
// Pure functies, zonder GitHub-aanroepen, zodat de tabel zonder netwerk te testen is.
//
// FASE 1: de router heeft alleen `workflow_dispatch` (inputs `issue_number` en `dry_run`), geen trigger
// op issue-events. De dispatch bootst het event `labeled` + `turn: claude-code` na. De handlers
// `status` (setIssueStatus, nu in label-issue-status.yml) en `exclusief` (maakTurnExclusief, nu in
// turn-label-exclusief.yml) worden in fase 2 aangesloten; hier meldt de route-job alleen welke rijen
// zouden gelden en voert hij niets uit.

const TURN_PREFIX = 'turn: ';
const TURN_CLAUDE = 'turn: claude-code';

/**
 * Eén rij per route. `on` = issue-action(s), `labelPrefix` of `label` filtert op het label dat het event
 * veroorzaakte. `beurt` (alleen bij handler `beurt`) is de waarde die de route-job als output geeft en die
 * de aanroep-job `beurt` activeert.
 */
const ROUTES = [
  { on: ['opened', 'reopened'], handler: 'status' },
  { on: ['labeled'], labelPrefix: TURN_PREFIX, handler: 'exclusief' },
  { on: ['labeled'], label: TURN_CLAUDE, handler: 'beurt', beurt: 'claude-code' },
];

const ISSUE_NUMBER_RE = /^[1-9]\d{0,8}$/;

function rowMatches(row, { action, label }) {
  if (!row.on.includes(action)) return false;
  if (row.label !== undefined) return label === row.label;
  if (row.labelPrefix !== undefined) return typeof label === 'string' && label.startsWith(row.labelPrefix);
  return true;
}

/**
 * @param {{action: string, label?: string, issueNumber: unknown}} event
 * @returns {{valid: boolean, reason?: string, issueNumber?: number, handlers: string[], beurt: string}}
 */
function route(event) {
  const raw = typeof event.issueNumber === 'number' ? String(event.issueNumber) : event.issueNumber;
  if (typeof raw !== 'string' || !ISSUE_NUMBER_RE.test(raw)) {
    return { valid: false, reason: 'issue_number is geen positief geheel getal', handlers: [], beurt: '' };
  }
  const rows = ROUTES.filter((r) => rowMatches(r, event));
  const beurten = rows.filter((r) => r.handler === 'beurt').map((r) => r.beurt);
  return {
    valid: true,
    issueNumber: Number(raw),
    handlers: rows.map((r) => r.handler),
    // Eén beurtsoort per event; een tabel met twee conflicterende rijen start niets.
    beurt: beurten.length === 1 ? beurten[0] : '',
  };
}

/** Het event dat de fase-1-dispatch nabootst (zie bovenaan). */
function dispatchEvent(issueNumber) {
  return { action: 'labeled', label: TURN_CLAUDE, issueNumber };
}

module.exports = { ROUTES, route, dispatchEvent, TURN_CLAUDE, TURN_PREFIX };
