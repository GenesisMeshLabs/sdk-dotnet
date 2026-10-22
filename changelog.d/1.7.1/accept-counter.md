### Fixed

- **A counter-offer can be accepted.** `Agreement.Accept` sends
  `{"offer": ...}`, which the Network Authority refuses for a counter-offer
  (422 `accept_rejected`), so the SDK had no way to accept one. The new
  `Agreement.AcceptCounter(counter, originalOffer)` sends
  `{"counter": ..., "original_offer": ...}`, the form the NA accepts.
