// FSharpScript.fsx
// F# script exercising the `#r`/`#load` directives that scripts use in place of project
// references: a NuGet package reference with a pinned version, a plain assembly reference,
// and a loaded script file. All three must surface as dependencies.
#r "nuget: Newtonsoft.Json, 13.0.3"
#r "System.Xml"
#load "Helper.fsx"

open System

let run (path: string) =
    IO.File.ReadAllText(path)
