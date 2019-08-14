﻿namespace Pulsar.Client.Internal

open System
open System.Net
open Pulsar.Client.Api

//TODO: implement and move into separate file
type internal ServiceUri = Uri

type internal ServiceNameResolver(config: PulsarClientConfiguration) =

    let mutable config = config

    member this.GetServiceUrl() = config.ServiceUrl

    member this.UpdateServiceUrl (serviceUrl: string) =
        config <- { config with ServiceUrl = serviceUrl }
        this.GetServiceUrl()

    member this.GetServiceUri() = ServiceUri(config.ServiceUrl)

    member this.ResolveHost() =
        let uri = this.GetServiceUri()
        DnsEndPoint(uri.Host, uri.Port)

    member this.ResolveHostUri() = Uri(config.ServiceUrl)