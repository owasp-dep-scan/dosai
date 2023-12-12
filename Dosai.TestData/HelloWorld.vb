Imports System.Globalization
Imports System.Reflection
Imports System.Threading.Tasks

Namespace HelloWorld
    Public Class Hello
        Public Shared Sub elevate()
        End Sub

        Public Async Function Appreciate() As Task
            Await Task.Delay(0)
        End Function
    End Class

    Public Class World
        Public Sub shout()
        End Sub

        Private Sub PrivateMethod()
        End Sub
    End Class
End Namespace