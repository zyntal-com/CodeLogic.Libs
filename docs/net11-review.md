# .NET 11 review — 2026-10-31

Reviewed on `feature/cl-data`, using SDK `11.0.100-rc.3.26522.148` and the sibling CodeLogic source.
This is a targeted code review plus broad test execution, not a proof that every library is defect-free.

## Corrections

- `CL.Data` decimal materialization: format a float before widening it. Widening `0.1f` to double
  exposes binary precision that the shortest float representation intentionally hides. Both float and
  double now retain their own round-trip precision. Regression tests exercise nullable and non-nullable reads.
- Converter registration/removal: use atomic version increments and volatile reads. Plain `Version++`
  could lose increments when registrations overlap. This fixes the public version contract; no internal
  consumer of that version was found during this review.
- Converter removal: unwrap nullable types just as lookup does. Previously lookup by `MyId?` could find
  a registration that removal by `MyId?` could not remove.
- Query plan cache: publish immutable variant arrays with volatile reads/writes. Writers already used
  a lock, but readers do not acquire it; explicit publication provides the matching memory ordering.
- N+1 query counting: use a dedicated `System.Threading.Lock` instead of locking the state object.
  No throughput improvement is claimed without measurements.
- Corrected the stale contributor note that CodeLogic publication was still blocking CL.Data.

## Existing modernization

The root and test build properties agree on `net11.0` and C# 15. The code already uses CodeLogic union
results, closed error hierarchies, frozen lookup dictionaries, generated mapping, static abstract value
converters, `System.Threading.Lock`, and built-in Zstandard. The collection translator handles both
span `Contains` overloads, including nullable-array calls. Decimal reads already account for the .NET 11
double conversion change; the correction above extends that intention consistently to floats.

These choices fit the framework's current APIs. Further syntax-only rewrites would not demonstrate an
optimization. Compare future changes against the existing allocation and database benchmark budgets.

## Validation and limits

- Executed the repository's test projects, including the generator and all non-database library suites.
- Ran MySQL, PostgreSQL and SQL Server conformance against the existing dedicated `cl-data-*` containers,
  as well as SQLite, with generated and reflection mapping. Optional features still have explicit skips.
- Storage's 902 unit tests also passed against the restored published CodeLogic preview, using
  `-p:CodeLogicFromNuGet=true`. Switching this property requires restore; old source-reference assets
  cannot validate a NuGet build with `--no-restore`.
- Published and executed the Windows x64 Native AOT smoke sample successfully with reflection mapping off.
- CL.Data unit tests pass with a deliberate CLD001 diagnostic on the private reflection-only test model.
  The restored Storage test build also reports existing test warnings: obsolete TLS 1.1 usage,
  a nullable dereference, and potentially partial stream reads. Test success is not a zero-warning claim.
- Shared strong-name props and targets have identical SHA-256 hashes in both repositories. This review
  does not verify a private-key release signature.
- The sibling CodeLogic checkout is `feature/result-throw-if-failure`, not `main`. Its source builds
  successfully as the library dependency; its entire implementation was not independently audited here.
- Replica-pair tests, external SMTP/Git/network services, Storage live integration, benchmark budgets,
  and release packing/signing were not validated in this pass. No performance gains are inferred from
  ordinary test timings. No merge, promotion, publication or documentation deployment was performed.

## Documentation

Library and site documentation already describe .NET 11 / CodeLogic 5. The public site intentionally
remains on 4.9 while deployment is disabled. The historical publication blocker in `CL.Data/FINDINGS.md`
is explicitly marked resolved; retain it as history. Converter registry behavior is now documented in
both CL.Data's README and the site source.

Reference: [Microsoft's C# 15 overview](https://learn.microsoft.com/dotnet/csharp/whats-new/).
