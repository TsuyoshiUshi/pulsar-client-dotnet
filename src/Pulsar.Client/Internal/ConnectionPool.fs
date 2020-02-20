﻿namespace Pulsar.Client.Internal

open Pulsar.Client.Common
open FSharp.Control.Tasks.V2.ContextInsensitive
open System.Collections.Concurrent
open System.Net
open System.Threading.Tasks
open Pipelines.Sockets.Unofficial
open System
open Microsoft.Extensions.Logging
open pulsar.proto
open System.Reflection
open Pulsar.Client.Internal
open System.IO.Pipelines
open Pulsar.Client.Api
open System.Net.Sockets
open System.Net.Security
open System.Security.Cryptography.X509Certificates

type internal ConnectionPool (config: PulsarClientConfiguration) =

    let clientVersion = "Pulsar.Client v" + Assembly.GetExecutingAssembly().GetName().Version.ToString()
    let protocolVersion =
        ProtocolVersion.GetValues(typeof<ProtocolVersion>)
        :?> ProtocolVersion[]
        |> Array.last

    let connections = ConcurrentDictionary<LogicalAddress, Lazy<Task<ClientCnx>>>()

    // from https://github.com/mgravell/Pipelines.Sockets.Unofficial/blob/master/src/Pipelines.Sockets.Unofficial/SocketConnection.Connect.cs
    let getSocket (endpoint: DnsEndPoint) =
        let addressFamily =
            if endpoint.AddressFamily = AddressFamily.Unspecified then
                AddressFamily.InterNetwork
            else
                endpoint.AddressFamily
        let protocolType =
            if addressFamily = AddressFamily.Unix then
                ProtocolType.Unspecified
            else
                ProtocolType.Tcp
        let socket = new Socket(addressFamily, SocketType.Stream, protocolType)
        SocketConnection.SetRecommendedClientOptions(socket)

        use args = new SocketAwaitableEventArgs(PipeScheduler.ThreadPool)
        args.RemoteEndPoint <- endpoint
        Log.Logger.LogDebug("Socket connecting to {0}", endpoint)

        task {
            try
                if (socket.ConnectAsync(args) |> not) then
                    args.Complete()
                let! _ = args
                Log.Logger.LogDebug("Socket connected to {0}", endpoint)
                return socket
            with
            | ex ->
                socket.Dispose()
                Log.Logger.LogError(ex, "Socket connection failed to {0}", endpoint)
                return reraize ex
        }

    let remoteCertificateValidationCallback (sender: obj) (cert: X509Certificate) (chain: X509Chain) (errors: SslPolicyErrors) =
        match errors with
        | SslPolicyErrors.None ->
            Log.Logger.LogDebug("No certificate errors")
            true
        | SslPolicyErrors.RemoteCertificateNotAvailable ->
            Log.Logger.LogError("RemoteCertificateNotAvailable")
            false
        | SslPolicyErrors.RemoteCertificateNameMismatch ->
            let result = not config.TlsHostnameVerificationEnable
            let log = if result then Log.Logger.LogWarning else Log.Logger.LogError
            log("RemoteCertificateNameMismatch")
            result
        | SslPolicyErrors.RemoteCertificateChainErrors ->
            let result =
                config.TlsAllowInsecureConnection
                ||
                if isNull config.TlsTrustCertificate then
                    false
                else
                    chain.ChainPolicy.ExtraStore.Add(config.TlsTrustCertificate) |> ignore
                    use cert2 = new X509Certificate2(cert)
                    chain.Build(cert2)
            let log = if result then Log.Logger.LogWarning else Log.Logger.LogError
            log("RemoteCertificateChainErrors")
            result
        | error ->
            Log.Logger.LogError("Unknown ssl error: {0}", error)
            false

    let connect (broker: Broker) =
        Log.Logger.LogInformation("Connecting to {0}", broker)
        task {
            let (PhysicalAddress physicalAddress) = broker.PhysicalAddress
            let (LogicalAddress logicalAddress) = broker.LogicalAddress
            let pipeOptions = PipeOptions(pauseWriterThreshold = int64 Commands.DEFAULT_MAX_MESSAGE_SIZE )
            let! socket = getSocket physicalAddress

            let! connection =
                task {
                    if config.UseTls then
                        Log.Logger.LogDebug("Configuring ssl for {0}", physicalAddress)
                        let sslStream = new SslStream(new NetworkStream(socket), false, RemoteCertificateValidationCallback(remoteCertificateValidationCallback))
                        do! sslStream.AuthenticateAsClientAsync(physicalAddress.Host)
                        let pipeConnection = StreamConnection.GetDuplex(sslStream, pipeOptions)
                        let writerStream = StreamConnection.GetWriter(pipeConnection.Output)
                        return
                            {
                                Input = pipeConnection.Input
                                Output = writerStream
                                Dispose = sslStream.Dispose
                            }
                    else
                        let pipeConnection = SocketConnection.Create(socket, pipeOptions)
                        let writerStream = StreamConnection.GetWriter(pipeConnection.Output)
                        return
                            {
                                Input = pipeConnection.Input
                                Output = writerStream
                                Dispose = pipeConnection.Dispose
                            }
                }

            let initialConnectionTsc = TaskCompletionSource<ClientCnx>(TaskCreationOptions.RunContinuationsAsynchronously)
            let unregisterClientCnx (broker: Broker) =
                connections.TryRemove(broker.LogicalAddress) |> ignore
            let clientCnx = ClientCnx(config, broker, connection, initialConnectionTsc, unregisterClientCnx)
            let proxyToBroker = if physicalAddress = logicalAddress then None else Some logicalAddress
            let authenticationDataProvider = config.Authentication.GetAuthData(physicalAddress.Host);
            let authData = authenticationDataProvider.Authenticate({ Bytes = AuthData.INIT_AUTH_DATA })
            let authMethodName = config.Authentication.GetAuthMethodName()
            let connectPayload = Commands.newConnect authMethodName authData clientVersion protocolVersion proxyToBroker

            let! success = clientCnx.Send connectPayload
            if not success then
                raise (ConnectionFailedOnSend "ConnectionPool connect")
            Log.Logger.LogInformation("Connected to {0}", broker)
            return! initialConnectionTsc.Task
        }

    member this.GetConnection (broker: Broker) =
        connections.GetOrAdd(broker.LogicalAddress, fun(address) ->
            lazy connect broker).Value

    member this.GetBrokerlessConnection (address: DnsEndPoint) =
        this.GetConnection { LogicalAddress = LogicalAddress address; PhysicalAddress = PhysicalAddress address }

    member this.Close() =
        connections
        |> Seq.map (fun kv -> kv.Value.Value.Result)
        |> Seq.iter (fun clientCnx -> clientCnx.Dispose())