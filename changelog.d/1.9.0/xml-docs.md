### Added

- **XML documentation for every public type and member.** The 202 members that had none, most of them response properties, now say what each field means and which request fields the NA ignores. IntelliSense shows it, and the build fails on a public member without a doc comment.

### Changed

- **The constructors of the exception types name their parameters `status`, `code` and `detail`**, like `GenesisMeshException`'s, where they were `s`, `c` and `d`. Only a call that names its arguments changes.
