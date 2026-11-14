"""Declared local authority; this does not authenticate people or remote credentials."""
import json
import time

from .store import StoreError, append_event, decode, load_request, transaction


FIELDS = ("request_id", "repository", "issue", "pr", "branch", "base_sha", "head_sha", "attempt", "generation")


class Authority:
    def __init__(self, db):
        self.db = db

    def owner(self, session):
        owner = self.db.execute("SELECT value FROM metadata WHERE key='owner_session'").fetchone()
        if not owner or session != owner[0]:
            raise StoreError("Sessie is niet de vastgelegde lokale eigenaar.")

    def grant(self, data):
        self.owner(data.get("grantor_session"))
        required = ("grant_id", "grantee_session", "request_id", "action", "resource_id", "expires_at", "authorization_ref")
        if any(key not in data for key in required):
            raise StoreError("Grant mist verplichte scope-, verval- of autorisatievelden.")
        if any(not isinstance(data[key], str) or not data[key].strip()
               for key in ("grant_id", "grantee_session", "resource_id", "authorization_ref")):
            raise StoreError("Grant-identiteit en autorisatiereferentie mogen niet leeg zijn.")
        if data["action"] != "owner_accepted" or type(data["expires_at"]) is not int or data["expires_at"] <= time.time():
            raise StoreError("Grant vereist geldige actie en toekomstig vervaltijdstip.")
        with transaction(self.db):
            request = load_request(self.db, data["request_id"])
            scope = {key: request[key] for key in FIELDS}
            scope.update(action=data["action"], resource_id=data["resource_id"], authorization_ref=data["authorization_ref"])
            # Caller must acknowledge the full immutable authorization tuple, not just a request id.
            if data.get("scope") != scope:
                raise StoreError("Grant-scope komt niet exact overeen met aanvraag en bevoegdheid.")
            event = append_event(self.db, "authority_granted", request["request_id"], data["grantor_session"], data)
            self.db.execute("INSERT INTO grants VALUES(?,?,?,?,0,?)", (data["grant_id"], data["grantee_session"],
                            json.dumps(scope, sort_keys=True), data["expires_at"], event))
            return {"grant_id": data["grant_id"], "event_id": event}

    def revoke(self, data):
        self.owner(data.get("grantor_session"))
        if not isinstance(data.get("reason"), str) or not data["reason"].strip():
            raise StoreError("Intrekken vereist een reden.")
        with transaction(self.db):
            grant = self.db.execute("SELECT * FROM grants WHERE grant_id=?", (data.get("grant_id"),)).fetchone()
            if not grant:
                raise StoreError("Grant bestaat niet.")
            scope = decode(grant["scope"])
            event = append_event(self.db, "authority_revoked", scope["request_id"], data["grantor_session"], data)
            self.db.execute("UPDATE grants SET revoked=1 WHERE grant_id=?", (data["grant_id"],))
            self.db.execute("UPDATE requests SET state='invalidated' WHERE request_id=? AND state='ready'", (scope["request_id"],))
            return {"grant_id": data["grant_id"], "event_id": event}

    def require(self, identity, request, data, action, resource_id):
        grant = self.db.execute("SELECT * FROM grants WHERE grant_id=?", (data.get("grant_id"),)).fetchone()
        expected = {key: request[key] for key in FIELDS}
        expected.update(action=action, resource_id=resource_id, authorization_ref=data.get("authorization_ref"))
        if (not grant or grant["revoked"] or grant["expires_at"] <= time.time()
                or grant["grantee_session"] != identity.get("session") or decode(grant["scope"]) != expected
                or any(str(identity.get(key)) != str(request[key]) for key in
                       ("request_id", "repository", "pr", "base_sha", "head_sha", "attempt", "generation"))):
            raise StoreError("Owner-acceptatie mist een actuele grant voor de exacte autorisatietuple.")

    def assert_dispositions(self, request):
        rows = self.db.execute(
            "SELECT d.*,f.request_id FROM findings f JOIN finding_dispositions d ON d.disposition_id="
            "(SELECT MAX(d2.disposition_id) FROM finding_dispositions d2 WHERE d2.finding_id=f.finding_id) "
            "WHERE f.repository=? AND f.pr=? AND d.disposition='owner_accepted'",
            (request["repository"], request["pr"]),
        ).fetchall()
        for row in rows:
            original = load_request(self.db, row["request_id"])
            event = self.db.execute("SELECT payload FROM events WHERE event_id=?", (row["event_id"],)).fetchone()
            data = decode(event[0])
            identity = {key: original[key] for key in FIELDS}
            identity["session"] = row["actor_session"]
            self.require(identity, original, data, "owner_accepted", row["finding_id"])
