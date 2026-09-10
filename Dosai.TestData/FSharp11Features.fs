// FSharp11Features.fs
// F# 11 source (ships with .NET 11) exercising record spread syntax - `{ ...record; Field = v }`,
// which is a parse error on earlier F# compilers - alongside interpolated strings and delegate
// arguments. The F# frontend must keep extracting the declared functions, dependencies, and
// calls from this file without failing on the new syntax.
//
// Source-mode fixture only: it is deliberately not in Dosai.TestData.FSharp.fsproj's Compile
// items, because compiling it would change that assembly's member set and with it the method
// tables the assembly-mode tests expect. The spread syntax here was verified against the F# 11
// compiler in the .NET 11 SDK.
module Sample.FSharp11

open System

type Config =
    { Host: string
      Port: int }

type Service =
    { Name: string
      Config: Config }

let describe service =
    $"service {service.Name} on {service.Config.Host}:{service.Config.Port}"

// A spread source must have a known nominal record type, hence the annotation.
let relocate host (service: Service) =
    { ...service; Config = { ...service.Config; Host = host } }

let build name host port = { Name = name; Config = { Host = host; Port = port } }

let register (invoke: Action<string>) (service: Service) =
    invoke.Invoke(service.Name)
    service
