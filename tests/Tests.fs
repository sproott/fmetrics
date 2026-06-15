open System
open Expecto
open Alma.Metrics
open Alma.ServiceIdentification

let private uniqueMetricName prefix =
    sprintf "%s_%s" prefix (Guid.NewGuid().ToString("N"))
    |> MetricName.createOrFail

let private instance = {
    Domain = Domain "consents"
    Context = Context "example"
    Purpose = Purpose "common"
    Version = Version "stable"
}

let private box =
    Box.ofInstance instance (Zone "data") (Bucket "lmc")

let private resultOrFail = function
    | Ok value -> value
    | Error error -> failtestf "Expected Ok, got %A" error

let prometheusPureTests =
    testList "Prometheus pure" [
        testCase "metric name validates empty and too short" <| fun _ ->
            match MetricName.create "" with
            | Error (MetricNameError.NameError EmptyName) -> ()
            | actual -> failtestf "Expected EmptyName, got %A" actual

            match MetricName.create "x" with
            | Error (MetricNameError.NameError (TooShortName 2)) -> ()
            | actual -> failtestf "Expected TooShortName 2, got %A" actual

        testCase "label name validates too short" <| fun _ ->
            match Label.create ("x", "value") with
            | Error (LabelNameError.NameError (TooShortName 2)) -> ()
            | actual -> failtestf "Expected TooShortName 2, got %A" actual

        testCase "metric name normalizes dash and spaces" <| fun _ ->
            let name = MetricName.createOrFail "my-metric value"
            Expect.equal (MetricName.value name) "my_metric_value" "Metric name should normalize"

        testCase "label create validates names" <| fun _ ->
            match Label.create ("", "v") with
            | Error (LabelNameError.NameError EmptyName) -> ()
            | actual -> failtestf "Expected label EmptyName, got %A" actual

        testCase "metric value add combinations" <| fun _ ->
            Expect.equal (Int 2 + Int 3) (Int 5) "Int + Int"
            Expect.equal (Float 2.5 + Float 0.5) (Float 3.0) "Float + Float"
            Expect.equal (Int 2 + Float 0.5) (Float 2.5) "Int + Float"
            Expect.equal (Float 2.5 + Int 2) (Float 4.5) "Float + Int"
            Expect.equal (NotANumber + Int 7) (Int 7) "NaN + value"
            Expect.equal (Int 7 + NotANumber) (Int 7) "value + NaN"
            Expect.equal (Infinite + Int 1) Infinite "Inf + value"
            Expect.equal (NegativeInfinite + Int 1) NegativeInfinite "-Inf + value"

            Expect.throws
                (fun _ -> ignore (Infinite + NegativeInfinite))
                "Inf + -Inf must throw"

            Expect.throws
                (fun _ -> ignore (NegativeInfinite + Infinite))
                "-Inf + Inf must throw"

        testCase "histogram buckets normalize and append +Inf" <| fun _ ->
            let buckets =
                HistogramBuckets.create [ 1.0; 0.5; 1.0; Double.NaN; Double.PositiveInfinity; 0.25 ]
                |> HistogramBuckets.value

            Expect.equal buckets [ 0.25; 0.5; 1.0; Double.PositiveInfinity ] "Buckets should normalize"

        testCase "dataset key injects svc labels" <| fun _ ->
            match DataSetKey.createFromInstance instance [ ("custom", "v") ] with
            | Error e -> failtestf "Expected dataset key, got %A" e
            | Ok key ->
                let metric =
                    Metric.createMetric
                        (uniqueMetricName "dataset_key")
                        None
                        None
                        [ DataSet.createFromTuple (key, Int 1) ]

                let formatted = Metric.format metric
                Expect.stringContains formatted "svc_domain=\"consents\"" "Missing svc_domain"
                Expect.stringContains formatted "svc_context=\"example\"" "Missing svc_context"
                Expect.stringContains formatted "svc_purpose=\"common\"" "Missing svc_purpose"
                Expect.stringContains formatted "svc_version=\"stable\"" "Missing svc_version"
                Expect.stringContains formatted "custom=\"v\"" "Missing custom"

        testCase "histogram dataset computes cumulative counts" <| fun _ ->
            let simple =
                SimpleHistogramDataSet.create
                    []
                    (HistogramBuckets.create [ 1.0; 2.0 ])
                    [ 0.5; 1.5; 3.0 ]

            match HistogramDataSet.createFromSimple simple with
            | Error e -> failtestf "Expected histogram dataset, got %A" e
            | Ok dataSet ->
                let counts = dataSet.Buckets |> List.map (fun b -> b.CumulativeCount)
                Expect.equal counts [ 1; 2; 3 ] "Cumulative counts mismatch"
                Expect.equal dataSet.Sum 5.0 "Sum mismatch"
                Expect.equal dataSet.Count 3 "Count mismatch"

        testCase "metric format includes help type and value" <| fun _ ->
            let metric =
                Metric.createSingle "requests_total" (Int 3) (Some "Request count") (Some MetricType.Counter)
                |> resultOrFail

            let formatted = Metric.format metric
            let expected = "# HELP requests_total Request count\n# TYPE requests_total counter\nrequests_total 3\n\n"

            Expect.equal formatted expected "Metric format mismatch"

        testCase "histogram format has buckets sum count" <| fun _ ->
            let simple =
                SimpleHistogramDataSet.create
                    []
                    (HistogramBuckets.create [ 1.0 ])
                    [ 0.5; 2.0 ]

            let histogram =
                Histogram.createWithSimpleDataSets "request_duration_seconds" (Some "Duration") [ simple ]
                |> resultOrFail

            let formatted = Histogram.format histogram

            Expect.stringContains formatted "# HELP request_duration_seconds Duration" "Missing HELP"
            Expect.stringContains formatted "# TYPE request_duration_seconds histogram" "Missing TYPE"
            Expect.stringContains formatted "request_duration_seconds_bucket{le=\"1\"} 1" "Missing bucket 1"
            Expect.stringContains formatted "request_duration_seconds_bucket{le=\"+Inf\"} 2" "Missing +Inf bucket"
            Expect.stringContains formatted "request_duration_seconds_sum 2.5" "Missing sum"
            Expect.stringContains formatted "request_duration_seconds_count 2" "Missing count"

        testCase "histogram format with labels" <| fun _ ->
            let simple =
                SimpleHistogramDataSet.create
                    [ ("method", "GET") ]
                    (HistogramBuckets.create [ 0.5 ])
                    [ 0.1; 1.0 ]

            let histogram =
                Histogram.createWithSimpleDataSets "http_duration_seconds" None [ simple ]
                |> resultOrFail

            let formatted = Histogram.format histogram
            Expect.stringContains formatted "method=\"GET\"" "Missing label in bucket"
            Expect.stringContains formatted "http_duration_seconds_bucket" "Missing bucket metric name"
            Expect.stringContains formatted "http_duration_seconds_sum" "Missing sum metric name"
            Expect.stringContains formatted "http_duration_seconds_count" "Missing count metric name"

        testCase "histogram format with multiple datasets" <| fun _ ->
            let simpleA =
                SimpleHistogramDataSet.create
                    [ ("method", "GET") ]
                    (HistogramBuckets.create [ 1.0 ])
                    [ 0.5 ]

            let simpleB =
                SimpleHistogramDataSet.create
                    [ ("method", "POST") ]
                    (HistogramBuckets.create [ 1.0 ])
                    [ 0.8 ]

            let histogram =
                Histogram.createWithSimpleDataSets "multi_duration_seconds" None [ simpleA; simpleB ]
                |> resultOrFail

            let formatted = Histogram.format histogram
            Expect.stringContains formatted "method=\"GET\"" "Missing GET label"
            Expect.stringContains formatted "method=\"POST\"" "Missing POST label"

        testCase "metric format empty datasets" <| fun _ ->
            let metric =
                Metric.createMetric
                    (uniqueMetricName "empty_datasets")
                    None
                    None
                    []

            let formatted = Metric.format metric
            // No data set lines, just trailing newline
            Expect.stringEnds formatted "\n" "Format should end with newline"

        testCase "metric format with timestamp" <| fun _ ->
            let ts = DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            let dataSet =
                SimpleDataSet.createWithTimestamp [] (Int 42) (Some ts)
                |> DataSet.createFromSimple
                |> resultOrFail

            let metric =
                Metric.createMetric
                    (uniqueMetricName "ts_metric")
                    None
                    None
                    [ dataSet ]

            let formatted = Metric.format metric
            // Unix timestamp for 2024-01-01 00:00:00 UTC = 1704067200
            Expect.stringContains formatted "1704067200" "Timestamp should appear in formatted output"

        testCase "float format uses invariant culture" <| fun _ ->
            // Verifies decimal point, not comma, regardless of system locale
            let metric =
                Metric.createSingle "float_culture_check" (Float 2.5) None None
                |> resultOrFail

            let formatted = Metric.format metric
            Expect.stringContains formatted "2.5" "Float must use dot decimal separator"
            Expect.isFalse (formatted.Contains "2,5") "Float must not use comma decimal separator"

        testCase "dataset key rejects invalid label name" <| fun _ ->
            match DataSetKey.createFromInstance instance [ ("", "value") ] with
            | Error (LabelError (LabelNameError.NameError EmptyName)) -> ()
            | actual -> failtestf "Expected LabelError EmptyName, got %A" actual
    ]

