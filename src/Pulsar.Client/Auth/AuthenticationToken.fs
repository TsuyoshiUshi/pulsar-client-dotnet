﻿namespace Pulsar.Client.Auth

open Pulsar.Client.Api

type AuthenticationToken (supplier: unit -> string) =
    inherit Authentication()

    new(token: string) =
        AuthenticationToken (fun () -> token)

    override this.GetAuthMethodName() =
        "token"
    override this.GetAuthData() =
        AuthenticationDataToken(supplier) :> AuthenticationDataProvider
