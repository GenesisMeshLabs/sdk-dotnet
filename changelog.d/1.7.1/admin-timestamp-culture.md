### Fixed

- **Admin requests work under every culture.** The SDK wrote the signed
  `X-Admin-Timestamp` in the current culture: under a culture with another
  calendar (`th-TH`, `ar-SA`) the year was 2569 or 1448, and under one with
  another time separator (`fi-FI`) the time read `16.27.41`, so the NA refused
  every admin request. The timestamp is written in the invariant culture.
