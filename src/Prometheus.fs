namespace Alma.Metrics

open System
open Feather.ErrorHandling

//
// Name
//

type private Name = private Name of string

type NameError =
    | EmptyName
    | TooShortName of int

[<RequireQualifiedAccess>]
module private Name =
    let value (Name name) = name

    let create nameConstructor liftError (name: string) =
        if name |> String.IsNullOrEmpty then EmptyName |> liftError |> Error
        elif name.Length < 2 then (TooShortName 2) |> liftError |> Error
        else
            name
                .Replace("-", "_")
                .Replace(" ", "_")
            |> Name
            |> nameConstructor
            |> Ok

// Metric Format
// ================
//
// metric_name [
//   "{" label_name "=" `"` label_value `"` { "," label_name "=" `"` label_value `"` } [ "," ] "}"
// ] value [ timestamp ]
//
// For more information, see https://prometheus.io/docs/instrumenting/exposition_formats/

//
// Metric Name
//

type MetricName = private MetricName of Name

type MetricNameError =
    | NameError of NameError

[<RequireQualifiedAccess>]
module MetricName =
    let value (MetricName name) = name |> Name.value

    let create = Name.create MetricName NameError
    let createOrFail = create >> Result.orFail

//
// Metric Value
//

type MetricValue =
    | Float of float
    | Int of int
    | Infinite
    | NegativeInfinite
    | NotANumber

    static member (+) (value1, value2) =
        match (value1, value2) with
        | Infinite, NegativeInfinite
        | NegativeInfinite, Infinite -> failwith "Inf - Inf is not supported math operation."
        | Int int1, Int int2 -> Int (int1 + int2)
        | Float float1, Float float2 -> Float (float1 + float2)
        | Int int1, Float float2 -> Float ((int1 |> float) + float2)
        | Float float1, Int int2 -> Float ((int2 |> float) + float1)
        | Infinite, _
        | _, Infinite -> Infinite
        | NegativeInfinite, _
        | _, NegativeInfinite -> NegativeInfinite
        | NotANumber, value
        | value, NotANumber -> value

type MetricType =
    | Counter
    | Gauge
    | Histogram
    | Summary
    | Untyped

[<RequireQualifiedAccess>]
module private MetricType =
    let value = function
        | Counter -> "counter"
        | Gauge -> "gauge"
        | Histogram -> "histogram"
        | Summary -> "summary"
        | Untyped -> "untyped"

type HistogramBuckets = private HistogramBuckets of float list

[<RequireQualifiedAccess>]
module HistogramBuckets =
    let private normalize bounds =
        bounds
        |> List.filter (fun bound ->
            not (Double.IsNaN bound)
            && not (Double.IsInfinity bound)
        )
        |> List.sort
        |> List.distinct

    let create bounds =
        bounds
        |> normalize
        |> HistogramBuckets

    let defaultBuckets =
        [ 0.005; 0.01; 0.025; 0.05; 0.1; 0.25; 0.5; 1.0; 2.5; 5.0; 10.0 ]
        |> create

    let value (HistogramBuckets bounds) =
        bounds @ [ Double.PositiveInfinity ]

//
// Label
//

type LabelName = private LabelName of Name

type LabelNameError =
    | NameError of NameError

[<RequireQualifiedAccess>]
module private LabelName =
    let value (LabelName name) = name |> Name.value

    let create = Name.create LabelName NameError

type Label = {
    Name: LabelName
    Value: string
}

[<RequireQualifiedAccess>]
module Label =
    let create (name, value) =
        result {
            let! name' = name |> LabelName.create

            return {
                Name = name'
                Value = value
            }
        }

//
// Data sets
//

type SimpleDataSet = {
    Key: (string * string) list
    Value: MetricValue
    Timestamp: DateTime option
}

[<RequireQualifiedAccess>]
module SimpleDataSet =
    let createWithTimestamp labels value timestamp =
        {
            Key = labels
            Value = value
            Timestamp = timestamp
        }

    let create labels value =
        createWithTimestamp labels value None

