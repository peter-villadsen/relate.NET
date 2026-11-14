# Relate.Net

[![CI](https://github.com/peter-villadsen/relate.NET/actions/workflows/ci.yml/badge.svg)](https://github.com/peter-villadsen/relate.NET/actions/workflows/ci.yml)

An idiomatic .NET wrapper for [**Relate**](https://github.com/relatedb/relate), the embedded,
in-process property-graph database with a Cypher query language and Neo4j compatibility.

Relate's engine is written in Rust and exposes a stable C ABI (`relate.h`, ABI v2). Relate.Net
binds that C ABI with source-generated P/Invoke and exposes it as ordinary .NET code: `IDisposable`
handles, `async`/`await`, exceptions, LINQ-friendly results and BCL types (`DateOnly`,
`DateTimeOffset`, `IReadOnlyDictionary`, …). The public API is CLS-compliant, so you can use it
from C#, F#, Visual Basic and any other managed language.

```csharp
using Relate.Net;

using var db = RelateDatabase.OpenInMemory();
using var conn = db.Connect();

conn.Query("CREATE (:Person {name: $name, born: $born})",
           new { name = "Ada Lovelace", born = new DateOnly(1815, 12, 10) });

foreach (var record in conn.Query("MATCH (p:Person) RETURN p.name AS name, p.born AS born"))
{
    Console.WriteLine($"{record.Get<string>("name")} was born {record.Get<DateOnly>("born")}");
}
```

## Features

- **Embedded**: no server to run. Open an in-memory database or a directory on disk.
- **Cypher with typed parameters**: pass anonymous objects, dictionaries, POCOs/records, F# maps and
  anonymous records. Values are sent through relate's typed parameter builder, not JSON.
- **Rich value mapping**: nodes, relationships, paths, lists, maps, all Cypher temporal types,
  durations, spatial points and vectors map to .NET types.
- **Transactions** with `using`-scoped rollback and bookmarks.
- **Async**: `QueryAsync`, `BeginTransactionAsync`, `CommitAsync`, `OpenAsync`, with cancellation
  honoured until a statement starts.
- **Thread-safe**: a connection serializes its callers, and separate connections run in parallel.
- **Structured errors**: `RelateException` exposes the Neo4j status `Code`, the `GqlStatus`, the
  failing `Stage` and `IsRetryable`.
- **Result metadata**: query type, mutation counters, notifications, the `EXPLAIN`/`PROFILE` plan
  and engine timings.

## Requirements

- .NET 8.0 or later
- **Windows x64**. Relate.Net bundles the official relate 1.0.1 `relate_ffi.dll` for
  `win-x64` only. On other platforms the first call throws `PlatformNotSupportedException`.

## Installation

Build the package from source (see below) and reference it, or reference the project directly:

```xml
<PackageReference Include="Relate.Net" Version="1.0.0" />
```

The package places `relate_ffi.dll` under `runtimes/win-x64/native`, and the .NET host loads it
automatically.

## Building from source

```powershell
git clone <this repository>
cd relate.NET
dotnet build
dotnet test
dotnet pack src/Relate.Net -c Release -o artifacts
```

The first build downloads `relate-ffi-x86_64-pc-windows-msvc.tar.gz` from the
[relate v1.0.1 GitHub release](https://github.com/relatedb/relate/releases/tag/v1.0.1), verifies
its SHA-256 and extracts `relate_ffi.dll` into `native/1.0.1/win-x64/`. You don't need a Rust
toolchain. To build offline or against your own build of the library:

```powershell
dotnet build -p:RelateNativeLibraryPath=C:\path\to\relate_ffi.dll
```

## Releasing

CI (`.github/workflows/ci.yml`) builds and tests every push and pull request. To publish a version
to nuget.org, push a tag:

```powershell
git tag v1.0.0
git push origin v1.0.0
```

The tag sets the package version (`v1.2.3-beta.1` becomes `1.2.3-beta.1`). After the tests pass,
the `publish` job pushes the `.nupkg` and the `.snupkg` symbol package using
[nuget.org Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing).
GitHub's OIDC token is exchanged for a one-hour API key, so no long-lived key is stored.

One-time setup:

1. On nuget.org, under *your profile → Trusted Publishing*, add a policy with
   Repository Owner `peter-villadsen`, Repository `relate.NET`, Workflow File `ci.yml` and
   Environment `nuget`.
2. The nuget.org profile that owns the policy (`pvillads`) is set as `user:` in the workflow's
   `NuGet/login` step. Change it there if the package moves to another owner.
3. Optional: add required reviewers to the `nuget` environment under *Settings → Environments* to
   approve each release by hand.

## Repository layout

| Path | Contents |
|---|---|
| `src/Relate.Net` | The library |
| `src/Relate.Net/Interop` | P/Invoke declarations, safe handles, parameter writer and value reader |
| `tests/Relate.Net.Tests` | xUnit tests (C#) covering the whole public interface |
| `tests/Relate.Net.FSharpTests` | xUnit tests written in F# |
| `tests/Relate.Net.VisualBasicTests` | xUnit tests written in Visual Basic |
| `build/RelateNative.targets` | Download, verification and packaging of the native library |
| `docs/UserGuide.md` | The user guide |

## Documentation

See the **[User Guide](docs/UserGuide.md)** for the full API tour: opening databases, parameters,
the type-mapping tables, transactions, bookmarks, error handling, async use, threading, and examples
in F# and Visual Basic.

## License

Relate.Net is released under the [MIT License](LICENSE).

It redistributes the unmodified relate native library, which is © The Relate Authors and licensed
under the Apache License 2.0. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Relate.Net is
an independent project and is not affiliated with the Relate authors or with Neo4j, Inc.
