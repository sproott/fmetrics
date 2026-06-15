namespace Alma.Metrics

module ServiceStatus =
    type MarkAsEnabled = MarkAsEnabled of (unit -> unit)
    type MarkAsDisabled = MarkAsDisabled of (unit -> unit)

    [<RequireQualifiedAccess>]
    module MarkAsEnabled =
        let execute (MarkAsEnabled f) = f()

    [<RequireQualifiedAccess>]
    module MarkAsDisabled =
        let execute (MarkAsDisabled f) = f()

    type ServiceStatus = {
        MarkAsEnabled: MarkAsEnabled
        MarkAsDisabled: MarkAsDisabled
    }

    let private createDataSetKey instance audience =
        [
            ("audience", audience |> Audience.value)
        ]
        |> DataSetKey.createFromInstance instance

    let private serviceStatusMetric = "service_status" |> MetricName.createOrFail

    let markAsEnabledIn, markAsEnabled =
        Registry.withDefault (fun registry instance audience ->
            audience
            |> createDataSetKey instance
            |> Result.map (fun dataSetKey -> fun () -> State.enableStatusMetricIn registry serviceStatusMetric dataSetKey)
            |> Result.map MarkAsEnabled
            |> Result.mapError DataSetError)

    let markAsDisabledIn, markAsDisabled =
        Registry.withDefault (fun registry instance audience ->
            audience
            |> createDataSetKey instance
            |> Result.map (fun dataSetKey -> fun () -> State.disableStatusMetricIn registry serviceStatusMetric dataSetKey)
            |> Result.map MarkAsDisabled
            |> Result.mapError DataSetError)

    let getFormattedValueIn, getFormattedValue =
        Registry.withDefault (fun registry () ->
            serviceStatusMetric
            |> State.getMetricIn registry
            |> function
                | Some metric ->
                    { metric with
                        Description = Some "Current service status."
                        Type = Some MetricType.Gauge
                    }
                    |> Metric.format
                | _ -> "")
