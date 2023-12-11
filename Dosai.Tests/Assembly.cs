using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

// Example assembly that can be used in unit tests
namespace HelloWorld
{
    public class Hello
    {         
        public static void elevate()
        {
            
        }

        public async Task Appreciate()
        {
            await Task.Delay(0);
        }
    }

    public class World
    {
        public void shout()
        {
            
        }

        private void PrivateMethod()
        {
            
        }
    }
}

namespace FooBar
{
    public class Foo
    {         
        public void foo()
        {
            
        }
    }

    public class Bar
    {
        public void bar()
        {
            
        }

        private void PrivateMethod()
        {
            
        }
    }
}