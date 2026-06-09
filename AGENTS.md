# AGENTS.md — Alma.Metrics

## Project Purpose

F# library for creating and formatting application metrics in Prometheus exposition format. Supports simple metrics, labeled data sets, histograms, summaries, and service status/resource availability tracking. Published as NuGet package `Alma.Metrics`.

## Tech Stack

- **Language:** F# (.NET 10)
- **Framework:** .NET SDK library
- **Package management:** Paket
- **Build system:** FAKE (F# Make) via `build.sh`
- **Linting:** fsharplint
- **CI/CD:** GitHub Actions
- **Key dependencies:** `FSharp.Core ~> 10.0`, `Feather.ErrorHandling ~> 2.0`, `Alma.ServiceIdentification ~> 11.0`

## Commands

```bash
# Install dependencies
dotnet tool restore && dotnet paket install

# Build
./build.sh build

# Run tests
./build.sh -t tests

# Lint
dotnet fsharplint lint Metrics.fsproj
```

## Project Structure

```
fmetrics/
├── Metrics.fsproj              # Main project (PackageId: Alma.Metrics, v12.0.0)
├── AssemblyInfo.fs             # Auto-generated
├── src/
│   ├── Audience.fs             # Audience/consumer types
│   ├── Prometheus.fs           # Prometheus format metrics (simple, labeled, histogram, summary)
│   ├── State.fs                # Metric state management
│   ├── ResourceAvailability.fs # Resource availability tracking
│   └── ServiceStatus.fs        # Service status metrics
├── example/
│   └── example.fsproj          # Example usage project
├── build/
│   ├── build.fsproj
│   └── ...
├── build.sh
├── paket.dependencies
├── paket.references            # FSharp.Core, Feather.ErrorHandling, Alma.ServiceIdentification
├── global.json                 # .NET SDK 10.0.0
├── fsharplint.json
├── CHANGELOG.md
└── .github/workflows/
    ├── tests.yaml
    ├── pr-check.yaml
    └── publish.yaml
```

## Architecture

Pure library for Prometheus-compatible metrics:

- **Metric types:** Counter, Gauge, Histogram, Summary, Untyped
- **Value types:** Int, Float, Infinite, NegativeInfinite, NotANumber
- **SimpleDataSet** — metric with labels (key-value pairs)
- **SimpleHistogramDataSet / HistogramDataSet** — histogram buckets, cumulative counts, sum and count per label set
- **HistogramBuckets** — normalized custom/default bucket definitions (`+Inf` bucket is appended automatically)
- **Metric.format** — formats a metric to Prometheus exposition format text
- **Metric.createSimple** / **Metric.createWithSimpleDataSets** — constructor functions
- **Histogram.format** / **Histogram.createWithSimpleDataSets** — histogram constructor and formatter
- **ServiceStatus** / **ResourceAvailability** — higher-level domain metrics

State module also supports histogram observation and retrieval via:

- **State.observeHistogramSetValue** — records a histogram observation for a metric and dataset key
- **State.getHistogram** / **State.getHistograms** — reads histogram state as `Histogram` values

Uses `result {}` computation expressions for validation (metric names, label values).

## Build System (FAKE)

Standard library target chain: `Clean → AssemblyInfo → Build → Lint → Tests → Release → Publish`

## CI/CD

- **tests.yaml** — runs on PRs and nightly
- **pr-check.yaml** — blocks fixup commits, runs ShellCheck
- **publish.yaml** — publishes to NuGet on semver tags

## Release Process

1. Increment `<Version>` in `Metrics.fsproj`
2. Update `CHANGELOG.md`
3. Commit, tag with version, push

## Conventions

- `result {}` CE for error handling / validation
- Metric names follow Prometheus naming conventions
- `Alma.ServiceIdentification` for service identity integration
- `example/` folder contains usage examples — keep up to date

## Pitfalls

- **No tests** — no test project exists currently
- **No Docker** — pure library
- **Prometheus format compliance** — output must conform to [Prometheus exposition format](https://prometheus.io/docs/instrumenting/exposition_formats/)
- **Paket, not NuGet CLI** — use `dotnet paket install`
- **Compile order** — 5 source files must be in correct dependency order in `.fsproj`
