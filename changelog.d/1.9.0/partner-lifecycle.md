### Added

- **The partner credential lifecycle, live.** `PartnerLifecycleTests` runs a partner controller on a standard key against a live NA that requires policies: the platform team's setup with the privileged key, then create, rotate, retire and revoke through governed actions with an outbox, the denials and refusals, emergency removal under the security team's attestation after the partner's is revoked, and reconciliation. It runs in the `Live contract` job, next to `LiveContractTests`.
- **`scripts/contract_na.py`** has a second operator key, `controller` (standard tier), and `--policy-enforcement required`. `scripts/check_trx.py` takes `--only-test` more than once.
