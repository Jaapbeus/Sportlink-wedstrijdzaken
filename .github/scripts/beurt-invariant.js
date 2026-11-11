// beurt-invariant.js (#1858)
//
// Bewaakt de invariant "het enige `turn:`-label = de volgende beurt uit de nieuwste vertrouwde
// overdrachtscomment". Alleen SIGNALEREN: deze helper voegt nooit een label toe of verwijdert er een.
//
// Waarom: de beurt staat op twee plekken (label en fasecomment), geschreven door verschillende
// schrijvers (Codex CLI, de Codex-connector, Claude Code, de bot) in losse aanroepen zonder
// volgorde of controle. Op 2026-11-11 gaf dat: een label zonder bijbehorende aanvraag op het issue
// (de aanvraag stond alleen op de PR), een label vóór zijn fasecomment, een afgekapte head-SHA en
// een handmatig gezet `status: waiting-codex`. `turn-label-exclusief` controleert alleen "hoogstens
// één"; niets vergeleek label en comment. Eerst bewezen signaleren, pas daarna eventueel herstellen.
//
// Opzet: pure functies (parseOverdracht, verwachteBeurt, evalueerIssue, signaalBody) die zonder
// GitHub te testen zijn, plus run() die de gegevens ophaalt en één gededupliceerde signaalcomment
// per afwijking plaatst. Tests: beurt-invariant.test.js (draait in build.yml).

const crypto = require('crypto');
const {
  TURN_PREFIX,
  API_VERSION_HEADERS,
  labelNames,
  extractIssueRefs,
} = require('./issue-status.js');

// Marker van onze eigen signaalcomments: zo negeert de parser ze (anders leest hij zijn eigen
// uitleg over "Volgende beurt" als overdracht) en herkent de dedup de vorige melding.
const SIGNAAL_MARKER = '<!-- beurt-invariant';
const VEROUDERD_STATUS = 'status: waiting-codex';
const AWAITING_RELEASE = 'status: awaiting-release';
const VERTROUWDE_ASSOCIATIES = ['OWNER', 'MEMBER', 'COLLABORATOR'];
const BOT_LOGIN = 'github-actions[bot]';
const STANDAARD_GRACE_MINUTEN = 30;
const RECENT_DAGEN = 7;
const GELDIGE_SHA = /^[0-9a-f]{40}$/i;

// Veldwaarde: tot einde regel, of tot ". " / ";" (inline-vorm). Een punt midden in een token
// (versie, bestandsnaam) beëindigt de waarde niet.
const WAARDE = '((?:[^\\n;.]|\\.(?!\\s|$))*)';
const VOOR = '(?:^|[\\s.;,(|>*-])';
const veld = (namen) => new RegExp(`${VOOR}(?:${namen})\\s*:\\s*${WAARDE}`, 'im');

const VELDEN = {
  fase: veld('Fase|Phase'),
  volgendeBeurt: veld('Volgende beurt|Next turn'),
  reviewer: veld('Reviewer'),
  implementer: veld('Implementer'),
  sha: veld('Aangeboden head[- ]SHA|Offered head(?:[- ]SHA)?|Frozen head|Reviewed head|Beoordeelde SHA'),
  pr: veld('PR'),
  ronde: veld('Ronde|Round'),
};

