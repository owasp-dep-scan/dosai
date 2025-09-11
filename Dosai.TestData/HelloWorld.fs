// HelloWorld.fs
open System

// Module with functions
module Greeter =
    let hello name = 
        printfn "Hello, %s!" name
        sprintf "Hello, %s!" name
    
    let goodbye name = 
        printfn "Goodbye, %s!" name
        sprintf "Goodbye, %s!" name

// Simple function outside module
let add x y = x + y

// Type with members
type Person(name: string, age: int) =
    member this.Name = name
    member this.Age = age
    member this.Introduce() = 
        printfn "Hi, I'm %s and I'm %d years old." this.Name this.Age
    member this.CelebrateBirthday() = 
        Person(this.Name, this.Age + 1)

// Main entry point
[<EntryPoint>]
let main argv =
    printfn "Welcome to F#"
    
    // Using module function
    Greeter.hello "World" |> ignore
    
    // Using simple function
    let result = add 5 3
    printfn "5 + 3 = %d" result
    
    // Using type and members
    let person = Person("Alice", 30)
    person.Introduce()
    
    let olderPerson = person.CelebrateBirthday()
    printfn "%s is now %d years old" olderPerson.Name olderPerson.Age
    
    0 // Return success code