type SimpleHistogramDataSet = {
    Key: (string * string) list
    Buckets: HistogramBuckets
    Observations: float list
    Timestamp: DateTime option
}

[<RequireQualifiedAccess>]
module SimpleHistogramDataSet =
    let createWithTimestamp labels buckets observations timestamp =
        {
            Key = labels
            Buckets = buckets
            Observations = observations
            Timestamp = timestamp
        }

    let create labels buckets observations =
        createWithTimestamp labels buckets observations None

type DataSetKey = DataSetKey of Label list

type DataSet = {
    Key: DataSetKey
    Value: MetricValue
    Timestamp: DateTime option
}

type HistogramBucket = {
    UpperBound: MetricValue
    CumulativeCount: int
}

type HistogramDataSet = {
    Key: DataSetKey
    Buckets: HistogramBucket list
    Sum: float
    Count: int
    Timestamp: DateTime option
}

type DataSetError =
    | LabelError of LabelNameError

[<RequireQualifiedAccess>]
module DataSetKey =
    open Alma.ServiceIdentification

    let empty = DataSetKey []

    let labels (DataSetKey labels) = labels

    let createFromInstance (instance: Instance) labels =
        [
            ("svc_domain", instance.Domain |> Domain.value)
            ("svc_context", instance.Context |> Context.value)
            ("svc_purpose", instance.Purpose |> Purpose.value)
            ("svc_version", instance.Version |> Version.value)
        ] @ labels
        |> List.map Label.create
        |> Result.sequence
        |> Result.map DataSetKey
        |> Result.mapError LabelError

[<RequireQualifiedAccess>]
module DataSet =
    let createFromSimple (simpleDataSet: SimpleDataSet): Result<DataSet, DataSetError> =
        result {
            let! labels =
                simpleDataSet.Key
                |> List.map Label.create
                |> Result.sequence
                |> Result.mapError LabelError

            return {
                Key = DataSetKey labels
                Value = simpleDataSet.Value
                Timestamp = simpleDataSet.Timestamp
            }
        }

    let createFromTuple (key, value) =
        {
            Key = key
            Value = value
            Timestamp = None
        }

    let createFromTupleWithTimpestamp (key, value, timestamp) =
        {
            Key = key
            Value = value
            Timestamp = Some timestamp
        }

[<RequireQualifiedAccess>]
module HistogramDataSet =
    let private computeBuckets buckets observations =
        buckets
        |> HistogramBuckets.value
        |> List.map (fun bound ->
            {
                UpperBound =
                    if Double.IsPositiveInfinity bound then Infinite
                    else Float bound
                CumulativeCount =
                    observations
                    |> List.filter (fun observation -> observation <= bound)
                    |> List.length
            }
        )

    let createFromSimple (simpleDataSet: SimpleHistogramDataSet): Result<HistogramDataSet, DataSetError> =
        result {
            let! labels =
                simpleDataSet.Key
                |> List.map Label.create
                |> Result.sequence
                |> Result.mapError LabelError

            return {
                Key = DataSetKey labels
                Buckets = computeBuckets simpleDataSet.Buckets simpleDataSet.Observations
                Sum = simpleDataSet.Observations |> List.sum
                Count = simpleDataSet.Observations |> List.length
                Timestamp = simpleDataSet.Timestamp
            }
        }

//
// Metric
//

type Metric = {
    Name: MetricName
    Description: string option
    Type: MetricType option
    DataSets: DataSet list
}

type MetricError =
    | MetricNameError of MetricNameError
    | DataSetError of DataSetError

type Histogram = {
    Name: MetricName
    Description: string option
    DataSets: HistogramDataSet list
}

