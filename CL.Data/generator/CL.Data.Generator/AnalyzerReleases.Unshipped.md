; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
CLD001 | CL.Data.Mapping | Warning | Type cannot use generated mapping (falls back to reflection)
CLD002 | CL.Data.Mapping | Error | Constructor cannot be bound
CLD003 | CL.Data.Mapping | Error | Invalid column converter
CLD004 | CL.Data.Mapping | Error | Key column cannot be nullable
CLD005 | CL.Data.Mapping | Error | Composite key needs KeyOrder
CLD006 | CL.Data.Mapping | Error | Column type does not fit its role
CLD007 | CL.Data.Mapping | Error | Role used more than once
CLD008 | CL.Data.Mapping | Error | Unknown property in index
CLD009 | CL.Data.Mapping | Error | Invalid or duplicate column name
CLD010 | CL.Data.Mapping | Error | SET NULL on a non-nullable column
CLD011 | CL.Data.Mapping | Error | Table maps no columns
