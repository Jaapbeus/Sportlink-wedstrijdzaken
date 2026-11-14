// Protocol 1 heeft één lokale schrijver. Een label of CI-run kan die niet vervangen.
const config = require('../samenwerking.json');
const fields = {
  protocolVersion: 'Protocol-versie', requestId: 'Request-ID',
  reviewerSession: 'Reviewer-sessie', reviewRoute: 'Reviewroute',
};
function parseProtocolFields(body) {
  const result = {};
  for (const [key, label] of Object.entries(fields)) {
    const lines = String(body || '').split(/\r?\n/);
    const explicit = new RegExp('^[\\s>*_`#\\[\\]-]*' + label + '(?=$|[^a-z0-9_-])', 'i');
    const valid = new RegExp('^[ \t>*_`-]*' + label + '[ *_`]*:[ \t]*(.*?)[ \t]*$', 'i');
    let count = 0;
    for (const line of lines) {
      // Ook een misvormd expliciet veld mag nooit terugvallen op de legacy-route.
      if (!explicit.test(line.normalize('NFKC'))) continue;
      count++;
      const match = line.match(valid);
      if (match) result[key] = match[1];
      else result.protocolInvalid = true;
    }
    if (count > 1) result.protocolInvalid = true;
  }
  return result;
}
function ciBlock(request, settings = config) {
  if (!settings || settings.protocol_version !== 1 || settings.authority !== 'local'
      || !['shadow', 'active'].includes(settings.mode)
      || Object.keys(settings).some(k => !['protocol_version', 'authority', 'mode'].includes(k))) {
    return 'Samenwerkingsconfiguratie ontbreekt of is ongeldig; geen agent of publicatie.';
  }
  if (settings.mode === 'active') {
    return 'Protocol 1 gebruikt de lokale coördinator; CI mag geen agentbeurt of review publiceren.';
  }
  if (request && (request.protocolInvalid || Object.keys(fields).some(k => k in request))) {
    return 'Deze expliciet gerouteerde aanvraag behoort aan de lokale reviewsessie; CI neemt haar niet over.';
  }
  return null; // Alleen het bestaande, niet-gemigreerde pad blijft in shadow actief.
}
module.exports = { parseProtocolFields, ciBlock };
