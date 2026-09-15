// FSharp11MoreFeatures.fs
// F# 11 source (ships with .NET 11) exercising the features beyond record spreads: record
// constructors (`Point(0, 0)` positional and `Point(Y = 20, X = 10)` named), direct delegate
// construction (`Func<int, int, int>(Calculator.Add)` without an intermediate lambda), the
// efficient interpolated strings, `Async.RunSynchronouslyImmediate`, and `<inheritdoc/>`
// overrides. Module-level `let` bindings that follow `type` declarations must be attributed
// to the module, not the preceding type, and the comment prose (full of `word (paren)`
// shapes) must not surface as phantom method calls.
//
// Source-mode fixture only: it is deliberately not in Dosai.TestData.FSharp.fsproj's Compile
// items, because compiling it would change that assembly's member set and with it the method
// tables the assembly-mode tests expect.
module Sample.FSharp11More

open System

type Point =
    { X: int
      Y: int }

type Calculator =
    static member Add x y = x + y

// Record constructors (F# 11).
let origin = Point(0, 0)
let offset = Point(Y = 20, X = 10)

// Direct delegate construction (F# 11): no intermediate closure allocation.
let add = Func<int, int, int>(Calculator.Add)

// Efficient interpolated strings (F# 11) lower to String.Concat.
let describe (point: Point) = $"({point.X}, {point.Y})"

let compute () =
    // Async.RunSynchronouslyImmediate (F# 11) starts on the calling thread.
    let work =
        async {
            do! Async.Sleep 10
            return add.Invoke(origin.X, offset.Y)
        }
    Async.RunSynchronouslyImmediate work

// <inheritdoc/> (F# 11) resolves inherited docs; the override itself must not read as a call.
type ServiceBase() =
    abstract Status: unit -> string

type Service() =
    inherit ServiceBase()
    override _.Status() = "Ready"

let summarize (service: Service) =
    describe origin + service.Status()
