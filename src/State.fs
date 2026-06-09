namespace Alma.Metrics

module State =
    open System.Collections.Generic
    open System.Collections.Concurrent
    open System

    type private MetricDataSet = ConcurrentDictionary<DataSetKey, MetricValue>
    type private HistogramDataSet = ConcurrentDictionary<DataSetKey, HistogramObservation>

    and private HistogramObservation = {
        Bounds: float list
        CumulativeBucketCounts: int list
        Sum: float
        Count: int
    }

    let private metricsWithDataSets = new ConcurrentDictionary<MetricName, MetricDataSet>()
    let private metricsWithValues = new ConcurrentDictionary<MetricName, MetricValue>()
    let private metricsWithHistogramDataSets = new ConcurrentDictionary<MetricName, HistogramDataSet>()

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

    let private createSetValue metric setKey value =
        let dataSet = new MetricDataSet()

        if metricsWithDataSets.TryAdd(metric, dataSet)
        then dataSet |> setSetValue value setKey
        else failwithf "DataSet \"%A\" for Metric %A was not stored." setKey metric

    let private createHistogramSetValue metric setKey buckets value =
        let dataSet = new HistogramDataSet()

        if metricsWithHistogramDataSets.TryAdd(metric, dataSet)
        then dataSet |> observeHistogramValue buckets value setKey
        else failwithf "Histogram data set \"%A\" for Metric %A was not stored." setKey metric

    let private (|HasDataSet|_|) metric =
        match metricsWithDataSets.TryGetValue metric with
        | true, dataSet -> Some dataSet
        | _ -> None

    let private (|HasSetValue|_|) (dataSet: MetricDataSet) setKey =
        match dataSet.TryGetValue setKey with
        | true, value -> Some value
        | _ -> None

    let private (|HasHistogramDataSet|_|) metric =
        match metricsWithHistogramDataSets.TryGetValue metric with
        | true, dataSet -> Some dataSet
        | _ -> None

    let private (|HasValue|_|) metric =
        match metricsWithValues.TryGetValue metric with
        | true, dataSet -> Some dataSet
        | _ -> None

    //
    // Write
    //

    let incrementMetricSetValue value metric setKey =
        match metric with
        | HasDataSet dataSet -> addSetValue value setKey dataSet
        | _ -> createSetValue metric setKey value

    let incrementMetricValue value metric =
        metricsWithValues.AddOrUpdate(
            metric,
            value,
            fun _ old -> old + value
        )

    let observeHistogramSetValue buckets value metric setKey =
        match metric with
        | HasHistogramDataSet dataSet -> observeHistogramValue buckets value setKey dataSet
        | _ -> createHistogramSetValue metric setKey buckets value

    let enableStatusMetric metric setKey =
        match metric with
        | HasDataSet dataSet ->
            match setKey with
            | HasSetValue dataSet value when value = Int 1 -> ()
            | _ -> setSetValue (Int 1) setKey dataSet |> ignore
        | _ -> createSetValue metric setKey (Int 1) |> ignore

    let disableStatusMetric metric setKey =
        match metric with
        | HasDataSet dataSet ->
            match setKey with
            | HasSetValue dataSet value when value = Int 0 -> ()
            | _ -> setSetValue (Int 0) setKey dataSet |> ignore
        | _ -> createSetValue metric setKey (Int 0) |> ignore

    let setMetricSetValue value metric setKey =
        match metric with
        | HasDataSet dataSet -> setSetValue value setKey dataSet
        | _ -> createSetValue metric setKey value
        |> ignore

    let setMetricValue value metric =
        metricsWithValues.AddOrUpdate(
            metric,
            value,
            fun _ _ -> value
        )
        |> ignore

    //
    // Read
    //

    let getMetric metric =
        match metric with
        | HasDataSet dataSet ->
            dataSet
            |> Seq.map (kvPairToTuple >> DataSet.createFromTuple)
            |> List.ofSeq
            |> Metric.createMetric metric None None
            |> Some
        | _ ->
            match metric with
            | HasValue value ->
                value
                |> Metric.createSimpleMetric metric
                |> Some
            | _ ->
                None

    let getHistogram metric =
        match metric with
        | HasHistogramDataSet dataSet ->
            dataSet
            |> Seq.map (kvPairToTuple >> toHistogramDataSet)
            |> List.ofSeq
            |> Histogram.createHistogram metric None
            |> Some
        | _ ->
            None

    let getHistograms () =
        metricsWithHistogramDataSets
        |> Seq.map (
            kvPairToTuple
            >> fun (name, dataSets) ->
                dataSets
                |> Seq.map (kvPairToTuple >> toHistogramDataSet)
                |> List.ofSeq
                |> Histogram.createHistogram name None
        )
        |> List.ofSeq

    let getMetrics () =
        [
            yield!
                metricsWithValues
                |> Seq.map (
                    kvPairToTuple
                    >> fun nameValue ->
                        nameValue
                        ||> Metric.createSimpleMetric
                )
            yield!
                metricsWithDataSets
                |> Seq.map (
                    kvPairToTuple
                    >> fun (name, dataSets) ->
                        dataSets
                        |> Seq.map (kvPairToTuple >> DataSet.createFromTuple)
                        |> List.ofSeq
                        |> Metric.createSimpleMetricWithDataSets name
                )
        ]