function schoon(body) {
  return String(body || '').replace(/\*\*|__|`/g, '').replace(/\r/g, '');
}

/**
 * Leest de overdrachtsvelden uit één comment (Nederlands of Engels, regel- of inline-vorm).
 * De ruwe SHA-token blijft bewaard, ook als hij geen 40 hex is: juist dan moet hij gesignaleerd worden.
 */
function parseOverdracht(body) {
  const tekst = schoon(body);
  const ruw = {};
  for (const [naam, re] of Object.entries(VELDEN)) {
    const m = re.exec(tekst);
    ruw[naam] = m ? m[1].trim() : null;
  }
  const prMatch = ruw.pr && (/#(\d+)/.exec(ruw.pr) || /\/pull\/(\d+)/.exec(ruw.pr));
  const rondeMatch = ruw.ronde && /\d+/.exec(ruw.ronde);
  const shaToken = ruw.sha ? ruw.sha.split(/\s+/)[0].replace(/^[<\[(]+|[>\])]+$/g, '') : null;
  return {
    fase: ruw.fase || null,
    volgendeBeurt: ruw.volgendeBeurt || null,
    reviewer: ruw.reviewer || null,
    implementer: ruw.implementer || null,
    sha: shaToken || null,
    pr: prMatch ? parseInt(prMatch[1], 10) : null,
    ronde: rondeMatch ? parseInt(rondeMatch[0], 10) : null,
  };
}

/** Vertaalt vrije tekst naar één beurt; meerdere verschillende treffers = onduidelijk (null). */
function naarBeurt(tekst) {
  const t = String(tekst || '').toLowerCase();
  if (!t) return null;
  if (t.includes('merge door implementer')) return 'geen';
  const gevonden = new Set();
  if (t.includes('claude')) gevonden.add('claude-code');
  if (t.includes('codex')) gevonden.add('codex');
  if (/owner|eigenaar/.test(t)) gevonden.add('owner');
  if (/\bgeen\b|\bnone\b/.test(t)) gevonden.add('geen');
  return gevonden.size === 1 ? [...gevonden][0] : null;
}

/**
 * Verwachte beurt uit een overdracht: een expliciete "Volgende beurt" wint; anders volgt hij uit de
 * fase (review = de reviewer, verwerking/implementatie = de implementer, eigenaarsbesluit = owner).
 * `geen` betekent "geen verwachting over het label" (bijv. merge door de implementer); null = onbekend.
 */
function verwachteBeurt(parsed) {
  if (!parsed) return null;
  const expliciet = naarBeurt(parsed.volgendeBeurt);
  if (expliciet) return expliciet;
  const fase = String(parsed.fase || '').toLowerCase();
  if (/eigenaarsbesluit|owner decision/.test(fase)) return 'owner';
  if (/review/.test(fase)) return naarBeurt(parsed.reviewer);
  if (/verwerking|processing|implementatie|implementation/.test(fase)) return naarBeurt(parsed.implementer);
  return null;
}

const isSignaal = (c) => String((c && c.body) || '').includes(SIGNAAL_MARKER);

function isVertrouwd(c) {
  if (!c) return false;
  const login = c.user && c.user.login;
  return VERTROUWDE_ASSOCIATIES.includes(c.author_association) || login === BOT_LOGIN;
}

const tijd = (c) => Date.parse((c && (c.created_at || c.createdAt)) || '') || 0;

/** Alle vertrouwde, niet-eigen comments met een leesbare fase, met herkomst en tijd. */
function verzamelOverdrachten(issueComments, prs) {
  const bronnen = [{ comments: issueComments || [], pr: null }]
    .concat((prs || []).filter((p) => !p.state || p.state === 'open')
      .map((p) => ({ comments: p.comments || [], pr: p.number })));
  const lijst = [];
  let volgnr = 0;
  for (const bron of bronnen) {
    for (const c of bron.comments) {
      volgnr += 1;
      if (!isVertrouwd(c) || isSignaal(c)) continue;
      const parsed = parseOverdracht(c.body);
      if (parsed.fase) lijst.push({ parsed, tijd: tijd(c), bronPr: bron.pr, volgnr });
    }
  }
  // Nieuwste eerst; bij gelijke tijd wint de later gelezen comment.
  return lijst.sort((a, b) => (b.tijd - a.tijd) || (b.volgnr - a.volgnr));
}

const TURN_BEURTEN = ['codex', 'claude-code', 'owner'];

function shaBevinding(overdrachten, verwacht, prs) {
  if (verwacht !== 'claude-code') return null;
  const aanvraag = overdrachten.find((o) => /review/i.test(o.parsed.fase));
  if (!aanvraag || naarBeurt(aanvraag.parsed.reviewer) !== 'claude-code') return null;
  const sha = aanvraag.parsed.sha;
  if (!sha) {
    return { code: 'REVIEW_SHA_ONGELDIG', detail: 'reden=ontbreekt', tekst: 'De nieuwste reviewaanvraag bevat geen aangeboden head-SHA.' };
  }
  if (!GELDIGE_SHA.test(sha)) {
    return { code: 'REVIEW_SHA_ONGELDIG', detail: 'reden=lengte', tekst: 'De aangeboden head-SHA in de nieuwste reviewaanvraag is geen volledige SHA van 40 hexadecimale tekens.' };
  }
  const prNr = aanvraag.parsed.pr || aanvraag.bronPr;
  const pr = (prs || []).find((p) => p.number === prNr);
  if (pr && pr.headSha && pr.headSha.toLowerCase() !== sha.toLowerCase()) {
    return { code: 'REVIEW_SHA_ONGELDIG', detail: 'reden=verouderd', tekst: 'De aangeboden head-SHA wijkt af van de huidige head van de gekoppelde PR (er is na de aanvraag gepusht).' };
  }
  return null;
}

/**
 * Beoordeelt één issue. Alleen vertrouwde comments tellen (eigenaar/lid/medewerker of de Actions-bot);
 * wij lezen daarbij óók de comments van gekoppelde open PR's, want Codex zet een aanvraag soms alleen daar.
 * @returns {{code:string, detail:string, tekst:string}[]}
 */
function evalueerIssue({ labels, issueComments, prs, now, graceMinuten = STANDAARD_GRACE_MINUTEN, laatsteLabelWijziging }) {
  const namen = labelNames({ labels });
  const turns = namen.filter((n) => n.startsWith(TURN_PREFIX)).map((n) => n.slice(TURN_PREFIX.length));
  const nuMs = now instanceof Date ? now.getTime() : (typeof now === 'number' ? now : Date.parse(now) || Date.now());
  const graceMs = graceMinuten * 60 * 1000;
  const overdrachten = verzamelOverdrachten(issueComments, prs);
  const nieuwste = overdrachten[0] || null;
  const verwacht = nieuwste ? verwachteBeurt(nieuwste.parsed) : null;
  const findings = [];

  if (turns.length > 1) {
    findings.push({ code: 'MEERDERE_TURN', detail: `aantal=${turns.length}`, tekst: 'Het issue heeft meer dan één turn:-label; er mag er hoogstens één staan.' });
  } else if (TURN_BEURTEN.includes(verwacht)) {
    if (turns.length === 0 && !namen.includes(AWAITING_RELEASE)) {
      findings.push({ code: 'GEEN_TURN', detail: `verwacht=${verwacht}`, tekst: `De nieuwste overdracht verwacht beurt "${verwacht}", maar het issue heeft geen turn:-label.` });
    } else if (turns.length === 1 && turns[0] !== verwacht) {
      const labelMs = laatsteLabelWijziging ? Date.parse(laatsteLabelWijziging) || 0 : 0;
      const verouderd = nuMs - nieuwste.tijd > graceMs && nuMs - labelMs > graceMs;
      if (verouderd) {
        findings.push({ code: 'TURN_WIJKT_AF', detail: `label=${turns[0]};verwacht=${verwacht}`, tekst: `Het label zegt "${turns[0]}", maar de nieuwste overdracht noemt "${verwacht}" als volgende beurt.` });
      }
    }
  }
  const sha = shaBevinding(overdrachten, verwacht, prs);
  if (sha) findings.push(sha);
  if (namen.includes(VEROUDERD_STATUS)) {
    findings.push({ code: 'VEROUDERDE_STATUS', detail: 'label=waiting-codex', tekst: 'Het gedeprecieerde label "status: waiting-codex" staat op het issue; het bestaat niet meer.' });
  }
  return findings;
}

const hashVan = (findings) => crypto.createHash('sha256')
  .update(findings.map((f) => `${f.code}:${f.detail || ''}`).sort().join('|')).digest('hex');

/** Signaaltekst met stabiele marker; de hash hangt alleen van codes en details af (dedup). */
function signaalBody(issueNumber, findings) {
  const regels = findings.map((f) => `- \`${f.code}\`: ${f.tekst}`).join('\n');
  return `${SIGNAAL_MARKER} hash=${hashVan(findings)} -->
**Beurt-invariant: afwijking gesignaleerd op #${issueNumber}**

${regels}

Er zijn **geen labels gewijzigd**; deze controle signaleert alleen. Herstel de overdracht in deze volgorde:
1. Plaats eerst de fasecomment **op het issue** (ook als de reviewaanvraag zelf op de PR staat), met Fase, Implementer, Reviewer, PR, de volledige head-SHA van 40 tekens en Volgende beurt.
2. Zet daarna het \`turn:\`-label (verwijderen en toevoegen in één aanroep).
3. Zet nooit handmatig een \`status:\`-label; \`status: waiting-codex\` bestaat niet meer.

Zie CONTRIBUTING.md, "Overdracht en turn-label (#1858)".`;
}

