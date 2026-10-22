### Fixed

- **A data usage intent and policy can be passed back to `DataUsage.Verify`
  as returned.** The typed `DataAccessIntent` and `DataLicensePolicy` left out
  `estimated_volume_bytes` and `max_volume_bytes_per_session` when they were
  null, but the Network Authority signs them as null: it refused the intent
  ("Invalid intent signature", `intent_exceeds_license`). Both fields are now
  written as null.
