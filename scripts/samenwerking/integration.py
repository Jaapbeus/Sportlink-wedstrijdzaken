"""Bounded tree evidence plus explicit independent interaction assessment."""
import hashlib
import importlib.util
import json
from pathlib import Path
import subprocess

from .authority import Authority
from .store import StoreError, append_event, decode, load_request


def helper():
    path = Path(__file__).resolve().parents[1] / 'dev/integration-evidence.py'
    spec = importlib.util.spec_from_file_location('integration_evidence', path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


def digest(value):
    return hashlib.sha256(json.dumps(value, sort_keys=True).encode()).hexdigest()


class Integration:
    def __init__(self, db):
        self.db = db

    def prepare(self, data):
        if not isinstance(data.get('source_request_id'), str) or not data.get('repository_path'):
            raise StoreError('Integratie vereist een eerdere schone review en repository_path.')
        old = load_request(self.db, data['source_request_id'])
        if (old['state'] != 'ready' or old['repository'] != data['repository']
                or old['pr'] != data['pr'] or old['implementer_session'] != data['implementer_session']):
            raise StoreError('Integratie vereist een actuele eerdere schone review voor dezelfde bijdrage.')
        if old['base_sha'] == data['base_sha'] and old['head_sha'] == data['head_sha']:
            raise StoreError('Integratie vereist een gewijzigde base/head-tuple.')
        latest = self.db.execute('SELECT request_id FROM requests WHERE repository=? AND pr=? ORDER BY created_at DESC LIMIT 1',
                                 (old['repository'], old['pr'])).fetchone()
        if latest['request_id'] != old['request_id']:
            raise StoreError('Integratiebron is ingehaald door een nieuwere reviewaanvraag.')
        Authority(self.db).assert_dispositions(old)
        self.assert_valid(old)
        path = Path(data['repository_path'])
        if not path.is_absolute() or not path.is_dir():
            raise StoreError('repository_path moet een absolute checkout zijn.')
        try:
            result = helper().evidence(path, old['base_sha'], old['head_sha'], data['base_sha'], data['head_sha'])
        except (ValueError, KeyError, OSError, UnicodeError, subprocess.SubprocessError) as exc:
            raise StoreError('Integratiebewijs onvolledig; inhoudelijke review vereist.') from exc
        if result['classification'] not in ('exacte-integratietree', 'uitsluitend-gesynchroniseerde-versievelden'):
            raise StoreError('Gewijzigde, onzekere of handmatige integratie vereist inhoudelijke review.')
        return {'source_request_id': old['request_id'], 'evidence': result, 'repository_path': str(path)}

    def install(self, request_id, prepared):
        self.db.execute('INSERT INTO integration_evidence VALUES(?,?,?,NULL)',
                        (request_id, prepared['source_request_id'], json.dumps(prepared, sort_keys=True)))

    def attest(self, request, identity, data):
        row = self.db.execute('SELECT * FROM integration_evidence WHERE request_id=?', (request['request_id'],)).fetchone()
        if not row:
            raise StoreError('Geen gevalideerd integratiebewijs voor deze aanvraag.')
        prepared = decode(row['evidence'])
        evidence = prepared['evidence']
        if (data.get('evidence_sha256') != digest(evidence) or data.get('inherited_changes_reviewed') is not True
                or not isinstance(data.get('assessment'), str) or not data['assessment'].strip()
                or data.get('refs') != {key: evidence[key] for key in
                                       ('old_base', 'reviewed_head', 'new_base', 'candidate_head', 'expected_tree', 'candidate_tree')}):
            raise StoreError('Interactieattest mist exacte oude/nieuwe SHAs, trees, bewijs of onafhankelijke beoordeling.')
        event = append_event(self.db, 'integration_attested', request['request_id'], identity['session'],
                             data, generation=request['generation'])
        self.db.execute('UPDATE integration_evidence SET attestation_event=? WHERE request_id=?',
                        (event, request['request_id']))
        return {'event_id': event, 'evidence_sha256': digest(evidence)}

    def assert_valid(self, request):
        if request['kind'] != 'integration':
            return
        row = self.db.execute('SELECT * FROM integration_evidence WHERE request_id=?', (request['request_id'],)).fetchone()
        source = load_request(self.db, row['source_request_id']) if row else None
        event = self.db.execute('SELECT * FROM events WHERE event_id=?', (row['attestation_event'],)).fetchone() if row else None
        if (not source or source['state'] != 'ready' or not event
                or event['actor'] != request['reviewer_session'] or event['generation'] != request['generation']):
            raise StoreError('Integratie vereist geldige bronreview en actueel onafhankelijk interactieattest.')
        Authority(self.db).assert_dispositions(source)
        self.assert_valid(source)
