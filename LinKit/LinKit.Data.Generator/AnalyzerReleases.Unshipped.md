; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

| Rule ID    | Category    | Severity | Notes                              |
| ---------- | ----------- | -------- | ---------------------------------- |
| LNKDATA001 | LinKit.Data | Warning  | MultiTenantFilterNotEnforced       |
| LNKDATA002 | LinKit.Data | Warning  | PrimaryKeyFallback                 |
| LNKDATA003 | LinKit.Data | Warning  | SoftDeleteColumnNotMapped          |
| LNKDATA004 | LinKit.Data | Error    | DataContextEntityNotFound          |
| LNKDATA005 | LinKit.Data | Error    | UnsafeSqlIdentifier                |
| LNKDT006   | LinKit.Data | Warning  | DataRecordBaseTypeNotInCompilation |