const HASH_RE = /<!-- beurt-invariant hash=([0-9a-f]+)/;

/** Hash van de nieuwste eigen signaalcomment (vertrouwde auteur), of null. */
function laatsteSignaalHash(issueComments) {
  const eigen = (issueComments || []).filter((c) => isSignaal(c) && isVertrouwd(c));
  if (!eigen.length) return null;
  const nieuwste = eigen.reduce((a, b) => (tijd(b) >= tijd(a) ? b : a));
  const m = HASH_RE.exec(nieuwste.body || '');
  return m ? m[1] : null;
}

// Aan het eind van het label-verloop pas events ophalen: alleen nodig om "recent gewisseld" te bepalen.
async function laatsteTurnLabelTijd(github, owner, repo, issueNumber) {
  const events = await github.paginate(github.rest.issues.listEvents, {
    owner, repo, issue_number: issueNumber, per_page: 100, headers: API_VERSION_HEADERS,
  });
  return events
    .filter((e) => (e.event === 'labeled' || e.event === 'unlabeled') && e.label && String(e.label.name).startsWith(TURN_PREFIX))
    .map((e) => e.created_at).sort().pop() || null;
}

async function laadContext(github, owner, repo) {
  const opts = { owner, repo, per_page: 100, headers: API_VERSION_HEADERS };
  const alles = await github.paginate(github.rest.issues.listForRepo, { ...opts, state: 'open' });
  const issues = new Map(alles.filter((i) => !i.pull_request).map((i) => [i.number, i]));
  const openPrs = await github.paginate(github.rest.pulls.list, { ...opts, state: 'open' });
  const prsPerIssue = new Map();
  const prNummers = new Map();
  for (const pr of openPrs) {
    prNummers.set(pr.number, pr);
    for (const nr of extractIssueRefs(pr.title, pr.body).strong) {
      if (!prsPerIssue.has(nr)) prsPerIssue.set(nr, []);
      prsPerIssue.get(nr).push({ number: pr.number, headSha: pr.head && pr.head.sha, state: 'open' });
    }
  }
  return { issues, prsPerIssue, prNummers };
}

