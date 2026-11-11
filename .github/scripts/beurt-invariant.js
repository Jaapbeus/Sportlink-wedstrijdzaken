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
// Issues zonder turn: waar dat bewust zo is: wachten op release of geparkeerd door de eigenaar.
// Voor deze statussen is een ontbrekend turn:-label geen afwijking.
const GEEN_TURN_UITZONDERING = ['status: awaiting-release', 'status: blocked', 'status: wont-fix', 'status: waiting-owner'];
const EINDFASE = /^(gemerged|merged)\b/i;
// Spiegel van de vertrouwensregel in claude-issue-turn-gate.js (isTrustedAuthor, regels ~79-84 en
// ~165-172: associatie OWNER/MEMBER/COLLABORATOR, of user.type === 'Bot' met een login uit TRUSTED_BOTS).
// Dat bestand exporteert alleen isTrustedAuthor, met uitsluitend claude[bot]; hier komt de
// Actions-bot erbij omdat deze controle zijn eigen signaalcomments daarmee plaatst. Een login alleen is
// niet genoeg: een gewone gebruiker kan 'claude[bot]' heten, een GitHub App heeft type 'Bot'.
const VERTROUWDE_ASSOCIATIES = ['OWNER', 'MEMBER', 'COLLABORATOR'];
const VERTROUWDE_BOTS = ['claude[bot]', 'github-actions[bot]'];
const STANDAARD_GRACE_MINUTEN = 30;
const RECENT_DAGEN = 7;
const GELDIGE_SHA = /^[0-9a-f]{40}$/i;

// Veldwaarde: tot einde regel, of tot ". " / ";" (inline-vorm). Een punt midden in een token
// (versie, bestandsnaam) beëindigt de waarde niet.
const WAARDE = '((?:[^\\n;.]|\\.(?!\\s|$))*)';
const VOOR = '(?:^|[\\s.;,(|>*-])';
const veld = (namen, voor = VOOR) => new RegExp(`${voor}(?:${namen})\\s*:\\s*${WAARDE}`, 'im');
// De fase moet een regel of zin openen. Zonder die eis telt proza dat de velden alleen noemt, zoals
// "(Phase: review, PR, Frozen head) en zet pas daarna turn: ...", als overdracht (P1 uit de review):
// de waarde liep dan tot het regeleinde en die nep-overdracht maskeerde de echte, oudere.
const VOOR_FASE = '(?:^[\\s>*-]*|[.;]\\s+)';

const VELDEN = {
  fase: veld('Fase|Phase', VOOR_FASE),
  volgendeBeurt: veld('Volgende beurt|Next turn'),
  reviewer: veld('Reviewer'),
  implementer: veld('Implementer'),
  sha: veld('Aangeboden head[- ]SHA|Offered head(?:[- ]SHA)?|Frozen head|Reviewed head|Beoordeelde SHA'),
  pr: veld('PR'),
  ronde: veld('Ronde|Round'),
};

