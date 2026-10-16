### Security

- **An attestation ID can no longer reach another admin route.**
  `Attestation.Revoke` put the ID into the request path as given, so an ID
  such as `x/../../recognition-policy?` signed and sent
  `POST /admin/recognition-policy`. An ID that is not one path segment
  (empty, `.` or `..`, or holding `/`, `\`, `?`, `#`, `%` or a control
  character) now throws before anything is signed or sent.

### Upgrading

- **`Attestation.Revoke` validates its ID.** It throws
  `ArgumentNullException` for a null ID and `ArgumentException`
  (`ParamName` `attestationId`) for an ID that is not one path segment, where
  it used to send the request. IDs the Network Authority issues are not
  affected.
