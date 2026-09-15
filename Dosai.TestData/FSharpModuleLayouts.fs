// FSharpModuleLayouts.fs
// F# layout coverage for the line frontend: a `module X =` declaration with a fully indented
// body (the common real-project shape, unlike a flat script). Module-level `let` bindings that
// follow `type` declarations must be attributed to the module, not the preceding type - at the
// body's indentation, not just at column 0. The prime identifiers (`x'`, `list'`) are legal F#
// and must not be read as unterminated char literals (which would swallow the rest of the
// line), while real char literals (`'-'`, `'\n'`) must still be honored.
module Sample.Indented =

    open System

    type Counter =
        member this.Next() = 1

    let transform (input: string) =
        input.Trim()

    let x' = transform "seed"

    let list' = [ x'; transform "other" ]

    let dash = '-'
    let newline = '\n'

    let eval () =
        Console.WriteLine(list'.Length + (int dash + (int newline)))
