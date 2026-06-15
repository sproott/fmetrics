namespace Alma.Metrics

open System.Collections.Concurrent
open System.Collections.Generic
open System

type private HistogramObservation = {
    Bounds: float list
    CumulativeBucketCounts: int list
    Sum: float
    Count: int
}

type Registry = private {
    MetricsWithDataSets: ConcurrentDictionary<MetricName, ConcurrentDictionary<DataSetKey, MetricValue>>
    MetricsWithValues: ConcurrentDictionary<MetricName, MetricValue>
    MetricsWithHistogramDataSets: ConcurrentDictionary<MetricName, ConcurrentDictionary<DataSetKey, HistogramObservation>>
}

[<RequireQualifiedAccess>]
module Registry =
    let create () = {
        MetricsWithDataSets = ConcurrentDictionary()
        MetricsWithValues = ConcurrentDictionary()
        MetricsWithHistogramDataSets = ConcurrentDictionary()
    }

    let defaultRegistry = create ()

    /// Given a function whose first argument is a Registry, returns a tuple of
    /// (the function itself, the function partially applied to defaultRegistry).
    /// Use to declare the *In variant and the default-registry variant together:
    ///   let enableIn, enable = Registry.withDefault (fun reg ... -> ...)
    let withDefault (f: Registry -> 'a) : (Registry -> 'a) * 'a =
        f, f defaultRegistry