let statefulTests =
    testList "Stateful" [
        testCase "state set/get metric value" <| fun _ ->
            let reg = Registry.create ()
            let name = uniqueMetricName "state_set_get"
            State.setMetricValueIn reg (Int 10) name

            match State.getMetricIn reg name with
            | None -> failtest "Metric should exist"
            | Some metric ->
                Expect.equal (Metric.singleValue metric) (Some (Int 10)) "Single value mismatch"

        testCase "state increment metric value" <| fun _ ->
            let reg = Registry.create ()
            let name = uniqueMetricName "state_increment"
            State.incrementMetricValueIn reg (Int 2) name |> ignore
            State.incrementMetricValueIn reg (Int 3) name |> ignore

            match State.getMetricIn reg name with
            | None -> failtest "Metric should exist"
            | Some metric ->
                Expect.equal (Metric.singleValue metric) (Some (Int 5)) "Incremented value mismatch"

        testCase "state histogram observe then read" <| fun _ ->
            let reg = Registry.create ()
            let name = uniqueMetricName "state_hist"
            State.observeHistogramSetValueIn reg (HistogramBuckets.create [ 1.0 ]) 0.5 name DataSetKey.empty
            State.observeHistogramSetValueIn reg (HistogramBuckets.create [ 1.0 ]) 2.0 name DataSetKey.empty

            match State.getHistogramIn reg name with
            | None -> failtest "Histogram should exist"
            | Some histogram ->
                let dataSet = histogram.DataSets |> List.head
                let counts = dataSet.Buckets |> List.map (fun b -> b.CumulativeCount)
                Expect.equal counts [ 1; 2 ] "Histogram counts mismatch"
                Expect.equal dataSet.Sum 2.5 "Histogram sum mismatch"
                Expect.equal dataSet.Count 2 "Histogram count mismatch"

        testCase "state histogram bounds mismatch throws" <| fun _ ->
            let reg = Registry.create ()
            let name = uniqueMetricName "state_hist_bounds"
            State.observeHistogramSetValueIn reg (HistogramBuckets.create [ 1.0 ]) 0.5 name DataSetKey.empty

            Expect.throws
                (fun _ -> State.observeHistogramSetValueIn reg (HistogramBuckets.create [ 2.0 ]) 1.5 name DataSetKey.empty)
                "Changing histogram bounds should throw"

        testCase "service status enable and disable" <| fun _ ->
            let reg = Registry.create ()

            let markEnabled =
                ServiceStatus.markAsEnabledIn reg instance Audience.Sys
                |> resultOrFail

            let markDisabled =
                ServiceStatus.markAsDisabledIn reg instance Audience.Sys
                |> resultOrFail

            let (ServiceStatus.MarkAsEnabled enableFn) = markEnabled
            let (ServiceStatus.MarkAsDisabled disableFn) = markDisabled

            enableFn ()
            let enabledValue = ServiceStatus.getFormattedValueIn reg ()
            Expect.stringContains enabledValue "service_status" "Missing service_status metric"
            Expect.stringContains enabledValue " 1" "Service should be enabled"

            disableFn ()
            let disabledValue = ServiceStatus.getFormattedValueIn reg ()
            Expect.stringContains disabledValue " 0" "Service should be disabled"

        testCase "resource availability supports multi-tenant labels" <| fun _ ->
            let reg = Registry.create ()
            let resource =
                ResourceAvailability.createForMultiTenantServiceFromStrings
                    "postgres"
                    "db"
                    "dc1"
                    box
                    Audience.Sys

            match ResourceAvailability.enableIn reg instance resource with
            | Error e -> failtestf "Enable should succeed, got %A" e
            | Ok _ ->
                let formatted = ResourceAvailability.getFormattedValueIn reg ()
                Expect.stringContains formatted "resource_availability" "Missing metric"
                Expect.stringContains formatted "res_svc_zone=\"data\"" "Missing zone label"
                Expect.stringContains formatted "res_svc_bucket=\"lmc\"" "Missing bucket label"
                Expect.stringContains formatted " 1" "Resource should be enabled"

        testCase "resource availability common variant" <| fun _ ->
            let reg = Registry.create ()
            let resource =
                ResourceAvailability.createFromStrings
                    "redis"
                    "cache-01"
                    "eu-west"
                    Audience.Sys

            match ResourceAvailability.enableIn reg instance resource with
            | Error e -> failtestf "Enable should succeed, got %A" e
            | Ok _ ->
                let formatted = ResourceAvailability.getFormattedValueIn reg ()
                Expect.stringContains formatted "res_type=\"redis\"" "Missing res_type label"
                Expect.stringContains formatted "res_identification=\"cache-01\"" "Missing res_identification"
                Expect.stringContains formatted "res_location=\"eu-west\"" "Missing res_location"
                Expect.isFalse (formatted.Contains "res_svc_zone") "Common resource must not have zone label"

        testCase "resource availability service variant" <| fun _ ->
            let reg = Registry.create ()
            let resource =
                ResourceAvailability.createForServiceFromStrings
                    "mysql"
                    "db-primary"
                    "us-east"
                    instance
                    Audience.Sys

            match ResourceAvailability.enableIn reg instance resource with
            | Error e -> failtestf "Enable should succeed, got %A" e
            | Ok _ ->
                let formatted = ResourceAvailability.getFormattedValueIn reg ()
                Expect.stringContains formatted "res_type=\"mysql\"" "Missing res_type label"
                Expect.stringContains formatted "res_svc_domain=\"consents\"" "Missing res_svc_domain"
                Expect.isFalse (formatted.Contains "res_svc_zone") "Service resource must not have zone label"

        testCase "resource availability disable" <| fun _ ->
            let reg = Registry.create ()
            let resource =
                ResourceAvailability.createForMultiTenantServiceFromStrings
                    "kafka"
                    "broker-1"
                    "dc2"
                    box
                    Audience.Sys

            ResourceAvailability.enableIn reg instance resource |> ignore

            match ResourceAvailability.disableIn reg instance resource with
            | Error e -> failtestf "Disable should succeed, got %A" e
            | Ok _ ->
                let formatted = ResourceAvailability.getFormattedValueIn reg ()
                Expect.stringContains formatted "res_identification=\"broker-1\"" "Missing identification"

        testCase "state increment metric set value" <| fun _ ->
            let reg = Registry.create ()
            let name = uniqueMetricName "state_set_inc"
            State.incrementMetricSetValueIn reg (Int 3) name DataSetKey.empty |> ignore
            State.incrementMetricSetValueIn reg (Int 4) name DataSetKey.empty |> ignore

            match State.getMetricIn reg name with
            | None -> failtest "Metric should exist"
            | Some metric ->
                Expect.equal (Metric.singleValue metric) (Some (Int 7)) "Incremented set value mismatch"

        testCase "state set metric set value" <| fun _ ->
            let reg = Registry.create ()
            let name = uniqueMetricName "state_set_set"
            State.setMetricSetValueIn reg (Int 10) name DataSetKey.empty
            State.setMetricSetValueIn reg (Int 20) name DataSetKey.empty

            match State.getMetricIn reg name with
            | None -> failtest "Metric should exist"
            | Some metric ->
                Expect.equal (Metric.singleValue metric) (Some (Int 20)) "Set overwrites previous value"

        testCase "state getMetrics returns all simple metrics" <| fun _ ->
            let reg = Registry.create ()
            let name = uniqueMetricName "bulk_read"
            State.setMetricValueIn reg (Int 99) name

            let metrics = State.getMetricsIn reg ()
            let found = metrics |> List.exists (fun m -> Metric.singleValue m = Some (Int 99))
            Expect.isTrue found "getMetrics should include newly set metric"

        testCase "state getHistograms returns all histograms" <| fun _ ->
            let reg = Registry.create ()
            let name = uniqueMetricName "bulk_hist"
            State.observeHistogramSetValueIn reg (HistogramBuckets.create [ 1.0 ]) 0.5 name DataSetKey.empty

            let histograms = State.getHistogramsIn reg ()
            let found = histograms |> List.exists (fun h -> h.DataSets |> List.isEmpty |> not)
            Expect.isTrue found "getHistograms should include newly observed histogram"
    ]

[<Tests>]
let all =
    testList "Alma.Metrics" [
        prometheusPureTests
        statefulTests
    ]

[<EntryPoint>]
let main argv =
    Tests.runTestsInAssemblyWithCLIArgs [ Parallel ] argv