/** Issues om te beoordelen: met turn:/waiting-codex-label, of met een recente vertrouwde overdracht. */
async function kiesIssues(github, owner, repo, ctx, nu) {
  const kandidaten = new Set();
  for (const [nr, issue] of ctx.issues) {
    const namen = labelNames(issue);
    if (namen.includes(VEROUDERD_STATUS) || namen.some((n) => n.startsWith(TURN_PREFIX))) kandidaten.add(nr);
  }
  const since = new Date(nu.getTime() - RECENT_DAGEN * 24 * 3600 * 1000).toISOString();
  const recent = await github.paginate(github.rest.issues.listCommentsForRepo, {
    owner, repo, since, per_page: 100, headers: API_VERSION_HEADERS,
  });
  for (const c of recent) {
    if (!isVertrouwd(c) || isSignaal(c) || !parseOverdracht(c.body).fase) continue;
    const nr = parseInt(String(c.issue_url || '').split('/').pop(), 10);
    if (ctx.issues.has(nr)) kandidaten.add(nr);
    else if (ctx.prNummers.has(nr)) {
      for (const [issueNr, prs] of ctx.prsPerIssue) if (prs.some((p) => p.number === nr)) kandidaten.add(issueNr);
    }
  }
  return [...kandidaten].filter((nr) => ctx.issues.has(nr)).sort((a, b) => a - b);
}

