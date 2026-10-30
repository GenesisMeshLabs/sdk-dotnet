### Fixed

- **A failed read of the NA's key is no longer reused.** Requests that need the NA's key at the same time share one lookup. Before, a failed lookup whose waiting requests had all been cancelled stayed cached, and the next admin request failed with the old error even after the NA had recovered. Each waiting request now gets an exception of its own.