// Geciteerde regels ("> ...") zijn een kopie van een oudere comment en tellen nooit als overdracht.
function schoon(body) {
  return String(body || '').replace(/\*\*|__|`/g, '').replace(/\r/g, '')
    .split('\n').filter((regel) => !/^\s*>/.test(regel)).join('\n');
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
  // Codex' issue-aanvragen noemen de PR vaak als proza ("PR #1846, issue #1817") zonder "PR:"-veld.
  const prMatch = (ruw.pr && (/#(\d+)/.exec(ruw.pr) || /\/pull\/(\d+)/.exec(ruw.pr)))
    || /\bPR\s*#(\d+)/i.exec(tekst) || /\/pull\/(\d+)/.exec(tekst);
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

const BEURT_NAAM = '(codex|claude[- ]?code|claude|owner|eigenaar)';
const LEIDENDE_BEURT = new RegExp(`^\\s*(?:turn:\\s*)?${BEURT_NAAM}\\b`, 'i');
const ALTERNATIEVE_BEURT = new RegExp(`^\\s*(?:of|or|en|and|/|,|\\|)\\s*(?:turn:\\s*)?${BEURT_NAAM}\\b`, 'i');

function beurtVanNaam(naam) {
  const n = naam.toLowerCase();
  if (n.startsWith('claude')) return 'claude-code';
  return n === 'codex' ? 'codex' : 'owner';
}

/**
 * Vertaalt vrije tekst naar één beurt. Een leidende naam ("turn: owner (reviewroute voor Claude Code)")
 * wint van namen in de toelichting erachter; staat er direct een alternatief achter ("Codex of Claude
 * Code") dan is het onduidelijk (null). Zonder leidende naam: meerdere verschillende treffers = null.
 */
function naarBeurt(tekst) {
  const t = String(tekst || '').toLowerCase();
  if (!t) return null;
  if (t.includes('merge door implementer')) return 'geen';
  const leidend = LEIDENDE_BEURT.exec(t);
  if (leidend) return ALTERNATIEVE_BEURT.test(t.slice(leidend[0].length)) ? null : beurtVanNaam(leidend[1]);
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
  // Een expliciete "Volgende beurt" die niet te duiden is, valt nooit terug op fase/reviewer/implementer:
  // dan ontbreekt de verwachting (null) in plaats van een gegokte beurt.
  if (parsed.volgendeBeurt) return naarBeurt(parsed.volgendeBeurt);
  const fase = String(parsed.fase || '').toLowerCase();
  if (EINDFASE.test(fase)) return null;
  if (/eigenaarsbesluit|owner decision/.test(fase)) return 'owner';
  if (/review/.test(fase)) return naarBeurt(parsed.reviewer);
  if (/verwerking|processing|implementatie|implementation/.test(fase)) return naarBeurt(parsed.implementer);
  return null;
}

/**
 * Is dit een echte overdracht? Een fase alleen is te weinig (proza dat "Fase" noemt): er moet minstens
 * één ander gestructureerd veld bij staan, en de comment moet een beurt opleveren of een eindfase
 * (gemerged) zijn. Anders beslist de eerstvolgende oudere overdracht.
 */
function isOverdracht(parsed) {
  if (!parsed || !parsed.fase) return false;
  const anderVeld = parsed.implementer || parsed.reviewer || parsed.volgendeBeurt || parsed.sha;
  if (!anderVeld) return false;
  return verwachteBeurt(parsed) !== null || EINDFASE.test(parsed.fase);
}

const isSignaal = (c) => String((c && c.body) || '').includes(SIGNAAL_MARKER);

function isVertrouwd(c) {
  if (!c) return false;
  if (VERTROUWDE_ASSOCIATIES.includes(c.author_association)) return true;
  return Boolean(c.user) && c.user.type === 'Bot' && VERTROUWDE_BOTS.includes(c.user.login);
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
      if (isOverdracht(parsed)) lijst.push({ parsed, tijd: tijd(c), bronPr: bron.pr, volgnr });
    }
  }
  // Nieuwste eerst; bij gelijke tijd wint de later gelezen comment.
  return lijst.sort((a, b) => (b.tijd - a.tijd) || (b.volgnr - a.volgnr));
}

const TURN_BEURTEN = ['codex', 'claude-code', 'owner'];

function shaBevinding(overdrachten, verwacht, prs) {
  // Geldt voor beide reviewers: alleen als de verwachte beurt de reviewer van de nieuwste reviewaanvraag is.
  if (verwacht !== 'claude-code' && verwacht !== 'codex') return null;
  const aanvraag = overdrachten.find((o) => /review/i.test(o.parsed.fase));
  if (!aanvraag || naarBeurt(aanvraag.parsed.reviewer) !== verwacht) return null;
  const sha = aanvraag.parsed.sha;
  if (!sha) {
    return { code: 'REVIEW_SHA_ONGELDIG', detail: 'reden=ontbreekt', tekst: 'De nieuwste reviewaanvraag bevat geen aangeboden head-SHA.' };
  }
  if (!GELDIGE_SHA.test(sha)) {
    return { code: 'REVIEW_SHA_ONGELDIG', detail: 'reden=lengte', tekst: 'De aangeboden head-SHA in de nieuwste reviewaanvraag is geen volledige SHA van 40 hexadecimale tekens.' };
  }
  if (headVerschoven(aanvraag, sha, prs)) {
    return { code: 'REVIEW_SHA_ONGELDIG', detail: 'reden=verouderd', tekst: 'De aangeboden head-SHA wijkt af van de huidige head van de gekoppelde PR (er is na de aanvraag gepusht).' };
  }
  return null;
}

/**
 * Wijkt de aangeboden SHA af van de huidige PR-head? De PR volgt uit het veld/de tekst of de bron van
 * de comment; ontbreekt die (aanvraag op het issue zonder PR-nummer), dan geldt de enige gekoppelde PR,
 * en bij meerdere PR's is het een afwijking als de SHA met géén enkele head overeenkomt.
 */
function headVerschoven(aanvraag, sha, prs) {
  const open = (prs || []).filter((p) => p.headSha && (!p.state || p.state === 'open'));
  const gelijk = (p) => p.headSha.toLowerCase() === sha.toLowerCase();
  const prNr = aanvraag.parsed.pr || aanvraag.bronPr;
  if (prNr) {
    const pr = open.find((p) => p.number === prNr);
    return Boolean(pr) && !gelijk(pr);
  }
  if (open.length === 1) return !gelijk(open[0]);
  return open.length > 1 && !open.some(gelijk);
}

/**
 * Beoordeelt één issue. Alleen vertrouwde comments tellen (eigenaar/lid/medewerker of de Actions-bot);
 * wij lezen daarbij óók de comments van gekoppelde open PR's, want Codex zet een aanvraag soms alleen daar.
 * @returns {{code:string, detail:string, tekst:string}[]}
 */
function evalueerIssue(invoer) {
  return beoordeel(invoer).findings;
}

const isEinde = (verwacht, nieuwste) => verwacht === 'geen' || (verwacht === null && EINDFASE.test(nieuwste.parsed.fase));

/**
 * Vergelijkt het label met de verwachte beurt. Overdracht én labelwissel moeten buiten de wachttijd
 * liggen: de volgorde (eerst fasecomment, dan label) mag even "scheef" staan zonder dat een draaiende
 * cron meteen een valse melding plaatst. MEERDERE_TURN let alleen op het tijdstip van de labelwissel.
 */
function turnBevindingen({ turns, namen, verwacht, nieuwste, nuMs, graceMs, labelMs }) {
  const labelRustig = nuMs - labelMs > graceMs;
  if (turns.length > 1) {
    return labelRustig ? [{ code: 'MEERDERE_TURN', detail: `aantal=${turns.length}`, tekst: 'Het issue heeft meer dan één turn:-label; er mag er hoogstens één staan.' }] : [];
  }
  if (!nieuwste || !labelRustig || nuMs - nieuwste.tijd <= graceMs) return [];
  if (TURN_BEURTEN.includes(verwacht)) {
    if (turns.length === 0) {
      if (GEEN_TURN_UITZONDERING.some((n) => namen.includes(n))) return [];
      return [{ code: 'GEEN_TURN', detail: `verwacht=${verwacht}`, tekst: `De nieuwste overdracht verwacht beurt "${verwacht}", maar het issue heeft geen turn:-label.` }];
    }
    if (turns[0] === verwacht) return [];
    return [{ code: 'TURN_WIJKT_AF', detail: `label=${turns[0]};verwacht=${verwacht}`, tekst: `Het label zegt "${turns[0]}", maar de nieuwste overdracht noemt "${verwacht}" als volgende beurt.` }];
  }
  if (turns.length === 1 && isEinde(verwacht, nieuwste)) {
    return [{ code: 'TURN_BIJ_EINDE', detail: `label=${turns[0]}`, tekst: `De nieuwste overdracht noemt geen volgende beurt (merge door de implementer of een eindfase), maar het label "${turns[0]}" staat er nog.` }];
  }
  return [];
}

/** Als evalueerIssue, plus de tijd (ms) van de nieuwste overdracht: nodig voor de dedup in run(). */
function beoordeel({ labels, issueComments, prs, now, graceMinuten = STANDAARD_GRACE_MINUTEN, laatsteLabelWijziging }) {
  const namen = labelNames({ labels });
  const turns = namen.filter((n) => n.startsWith(TURN_PREFIX)).map((n) => n.slice(TURN_PREFIX.length));
  const nuMs = now instanceof Date ? now.getTime() : (typeof now === 'number' ? now : Date.parse(now) || Date.now());
  const overdrachten = verzamelOverdrachten(issueComments, prs);
  const nieuwste = overdrachten[0] || null;
  const verwacht = nieuwste ? verwachteBeurt(nieuwste.parsed) : null;
  const labelMs = laatsteLabelWijziging ? Date.parse(laatsteLabelWijziging) || 0 : 0;
  const findings = turnBevindingen({ turns, namen, verwacht, nieuwste, nuMs, graceMs: graceMinuten * 60 * 1000, labelMs });
  const sha = shaBevinding(overdrachten, verwacht, prs);
  if (sha) findings.push(sha);
  if (namen.includes(VEROUDERD_STATUS)) {
    findings.push({ code: 'VEROUDERDE_STATUS', detail: 'label=waiting-codex', tekst: 'Het gedeprecieerde label "status: waiting-codex" staat op het issue; het bestaat niet meer.' });
  }
  return { findings, nieuwsteTijd: nieuwste ? nieuwste.tijd : 0 };
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

/** Hash en tijd (ms) van de nieuwste eigen signaalcomment (vertrouwde auteur), of null. */
function laatsteSignaal(issueComments) {
  const eigen = (issueComments || []).filter((c) => isSignaal(c) && isVertrouwd(c));
  if (!eigen.length) return null;
  const nieuwste = eigen.reduce((a, b) => (tijd(b) >= tijd(a) ? b : a));
  const m = HASH_RE.exec(nieuwste.body || '');
  return m ? { hash: m[1], tijd: tijd(nieuwste) } : null;
}

/**
 * Mag de melding achterwege blijven? Alleen als dezelfde bevinding al is gemeld ÉN die melding nieuwer
 * is dan zowel de nieuwste overdracht als de laatste labelwissel. Een hash alleen volstaat niet: een
 * opgeloste en later terugkerende afwijking (zelfde label/verwachting) heeft dezelfde hash en zou anders
 * voorgoed onzichtbaar blijven (P2 uit de review).
 */
function alGemeld(signaal, hash, nieuwsteTijd, laatsteLabelEvent) {
  if (!signaal || signaal.hash !== hash) return false;
  return signaal.tijd > Math.max(nieuwsteTijd, laatsteLabelEvent);
}

// Alle turn:/status:-labelwissels uit het issue-verloop; geeft de nieuwste tijd (ISO) voor alleen de
// turn:-labels (wachttijd) en voor turn:+status: samen (dedup), of null. Pas na een bevinding opgehaald.
async function labelTijden(github, owner, repo, issueNumber) {
  const events = await github.paginate(github.rest.issues.listEvents, {
    owner, repo, issue_number: issueNumber, per_page: 100, headers: API_VERSION_HEADERS,
  });
  const wissels = events.filter((e) => (e.event === 'labeled' || e.event === 'unlabeled') && e.label);
  const nieuwste = (lijst) => lijst.map((e) => e.created_at).sort().pop() || null;
  const naam = (e) => String(e.label.name);
  return {
    turn: nieuwste(wissels.filter((e) => naam(e).startsWith(TURN_PREFIX))),
    alle: nieuwste(wissels.filter((e) => naam(e).startsWith(TURN_PREFIX) || naam(e).startsWith('status: '))),
  };
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
    if (!isVertrouwd(c) || isSignaal(c) || !isOverdracht(parseOverdracht(c.body))) continue;
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
  let uitkomst = beoordeel(invoer);
  let laatsteLabelEvent = 0;
  if (uitkomst.findings.length) {
    // Labelwissels bepalen de wachttijd én of een eerdere melding nog actueel is.
    const tijden = await labelTijden(github, owner, repo, nr);
    laatsteLabelEvent = tijden.alle ? Date.parse(tijden.alle) || 0 : 0;
    uitkomst = beoordeel({ ...invoer, laatsteLabelWijziging: tijden.turn });
  }
  return { findings: uitkomst.findings, issueComments, nieuwsteTijd: uitkomst.nieuwsteTijd, laatsteLabelEvent };
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
      const { findings, issueComments, nieuwsteTijd, laatsteLabelEvent } = await beoordeelIssue(github, owner, repo, ctx, nr, now, graceMinuten);
      if (!findings.length) continue;
      const body = signaalBody(nr, findings);
      if (alGemeld(laatsteSignaal(issueComments), hashVan(findings), nieuwsteTijd, laatsteLabelEvent)) continue;
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
  isOverdracht,
  signaalBody,
  run,
};
