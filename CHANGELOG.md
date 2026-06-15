# Changelog

<!-- There is always Unreleased section on the top. Subsections (Add, Changed, Fix, Removed) should be Add as needed. -->
## Unreleased

- Add explicit `Registry` support for test isolation and multiple metric scopes
    - Add `Registry` type and `Registry.create ()` factory
    - Add `Registry.defaultRegistry` (the implicit process-global registry used by all existing functions)
    - Add `*In registry` variants for all `State`, `ResourceAvailability`, and `ServiceStatus` functions (`State.getMetricIn`, `State.observeHistogramSetValueIn`, `ResourceAvailability.enableIn`, `ServiceStatus.markAsEnabledIn`, etc.)
    - Existing functions without a registry argument are unchanged and delegate to the default registry
- Add histogram metrics support
    - Add Prometheus histogram output formatting (`_bucket`, `_sum`, `_count`) with `Histogram` domain types and formatter.
    - Add histogram state APIs for observation and retrieval: `State.observeHistogramSetValue`, `State.getHistogram`, and `State.getHistograms`.
    - Add simple histogram data set creation (`SimpleHistogramDataSet`, `Histogram.createWithSimpleDataSets`) so observations can be converted to histogram buckets inside the library.
    - Add histogram bucket configuration support via `HistogramBuckets`
 
## 12.0.0 - 2026-01-28
- [**BC**] Use net10.0

## 11.0.0 - 2025-11-28
- Move repository

## 10.1.1 - 2025-10-16
- Fix formatting the `Float` value

## 10.1.0 - 2025-03-17
- Update dependencies

## 10.0.0 - 2025-03-13
- [**BC**] Use net9.0

## 9.0.0 - 2024-01-09
- [**BC**] Use net8.0
- Fix package metadata

## 8.0.0 - 2023-09-10
- [**BC**] Use `Alma` namespace

## 7.0.0 - 2023-08-10
- Add `Audience.PrivacyComponents` case
- [**BC**] Use net 7.0

## 6.1.0 - 2022-01-19
- Normalize Prometheus names

## 6.0.0 - 2022-01-05
- [**BC**] Use net6.0
- [**BC**] Remove `Webserver`

## 5.1.0 - 2021-02-15
- Update dependencies

## 5.0.0 - 2020-11-23
- [**BC**] Use .netcore 5.0

## 4.0.0 - 2020-11-23
- [**BC**] Use .netcore 3.1
- Update dependencies
- [**BC**] Use `Lmc.Metrics` namespace

## 3.4.0 - 2020-02-11
- Change git host
- Add `AssemblyInfo.fs`
- Add `[<RequireQualifiedAccess>]` to `WebServer` module
- Set `WebServer.statePart` function public

## 3.3.0 - 2019-10-25
- Add `[<RequireQualifiedAccess>]` to modules
- Allow to run `WebServer` with `runStateAsync` with additional web server settings.

## 3.2.0 - 2019-08-07
- Add `State` functions:
    - `setMetricSetValue`
    - `setMetricValue`

## 3.1.0 - 2019-06-26
- Use lint

## 3.0.0 - 2019-06-07
- [**BC**] Extend `ResourceAvailability`
    - Add `Common` - common resource availability
    - Add `Service` - resource availability for service (_identified by `Instance`_)
    - Add `MultiTenantService` - resource availability for multi-tenant service (_identified by `Box`_)

## 2.0.0 - 2019-05-17
- Add `createServiceDataSetKey` function to easy create a `DataSetKey` with `Instance`
- Add `ResourceAvailability` and `Audience` metric module
- Add `ServiceStatus` metric module
- Refactor `State` module
    - [**BC**] Change parameters of `incrementMetricSetValue` function
    - Add `enableStatusMetric` function to enable status metric (set value to `1`)
    - Add `disableStatusMetric` function to disable status metric (set value to `0`)

## 1.0.0 - 2019-01-31
- Initial implementation
