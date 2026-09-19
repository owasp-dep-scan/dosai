// FSharp11Language.fs
// F# 11 source exercising the language-level additions beyond record spreads and delegates:
// the `#elif` preprocessor directive (FS-1334), conditional regions with compound conditions
// (`#if !DEBUG`), and `#:`-prefixed file-based app directives, which the F# 11 compiler
// ignores wherever they appear (FS-1337) - none of them may surface as phantom declarations or
// calls. Type-level record spreads (`type Labeled = { ...Config; Label: string }`) extend the
// expression spreads of FSharp11Features.fs, and nested dotted field updates
// (`{ ...service; Config.Host = host }`) update one field of a nested record in place.
//
// With no compilation symbols defined - the same empty define set the F# compiler uses for a
// Release build - only the `#else` branch of the first region and the `#if !DEBUG` branch of
// the second contribute declarations and calls.
//
// Source-mode fixture only: it is deliberately not in Dosai.TestData.FSharp.fsproj's Compile
// items, because compiling it would change that assembly's member set and with it the method
// tables the assembly-mode tests expect. The syntax here was verified against the F# 11
// compiler in the .NET 11 SDK.
#:property Define=NET11
#:package FSharp.Control.AsyncSeq@1.1.1

module Sample.FSharp11Language

open System

type Config =
    { Host: string
      Port: int }

// Type-level record spread (F# 11): Labeled has Host, Port, and Label.
type Labeled =
    { ...Config
      Label: string }

// Nested record for the dotted update form. The field is named `Opts` rather than `Config`
// because a field whose name equals its type's name makes the dotted update ambiguous.
type Service =
    { Name: string
      Opts: Config }

#if DEBUG
let configure mode = Diagnostics.Debug.WriteLine(mode)
#elif TRACE
let configure mode = Diagnostics.Trace.WriteLine(mode)
#else
let configure mode = ignore mode
#endif

#if !DEBUG
let releaseNotes = "release"
#endif

let annotate (config: Config) =
    {| ...config
       Label = "primary" |}

let label (labeled: Labeled) = $"{labeled.Label}:{labeled.Host}:{labeled.Port}"

let relocate host (service: Service) =
    { ...service
      Opts.Host = host }

let build name host port =
    { Name = name
      Opts = { Host = host; Port = port } }
