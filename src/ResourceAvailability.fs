namespace Alma.Metrics

open Alma.ServiceIdentification

type ResourceType = ResourceType of string

[<RequireQualifiedAccess>]
module ResourceType =
    let value (ResourceType resourceType) = resourceType

type ResourceIdentification = ResourceIdentification of string

[<RequireQualifiedAccess>]
module ResourceIdentification =
    let value (ResourceIdentification identification) = identification

type ResourceLocation = ResourceLocation of string

[<RequireQualifiedAccess>]
module ResourceLocation =
    let value (ResourceLocation location) = location

type CommonResourceAvailability = {
    Type: ResourceType
    Identification: ResourceIdentification
    Location: ResourceLocation
    Audience: Audience
}

type ServiceResourceAvailability = {
    ResourceAvailability: CommonResourceAvailability
    Instance: Instance
}

type MultiTenantServiceResourceAvailability = {
    ResourceAvailability: CommonResourceAvailability
    Box: Box
}

type ResourceAvailability =
    | Common of CommonResourceAvailability
    | Service of ServiceResourceAvailability
    | MultiTenantService of MultiTenantServiceResourceAvailability

type ResourceAvailabilityState =
    | Available of ResourceAvailability
    | NotAvailable of ResourceAvailability

type ResourceStatus =
    | Up
    | Down

[<RequireQualifiedAccess>]
module ResourceAvailability =
    let createFromStrings resourceType resourceIdentification resourceLocation audience =
        Common {
            Type = ResourceType resourceType
            Identification = ResourceIdentification resourceIdentification
            Location = ResourceLocation resourceLocation
            Audience = audience
        }

    let createForServiceFromStrings resourceType resourceIdentification resourceLocation instance audience =
        Service {
            ResourceAvailability = {
                Type = ResourceType resourceType
                Identification = ResourceIdentification resourceIdentification
                Location = ResourceLocation resourceLocation
                Audience = audience
            }
            Instance = instance
        }

    let createForMultiTenantServiceFromStrings resourceType resourceIdentification resourceLocation box audience =
        MultiTenantService {
            ResourceAvailability = {
                Type = ResourceType resourceType
                Identification = ResourceIdentification resourceIdentification
                Location = ResourceLocation resourceLocation
                Audience = audience
            }
            Box = box
        }

    let private commonResourceAvailability = function
        | Common resourceAvailability -> resourceAvailability
        | Service { ResourceAvailability = resourceAvailability } -> resourceAvailability
        | MultiTenantService { ResourceAvailability = resourceAvailability } -> resourceAvailability

    let private createInstanceKeys (instance: Instance) =
        [
            ("res_svc_domain", instance.Domain |> Domain.value)
            ("res_svc_context", instance.Context |> Context.value)
            ("res_svc_purpose", instance.Purpose |> Purpose.value)
            ("res_svc_version", instance.Version |> Version.value)
        ]

    let private createSpotKeys (spot: Spot) =
        [
            ("res_svc_zone", spot.Zone |> Zone.value)
            ("res_svc_bucket", spot.Bucket |> Bucket.value)
        ]

    let private createDataSetKey instance resourceAvailability =
        seq {
            let common = resourceAvailability |> commonResourceAvailability

            yield ("res_location", common.Location |> ResourceLocation.value)
            yield ("res_type", common.Type |> ResourceType.value)
            yield ("res_identification", common.Identification |> ResourceIdentification.value)

            match resourceAvailability with
            | Common _ -> ()
            | Service { Instance = instance } ->
                yield! instance |> createInstanceKeys
            | MultiTenantService { Box = box } ->
                yield! box |> Box.instance |> createInstanceKeys
                yield! box |> Box.spot |> createSpotKeys

            yield ("audience", common.Audience |> Audience.value)
        }
        |> Seq.toList
        |> DataSetKey.createFromInstance instance

    let private resourceAvailabilityMetric = "resource_availability" |> MetricName.createOrFail

    let enableIn, enable =
        Registry.withDefault (fun registry instance resource ->
            resource
            |> createDataSetKey instance
            |> Result.map (State.enableStatusMetricIn registry resourceAvailabilityMetric)
            |> Result.mapError DataSetError)

    let disableIn, disable =
        Registry.withDefault (fun registry instance resource ->
            resource
            |> createDataSetKey instance
            |> Result.map (State.disableStatusMetricIn registry resourceAvailabilityMetric)
            |> Result.mapError DataSetError)

    let getFormattedValueIn, getFormattedValue =
        Registry.withDefault (fun registry () ->
            resourceAvailabilityMetric
            |> State.getMetricIn registry
            |> function
                | Some metric ->
                    { metric with
                        Description = Some "Current instance resources."
                        Type = Some MetricType.Gauge
                    }
                    |> Metric.format
                | _ -> "")
