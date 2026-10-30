### Added

- **Policy-aware evaluation.** `client.Boundary.Evaluate(new EvaluateRequest { AttestationId = ..., RequestedCapability = ..., Context = ... })` asks for a decision under the active boundary policies (`POST /admin/boundary/evaluate`). It returns the signed decision and its justification proof. Exactly one basis is required: an attestation or an agreement. A context holding secret material, or more metadata than the NA accepts, is refused with `SecretMaterialException` before anything is signed or sent. The quick start uses it instead of a raw admin call.
- **The boundary policy lifecycle.** `client.Policy` provides `Validate`, `Publish`, `Activate` (an older version is the rollback), `Deactivate`, `List`, `Active`, `History` and `Verify`. The typed `BoundaryPolicyIntent` and `BoundaryPolicy` come with their selector and gates. `Publish` refuses an invalid intent with 400 `boundary_policy_invalid`, its issues in the exception's `Details`.
- **`EvidenceMetadata`.** `CheckMetadataOnly` and `SecretMaterial` are the NA's guard on metadata: at most 16 KiB, and no field named like a secret or holding a PEM block, a key or a token.

### Fixed

- **A typed `BoundaryDecision` passed back verifies.** It dropped a null `denial_reason` when written, but the NA signs that field as null, so the decision was refused, for example by `Boundary.Verify`. It is now written as received.
