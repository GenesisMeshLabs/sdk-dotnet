### Added

- **Store anchors offline** (protocol 1.2.0, as the reference's `verify_store_anchors` and `check_events_against_anchors` and the TypeScript SDK's `anchors.ts`):
  - `StoreAnchor`, `OfflineVerifier.ParseAnchors` (an array of anchors, or a page of the anchors route; an anchor that is not well formed is refused), `AnchorCanonical` and `AnchorDigest`;
  - `OfflineVerifier.VerifyStoreAnchors` (`AnchorVerifyOptions`): each signature (`anchor_invalid_signature`; under a key history, `na_key_not_valid_at_time`), the sovereign (`anchor_sovereign_mismatch`), the anchor chain (`anchor_sequence_gap`, `anchor_chain_break`) and heads that only move forward (`anchor_not_increasing`);
  - `OfflineVerifier.CheckEventsAgainstAnchors` (`AnchorCheckOptions`): a run of entries tied to the anchors at its start (`export_not_linked_to_anchors`) and its end (`export_ends_before_anchor`), each anchor inside it naming its entry's digest (`anchor_mismatch`, `anchor_entry_missing`), summed up in `EvidenceStoreVerification.Anchors`;
  - `OfflineVerifier.StartingCheckpoint`: the retention checkpoint that explains where a run starts.
- **Conformance:** the `audit` suite's 2 `verify_anchors` vectors run.

### Differs from TypeScript

- `VerifyStoreAnchors` needs the NA's keys or key history, or `SkipSignatures` to check no signature; TypeScript skips the signatures when its keys are null.
- `StartingCheckpoint` gives the checkpoint as received, the JSON `EvidenceVerifyOptions.Checkpoint` takes.
- `CheckEventsAgainstAnchors` refuses an anchor that is not well formed, as `VerifyStoreAnchors` does; TypeScript checks none there.
- `ParseAnchors` reads JSON (an array, or a page of the anchors route); TypeScript's `parseAnchors` checks items already parsed.
