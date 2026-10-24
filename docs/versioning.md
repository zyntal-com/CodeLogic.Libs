# Versioning

Every CodeLogic package — the framework and all official `CodeLogic.*` libraries —
shares one version line: **`4.8.<patch>`**.

Only the patch number moves. It is never hand-edited.

---

## The scheme

```text
4.8.142+a1b2c3d
│ │ │   └── git short sha  (build metadata, provenance only)
│ │ └────── patch          (CI run number, automatic)
│ └──────── minor          (version.txt, hand-controlled)
└────────── major          (version.txt, hand-controlled)
```

| Component | Source | Changes when |
|-----------|--------|--------------|
| `major.minor` | `version.txt` at the repo root | You edit the file. Never automatic. |
| `patch` | GitHub Actions run number | Every CI run. Monotonic. |
| `+sha` | Git commit | Every commit. Stripped by nuget.org. |

`version.txt` contains exactly one line — `4.8` — in both this repository and the
`CodeLogic` framework repository. Keeping the two files in step is what keeps the
framework and the libraries on a single, legible version line.

All libraries built in one CI run **share the same version**, even when change
detection rebuilds only a subset. A given `4.8.142` therefore means the same
generation across every package that carries it.

---

## Why the patch is the run number

The run number is monotonic and never collides, so no release can accidentally
reuse or move backwards over a published version. It needs no commit, no
write-back to the repository, and no coordination between the two repos.

The trade-off is that numbering has **gaps**. A failed run, or a run of another
workflow, still consumes a number. `4.8.141` may be followed by `4.8.145`. This
is expected and harmless — NuGet orders by value, not by adjacency.

## Why the sha is build metadata

Everything after `+` is SemVer *build metadata*. NuGet.org strips it from the
package version and ignores it entirely for ordering, so `4.8.142+a1b2c3d`
publishes as the package `4.8.142`.

It survives where it is useful: in each assembly's `InformationalVersion`, which
is what `InternalLibraryVersion.Current` reads at runtime. A deployed binary can
always be traced back to the exact commit that produced it.

---

## Assembly identity

`AssemblyVersion` and `FileVersion` are **not** the package version. They are
pinned to `Major.Minor.0.0` — for the 4.8 line, always `4.8.0.0`.

Win32 version fields are four `UInt16`s and cannot carry a `-prerelease` or
`+metadata` suffix, so the full composed string could not go there even if it
were desirable. Pinning has a second, larger benefit: **CLR assembly binding
stays fixed across the whole minor line**, so every `4.8.x` patch loads
interchangeably. Consumers never need a binding redirect to take a patch.

`AssemblyVersion` is derived in `Directory.Build.targets`; the composed
`Version` is assembled in `Directory.Build.props`.

---

## Releasing

Both repositories build from branch pushes. Nothing is written back to the repo.

| Push to | Result |
|---------|--------|
| `main` | Nothing. `main` is the working branch. |
| `prerelease` | Publishes `4.8.<run>-preview+<sha>` to NuGet, tagged as a GitHub pre-release. |
| `release` | Publishes `4.8.<run>+<sha>` to NuGet, tagged as a full GitHub release. |

A local or developer build leaves the patch at `0`, producing a stable `4.8.0`.
That value is never published — it only keeps local builds reproducible.

### Moving to the next minor

Edit `version.txt` in **both** repositories, in the same change:

```text
4.8   →   4.9
```

Then push to `release` as usual. The next run publishes `4.9.<run>`. Nothing
else needs touching — no csproj, no workflow, no documentation constant.

Because this changes assembly binding identity, treat it as a deliberate,
announced change rather than routine maintenance.

> Editing `version.txt` also forces a **full rebuild of every library**, since
> the `major.minor` line has moved. Change detection is bypassed by design.