module State =
    type private MetricDataSet = ConcurrentDictionary<DataSetKey, MetricValue>
    type private HistogramDataSet = ConcurrentDictionary<DataSetKey, HistogramObservation>

    let private kvPairToTuple (kvPair: KeyValuePair<_, _>) =
        (kvPair.Key, kvPair.Value)

    let private addSetValue value key (metricDataSet: MetricDataSet) =
        metricDataSet.AddOrUpdate(
            key,
            value,
            fun _ old -> old + value
        )

    let private setSetValue value key (metricDataSet: MetricDataSet) =
        metricDataSet.AddOrUpdate(
            key,
            value,
            fun _ _ -> value
        )

    let private createHistogramObservation buckets =
        let normalizedBounds =
            buckets
            |> HistogramBuckets.value

        {
            Bounds = normalizedBounds
            CumulativeBucketCounts = normalizedBounds |> List.map (fun _ -> 0)
            Sum = 0.0
            Count = 0
        }

    let private addHistogramObservationValue value observation =
        let newBucketCounts =
            observation.Bounds
            |> List.zip observation.CumulativeBucketCounts
            |> List.map (fun (currentCount, bound) ->
                if value <= bound then currentCount + 1
                else currentCount
            )

        {
            observation with
                CumulativeBucketCounts = newBucketCounts
                Sum = observation.Sum + value
                Count = observation.Count + 1
        }

    let private observeHistogramValue buckets value key (histogramDataSet: HistogramDataSet) =
        let initialObservation =
            buckets
            |> createHistogramObservation
            |> addHistogramObservationValue value

        histogramDataSet.AddOrUpdate(
            key,
            initialObservation,
            fun _ observation ->
                if observation.Bounds <> initialObservation.Bounds then
                    failwithf "Histogram bounds mismatch for key %A" key

                observation
                |> addHistogramObservationValue value
        )
        |> ignore

    let private toHistogramDataSet (key, observation) =
        {
            Key = key
            Buckets =
                observation.Bounds
                |> List.zip observation.CumulativeBucketCounts
                |> List.map (fun (count, bound) ->
                    {
                        UpperBound =
                            if Double.IsPositiveInfinity bound
                            then Infinite
                            else Float bound
                        CumulativeCount = count
                    }
                )
            Sum = observation.Sum
            Count = observation.Count
            Timestamp = None
        }

    let private createSetValueIn (registry: Registry) metric setKey value =
        let dataSet = new MetricDataSet()

        if registry.MetricsWithDataSets.TryAdd(metric, dataSet)
        then dataSet |> setSetValue value setKey
        else failwithf "DataSet \"%A\" for Metric %A was not stored." setKey metric

    let private createHistogramSetValueIn (registry: Registry) metric setKey buckets value =
        let dataSet = new HistogramDataSet()

        if registry.MetricsWithHistogramDataSets.TryAdd(metric, dataSet)
        then dataSet |> observeHistogramValue buckets value setKey
        else failwithf "Histogram data set \"%A\" for Metric %A was not stored." setKey metric

    let private (|HasDataSetIn|_|) (registry: Registry) metric =
        match registry.MetricsWithDataSets.TryGetValue metric with
        | true, dataSet -> Some dataSet
        | _ -> None

    let private (|HasSetValue|_|) (dataSet: MetricDataSet) setKey =
        match dataSet.TryGetValue setKey with
        | true, value -> Some value
        | _ -> None

    let private (|HasHistogramDataSetIn|_|) (registry: Registry) metric =
        match registry.MetricsWithHistogramDataSets.TryGetValue metric with
        | true, dataSet -> Some dataSet
        | _ -> None

    let private (|HasValueIn|_|) (registry: Registry) metric =
        match registry.MetricsWithValues.TryGetValue metric with
        | true, value -> Some value
        | _ -> None

    //
    // Write
    //

    let incrementMetricSetValueIn, incrementMetricSetValue =
        Registry.withDefault (fun (registry: Registry) value metric setKey ->
            match metric with
            | HasDataSetIn registry dataSet -> addSetValue value setKey dataSet
            | _ -> createSetValueIn registry metric setKey value)

    let incrementMetricValueIn, incrementMetricValue =
        Registry.withDefault (fun (registry: Registry) value metric ->
            registry.MetricsWithValues.AddOrUpdate(
                metric,
                value,
                fun _ old -> old + value
            ))

    let observeHistogramSetValueIn, observeHistogramSetValue =
        Registry.withDefault (fun (registry: Registry) buckets value metric setKey ->
            match metric with
            | HasHistogramDataSetIn registry dataSet -> observeHistogramValue buckets value setKey dataSet
            | _ -> createHistogramSetValueIn registry metric setKey buckets value)

    let enableStatusMetricIn, enableStatusMetric =
        Registry.withDefault (fun (registry: Registry) metric setKey ->
            match metric with
            | HasDataSetIn registry dataSet ->
                match setKey with
                | HasSetValue dataSet value when value = Int 1 -> ()
                | _ -> setSetValue (Int 1) setKey dataSet |> ignore
            | _ -> createSetValueIn registry metric setKey (Int 1) |> ignore)

    let disableStatusMetricIn, disableStatusMetric =
        Registry.withDefault (fun (registry: Registry) metric setKey ->
            match metric with
            | HasDataSetIn registry dataSet ->
                match setKey with
                | HasSetValue dataSet value when value = Int 0 -> ()
                | _ -> setSetValue (Int 0) setKey dataSet |> ignore
            | _ -> createSetValueIn registry metric setKey (Int 0) |> ignore)

    let setMetricSetValueIn, setMetricSetValue =
        Registry.withDefault (fun (registry: Registry) value metric setKey ->
            match metric with
            | HasDataSetIn registry dataSet -> setSetValue value setKey dataSet
            | _ -> createSetValueIn registry metric setKey value
            |> ignore)

    let setMetricValueIn, setMetricValue =
        Registry.withDefault (fun (registry: Registry) value metric ->
            registry.MetricsWithValues.AddOrUpdate(
                metric,
                value,
                fun _ _ -> value
            )
            |> ignore)

    //
    // Read
    //

    let getMetricIn, getMetric =
        Registry.withDefault (fun (registry: Registry) metric ->
            match metric with
            | HasDataSetIn registry dataSet ->
                dataSet
                |> Seq.map (kvPairToTuple >> DataSet.createFromTuple)
                |> List.ofSeq
                |> Metric.createMetric metric None None
                |> Some
            | _ ->
                match metric with
                | HasValueIn registry value ->
                    value
                    |> Metric.createSimpleMetric metric
                    |> Some
                | _ ->
                    None)

    let getHistogramIn, getHistogram =
        Registry.withDefault (fun (registry: Registry) metric ->
            match metric with
            | HasHistogramDataSetIn registry dataSet ->
                dataSet
                |> Seq.map (kvPairToTuple >> toHistogramDataSet)
                |> List.ofSeq
                |> Histogram.createHistogram metric None
                |> Some
            | _ ->
                None)

    let getHistogramsIn, getHistograms =
        Registry.withDefault (fun (registry: Registry) () ->
            registry.MetricsWithHistogramDataSets
            |> Seq.map (
                kvPairToTuple
                >> fun (name, dataSets) ->
                    dataSets
                    |> Seq.map (kvPairToTuple >> toHistogramDataSet)
                    |> List.ofSeq
                    |> Histogram.createHistogram name None
            )
            |> List.ofSeq)

    let getMetricsIn, getMetrics =
        Registry.withDefault (fun (registry: Registry) () ->
            [
                yield!
                    registry.MetricsWithValues
                    |> Seq.map (
                        kvPairToTuple
                        >> fun nameValue ->
                            nameValue
                            ||> Metric.createSimpleMetric
                    )
                yield!
                    registry.MetricsWithDataSets
                    |> Seq.map (
                        kvPairToTuple
                        >> fun (name, dataSets) ->
                            dataSets
                            |> Seq.map (kvPairToTuple >> DataSet.createFromTuple)
                            |> List.ofSeq
                            |> Metric.createSimpleMetricWithDataSets name
                    )
            ])
