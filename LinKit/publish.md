# Publishing to NuGet

Versions are **not** repeated here. Each package's version lives in its own `.csproj`
(`<Version>`), which is the single source of truth, and the produced files already carry it in
their names. Publishing is therefore version-agnostic: pack, then push everything that was packed.

## Prerequisites

- The version in every `LinKit.*.csproj` has been bumped and the release notes are up to date.
- `NUGET_API_KEY` is set in the environment. Never commit the key to the repository.

## 1. Pack

```powershell
cd LinKit
Remove-Item -Recurse -Force artifacts/packages -ErrorAction SilentlyContinue
dotnet pack LinKit.sln -c Release -o artifacts/packages
```

Pack into an empty directory. A folder that still holds a previous build is how an old version
gets pushed by mistake: the push step sends everything it finds.

Every package is expected to contain `README.md`, XML documentation next to each `lib/<tfm>/`
assembly, a `.snupkg` symbol package, and — for `LinKit.Core`, `LinKit.Data` and `LinKit.Grpc` —
the source generator under `analyzers/dotnet/cs` plus its `buildTransitive` targets. The CI `pack`
job enforces this.

## 2. Push

```powershell
$source = "https://api.nuget.org/v3/index.json"
$key    = $env:NUGET_API_KEY

Get-ChildItem artifacts/packages/*.nupkg |
    Where-Object { $_.Extension -eq ".nupkg" } |
    ForEach-Object {
        dotnet nuget push $_.FullName --source $source --api-key $key --skip-duplicate
    }

Get-ChildItem artifacts/packages/*.snupkg |
    ForEach-Object {
        dotnet nuget push $_.FullName --source $source --api-key $key --skip-duplicate
    }
```

## 3. Automated path

`.github/workflows/publish.yml` runs on a `v*.*.*` tag: it builds, tests, packs into an empty
directory, validates every package, pushes to nuget.org using the `NUGET_API_KEY` repository
secret, and creates a GitHub release. Prefer this over publishing by hand.

The validation is per package, because the twelve packages version independently:

- the file name must match the `id` and `version` inside the package's own nuspec;
- the version must not be older than the one already published on nuget.org (an equal version is
  reported and skipped, so a partially completed release can be re-run);
- the number of produced packages must equal the number of packable projects, so a package that
  silently failed to pack cannot slip through;
- the tag must equal the `LinKit.Core` version, since Core is the package every other one depends
  on and therefore names the release line.

