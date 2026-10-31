"""A live, disposable Network Authority for the SDK's live tests.

Needs the genesis-mesh core installed (CI installs the core branch of the same
name, or main). Prints one JSON line with the connection details and the
inputs the tests need, then serves until stopped. Real routes, signatures and
SQLite store; only the rate limiter is off. Prerequisites the SDK has no call
for (a recognition treaty for "sovereign-b", a signed justification proof) are
set up here.

Two operator keys: "ops" (privileged) and "controller" (standard), as a
platform team and a controller hold them. ``--policy-enforcement required``
starts the NA as a governed deployment runs it: every evaluation needs an
active policy, and the legacy decide route is refused.
"""
import argparse
import json
import signal
import sys
import tempfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

from werkzeug.serving import make_server

from genesis_mesh.crypto import generate_keypair, sign_model
from genesis_mesh.crypto.admin_auth import sign_admin_request
from genesis_mesh.models import GenesisBlock, NetworkAuthority, PolicyManifestRef
from genesis_mesh.models.justification import GateTrace, GateTraceEntry, JustificationProof
from genesis_mesh.na_service.server import NetworkAuthorityService


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--policy-enforcement", choices=("optional", "required"), default="optional")
    options = parser.parse_args()
    root = generate_keypair()
    operator = generate_keypair()
    controller = generate_keypair()
    now = datetime.now(timezone.utc)
    genesis = GenesisBlock(
        network_name="contract-na", network_version="v0.1", root_public_key=root.public_key_b64,
        network_authority=NetworkAuthority(public_key=root.public_key_b64, valid_from=now,
                                           valid_to=now + timedelta(days=1)),
        policy_manifest=PolicyManifestRef(hash="sha256:test", url=None),
    )
    genesis.signatures.append(sign_model(genesis, root.private_key, "root"))
    tmp = tempfile.mkdtemp(prefix="gm-contract-")
    service = NetworkAuthorityService(
        genesis_block=genesis, na_private_key=root.private_key, key_id="na-contract",
        db_path=str(Path(tmp) / "na.db"),
        operator_public_keys={"ops": operator.public_key_b64, "controller": controller.public_key_b64},
        operator_key_tiers={"ops": "privileged", "controller": "standard"}, evidence_store="on",
        boundary_policy_enforcement=options.policy_enforcement,
    )
    service.rate_limiter.allow = lambda *args, **kwargs: True

    client = service.app.test_client()
    body = {"subject_sovereign_id": "sovereign-b", "subject_public_keys": [root.public_key_b64],
            "scope": {"allowed_roles": ["role:client"]}, "validity_hours": 24}
    headers = sign_admin_request(operator.private_key, "ops", method="POST", path="/admin/recognition-treaties",
                                 audience=root.public_key_b64, body=body)
    response = client.post("/admin/recognition-treaties", json=body, headers=headers)
    assert response.status_code == 201, response.get_json()

    trace = GateTrace(
        trace_id="tr-contract-1", decision_id="dec-contract-1", agreement_id="agr-contract-1",
        operator_sovereign_id="contract-na", traced_at=now,
        entries=[GateTraceEntry(gate_name="capability_check", gate_type="CapabilityGate", evaluated_at=now,
                                inputs={"requested_capability": "read"}, result=True, reason="ok")],
        final_authorized=True,
    )
    proof = JustificationProof(proof_id="jp-contract-1", decision_id="dec-contract-1", trace=trace,
                               proof_issued_at=now, issuer_sovereign_id="contract-na")
    proof.signature = sign_model(proof, root.private_key, "na-contract")

    server = make_server("127.0.0.1", 0, service.app, threaded=True)
    signal.signal(signal.SIGTERM, lambda *_: sys.exit(0))
    print(json.dumps({
        "baseUrl": f"http://127.0.0.1:{server.server_port}",
        "seed": operator.private_key_b64, "keyId": "ops", "naPublicKey": root.public_key_b64,
        "controllerSeed": controller.private_key_b64, "controllerKeyId": "controller",
        "networkName": "contract-na", "justificationProof": json.loads(proof.model_dump_json()),
    }), flush=True)
    try:
        server.serve_forever()
    finally:
        server.server_close()


if __name__ == "__main__":
    main()
