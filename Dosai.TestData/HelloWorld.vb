Imports System.Globalization
Imports System.Reflection
Imports System.Threading.Tasks

Namespace HelloWorld
    Public Interface ITestInterface
        Sub InterfaceMethod()
    End Interface

    Public Interface IAnotherInterface
        Property InterfaceProperty As Integer
    End Interface

    Public Class BaseClass
        Public Overridable Sub BaseMethod()
        End Sub
    End Class

    Public Class Hello
        Inherits BaseClass
        Implements ITestInterface

        Public Shared Sub elevate()
        End Sub

        Public Async Function Appreciate() As Task
            Await Task.Delay(0)
        End Function

        Public Sub InterfaceMethod() Implements ITestInterface.InterfaceMethod
        End Sub

        Public Overrides Sub BaseMethod()
        End Sub
    End Class

    Public Class World
        Implements ITestInterface, IAnotherInterface

        Public Property InterfaceProperty As Integer Implements IAnotherInterface.InterfaceProperty

        Public Sub shout()
        End Sub

        Public Sub InterfaceMethod() Implements ITestInterface.InterfaceMethod
        End Sub

        Private Sub PrivateMethod()
        End Sub
    End Class
End Namespace