async function beoordeelIssue(github, owner, repo, ctx, nr, nu, graceMinuten) {
  const opts = { owner, repo, per_page: 100, headers: API_VERSION_HEADERS };
  const issueComments = await github.paginate(github.rest.issues.listComments, { ...opts, issue_number: nr });
  const prs = [];
  for (const pr of ctx.prsPerIssue.get(nr) || []) {
    const comments = await github.paginate(github.rest.issues.listComments, { ...opts, issue_number: pr.number });
    prs.push({ ...pr, comments });
  }
  const invoer = { labels: ctx.issues.get(nr).labels, issueComments, prs, now: nu, graceMinuten };
  let findings = evalueerIssue(invoer);
  if (findings.some((f) => f.code === 'TURN_WIJKT_AF')) {
    const laatste = await laatsteTurnLabelTijd(github, owner, repo, nr);
    findings = evalueerIssue({ ...invoer, laatsteLabelWijziging: laatste });
  }
  return { findings, issueComments };
}

/**
 * Hoofdingang vanuit actions/github-script. Wijzigt nooit labels. Een fout bij één issue breekt de
 * run niet af (core.warning), maar laat de run aan het eind wel falen: een niet-beoordeeld issue is
 * een gat in de bewaking en moet zichtbaar zijn.
 */
async function run({ github, context, core, now = new Date(), dryRun = false, graceMinuten = STANDAARD_GRACE_MINUTEN }) {
  const { owner, repo } = context.repo;
  const ctx = await laadContext(github, owner, repo);
  const nummers = await kiesIssues(github, owner, repo, ctx, now);
  const uitkomst = { beoordeeld: nummers.length, gesignaleerd: [], mislukt: [] };

  for (const nr of nummers) {
    try {
      const { findings, issueComments } = await beoordeelIssue(github, owner, repo, ctx, nr, now, graceMinuten);
      if (!findings.length) continue;
      const body = signaalBody(nr, findings);
      if (laatsteSignaalHash(issueComments) === hashVan(findings)) continue;
      uitkomst.gesignaleerd.push({ issue: nr, codes: findings.map((f) => f.code) });
      if (dryRun) core.info(`[dry-run] #${nr}: ${findings.map((f) => f.code).join(', ')}`);
      else await github.rest.issues.createComment({ owner, repo, issue_number: nr, body, headers: API_VERSION_HEADERS });
    } catch (e) {
      uitkomst.mislukt.push(nr);
      core.warning(`#${nr} niet beoordeeld: ${e.message}`);
    }
  }
  const samenvatting = `Beurt-invariant: ${uitkomst.beoordeeld} issue(s) beoordeeld, ${uitkomst.gesignaleerd.length} gesignaleerd${dryRun ? ' (dry-run)' : ''}, ${uitkomst.mislukt.length} mislukt.`;
  core.info(samenvatting);
  if (core.summary && typeof core.summary.addRaw === 'function') {
    await core.summary.addRaw(`${samenvatting}\n`).write();
  }
  if (uitkomst.mislukt.length) core.setFailed(`${uitkomst.mislukt.length} issue(s) konden niet worden beoordeeld: ${uitkomst.mislukt.map((n) => `#${n}`).join(', ')}`);
  return uitkomst;
}

module.exports = {
  SIGNAAL_MARKER,
  parseOverdracht,
  verwachteBeurt,
  evalueerIssue,
  signaalBody,
  run,
};
