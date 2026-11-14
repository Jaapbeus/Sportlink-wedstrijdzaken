// Protocol 1 heeft één lokale schrijver. Een label of CI-run kan die niet vervangen.
const config = require('../samenwerking.json');
const fields = {
  protocolVersion: 'Protocol-versie', requestId: 'Request-ID',
  reviewerSession: 'Reviewer-sessie', reviewRoute: 'Reviewroute',
};
function parseProtocolFields(body) {
  const result = {};
  for (const [key, label] of Object.entries(fields)) {
    const matches = [...String(body || '').matchAll(new RegExp('^[ \t>*_`-]*' + label + '[ *_`]*:[ \t]*(.*?)[ \t]*$', 'gim'))];
    if (matches.length) result[key] = matches[0][1];
    if (matches.length > 1) result.protocolInvalid = true;
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
