# Changelog

Every package in this repository is versioned independently, so entries are grouped by package
name and a package that is not mentioned did not ship in that release. The authoritative version
stays the `<Version>` element in each package's `.csproj`; this file records what changed and why.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project
adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## Unreleased

No package-visible API changes yet. The entries below describe how the packages are built and
verified, which is why they matter to consumers.

### Added

- SourceLink metadata (`PublishRepositoryUrl`, `EmbedUntrackedSources`) is embedded in every
  package, so stepping into `LinKit.*` from a project that consumed the `.nupkg` opens the real
  source file instead of decompiled IL. The provider is a `PrivateAssets="all"` build dependency
  and never appears in a consumer's dependency list.
- CI now runs the test suite on `net8.0`, `net9.0` and `net10.0` rather than `net10.0` alone, and
  fails below a line-coverage floor.
- CI runs the provider integration tests against real PostgreSQL, MySQL and SQL Server instances
  instead of skipping them, because a SQLite-only run cannot prove dialect-specific SQL.

### Fixed

- The publish workflow required every package to share one tag version, which a repository of
  independently versioned packages can never satisfy. Each package is now validated against its own
  nuspec id and version, the package count is checked against the number of packable projects, and a
  version that would move a package backwards on nuget.org fails the release instead of being
  pushed.
- Coverage collection silently produced no data because `coverlet.collector` was never referenced
  by the test projects.

## Release history

Package versions are deliberately not repeated here: the [releases page][releases] holds the notes
generated for each published tag, and the `.csproj` files hold the current versions.

[releases]: https://github.com/linhnq0520/LinKit/releases
