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
dotnet pack LinKit.sln -c Release -o artifacts/packages
```

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

`.github/workflows/publish.yml` runs on a `v*.*.*` tag: it builds, tests, packs, verifies that the
tag matches the version baked into every package, pushes to nuget.org using the `NUGET_API_KEY`
repository secret, and creates a GitHub release. Prefer this over publishing by hand.

