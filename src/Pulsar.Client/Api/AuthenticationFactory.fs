﻿module Pulsar.Client.Api.AuthenticationFactory

open Pulsar.Client.Auth

let token (token: string) : Authentication =
    DefaultImplementation.newAuthenticationToken token :> Authentication