[<RequireQualifiedAccess>]
module MetricError =
    let private nameErrorValue field = function
        | EmptyName -> sprintf "%s name must not be empty!" field
        | TooShortName minLength -> sprintf "%s name must be longer than %i chars!" field minLength

    let value = function
        | MetricNameError metricNameError ->
            match metricNameError with
            | MetricNameError.NameError nameError -> nameError |> nameErrorValue "Metric"
        | DataSetError dataSetError ->
            match dataSetError with
            | LabelError labelNameError ->
                match labelNameError with
                | LabelNameError.NameError nameError -> nameError |> nameErrorValue "Label"

//
// Implementation
//

[<RequireQualifiedAccess>]
module private Format =
    let private noneIfEmpty string =
        if string |> String.IsNullOrEmpty then None
        else Some string

    let private createHeader name description metricType =
        let name' =
            name
            |> MetricName.value
        let description' =
            description
            |> Option.map (sprintf "# HELP %s %s" name')
        let type' =
            metricType
            |> Option.map MetricType.value
            |> Option.map (sprintf "# TYPE %s %s" name')

        [ description'; type' ]
        |> List.choose id
        |> String.concat "\n"
        |> noneIfEmpty
        |> Option.map (sprintf "%s\n")

    let private createMetricHeader { Name = name; Description = description; Type = metricType } =
        createHeader name description metricType

    let private formatLabelsWithExtra extraLabels (DataSetKey labels) =
        labels
        |> List.map (fun label ->
            sprintf "%s=\"%s\"" (label.Name |> LabelName.value) label.Value
        )
        |> List.append extraLabels
        |> String.concat ", "
        |> noneIfEmpty
        |> Option.map (sprintf "{%s}")

    let private formatLabels = formatLabelsWithExtra []

    let private formatValue = function
        | Int int -> int.ToString()
        | Float float ->
            let str = float.ToString Globalization.CultureInfo.InvariantCulture
            if str.Contains('.') then str.TrimEnd('0').TrimEnd('.') else str
        | Infinite -> "+Inf"
        | NegativeInfinite -> "-Inf"
        | NotANumber -> "Nan"

    let private formatTimestamp (timestamp) =
        let toTimestamp (dateTime: DateTime) =
            // https://stackoverflow.com/questions/17632584/how-to-get-the-unix-timestamp-in-c-sharp
            dateTime.Subtract(DateTime(1970, 1, 1)).TotalSeconds
            |> int

        timestamp
        |> Option.map (toTimestamp >> string)

    let private formatDataSet nameValue (dataSet: DataSet) =
        [
            nameValue |> Some
            dataSet.Key |> formatLabels
            dataSet.Value |> formatValue |> Some
            dataSet.Timestamp |> formatTimestamp
        ]
        |> List.choose id
        |> String.concat " "

    let private formatDataSets name (dataSets: DataSet list) =
        let nameValue = name |> MetricName.value

        dataSets
        |> List.sortBy (fun { Key = key } -> key)
        |> List.map (formatDataSet nameValue)
        |> String.concat "\n"
        |> sprintf "%s\n"

    let toString metric =
        let header =
            metric
            |> createMetricHeader
        let dataSets =
            metric.DataSets
            |> formatDataSets metric.Name
            |> Some

        [ header; dataSets ]
        |> List.choose id
        |> String.concat ""
        |> sprintf "%s\n"

    let private formatNameWithLabels name (key: DataSetKey) extraLabels =
        let nameValue = name |> MetricName.value

        match key |> formatLabelsWithExtra extraLabels with
        | Some labels -> sprintf "%s%s" nameValue labels
        | None -> nameValue

    let private applyTimestamp timestamp sample =
        timestamp
        |> formatTimestamp
        |> Option.map (sprintf "%s %s" sample)
        |> Option.defaultValue sample

    let private formatHistogramUpperBound = function
        | Infinite -> "+Inf"
        | value -> formatValue value

    let private formatHistogramDataSet name (dataSet: HistogramDataSet) =
        let bucketLines =
            dataSet.Buckets
            |> List.map (fun bucket ->
                let bucketName = sprintf "%s_bucket" (name |> MetricName.value)
                let sampleName =
                    formatNameWithLabels
                        (bucketName |> MetricName.createOrFail)
                        dataSet.Key
                        [ sprintf "le=\"%s\"" (bucket.UpperBound |> formatHistogramUpperBound) ]

                sprintf "%s %i" sampleName bucket.CumulativeCount
                |> applyTimestamp dataSet.Timestamp
            )

        let sumLine =
            let sumName = sprintf "%s_sum" (name |> MetricName.value)
            let sampleName =
                formatNameWithLabels
                    (sumName |> MetricName.createOrFail)
                    dataSet.Key
                    []

            sprintf "%s %s" sampleName (dataSet.Sum |> Float |> formatValue)
            |> applyTimestamp dataSet.Timestamp

        let countLine =
            let countName = sprintf "%s_count" (name |> MetricName.value)
            let sampleName =
                formatNameWithLabels
                    (countName |> MetricName.createOrFail)
                    dataSet.Key
                    []

            sprintf "%s %s" sampleName (dataSet.Count |> Int |> formatValue)
            |> applyTimestamp dataSet.Timestamp

        bucketLines @ [ sumLine; countLine ]

    let histogramToString (histogram: Histogram) =
        let header =
            createHeader histogram.Name histogram.Description (Some Histogram)

        let dataSets =
            histogram.DataSets
            |> List.sortBy (fun { Key = key } -> key)
            |> List.collect (formatHistogramDataSet histogram.Name)
            |> String.concat "\n"
            |> sprintf "%s\n"
            |> Some

        [ header; dataSets ]
        |> List.choose id
        |> String.concat ""
        |> sprintf "%s\n"

[<RequireQualifiedAccess>]
module Metric =
    let createMetric name description metricType dataSets =
        {
            Name = name
            Description = description
            Type = metricType
            DataSets = dataSets
        }

    let create name description metricType dataSets =
        result {
            let! name' =
                name
                |> MetricName.create
                |> Result.mapError MetricError.MetricNameError

            return createMetric name' description metricType dataSets
        }

    let createWithSimpleDataSets name description metricType simpleDataSets =
        result {
            let! dataSets =
                simpleDataSets
                |> List.map DataSet.createFromSimple
                |> Result.sequence
                |> Result.mapError MetricError.DataSetError

            return! create name description metricType dataSets
        }

    let createSingle name value description metricType =
        create name description metricType [ { Key = DataSetKey.empty; Value = value; Timestamp = None } ]

    let createSimple name value =
        create name None None [ { Key = DataSetKey.empty; Value = value; Timestamp = None } ]

    let createSimpleMetricWithDataSets name (dataSets: DataSet list) =
        createMetric name None None dataSets

    let createSimpleMetric name value =
        createSimpleMetricWithDataSets name [ { Key = DataSetKey.empty; Value = value; Timestamp = None } ]

    let singleValue (metric: Metric) =
        match metric.DataSets with
        | [ singleDataSet ] ->
            let singleDataSet: DataSet = singleDataSet

            match singleDataSet.Key |> DataSetKey.labels with
            | [] -> Some singleDataSet.Value
            | _ -> None
        | _ -> None

    let format = Format.toString

[<RequireQualifiedAccess>]
module Histogram =
    let createHistogram name description dataSets =
        {
            Name = name
            Description = description
            DataSets = dataSets
        }

    let create name description dataSets =
        result {
            let! name' =
                name
                |> MetricName.create
                |> Result.mapError MetricError.MetricNameError

            return createHistogram name' description dataSets
        }

    let createWithSimpleDataSets name description simpleDataSets =
        result {
            let! dataSets =
                simpleDataSets
                |> List.map HistogramDataSet.createFromSimple
                |> Result.sequence
                |> Result.mapError MetricError.DataSetError

            return! create name description dataSets
        }

    let format = Format.histogramToString
