using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace HelloWorld
{
    // Generic interface
    public interface IGenericInterface<T>
    {
        T Process(T item);
    }

    public interface ITestInterface
    {
        void InterfaceMethod();
    }

    public interface IAnotherInterface
    {
        int InterfaceProperty { get; set; }
    }

    public class BaseClass
    {
        public virtual void BaseMethod()
        {
        }
    }

    // Generic class implementing a generic interface
    public class GenericProcessor<T> : IGenericInterface<T>
    {
        public T Value { get; set; }

        public GenericProcessor(T value)
        {
            Value = value;
        }

        public T Process(T item)
        {
            return item;
        }

        // Generic method within a generic class
        public TResult ConvertTo<TResult>(T input)
        {
            return (TResult)(object)input;
        }
    }

    // Non-generic class with a generic method
    public class Utility
    {
        public static T GetDefault<T>()
        {
            return default(T);
        }

        // Generic method with constraints
        public static void Swap<T>(ref T a, ref T b) where T : class
        {
            T temp = a;
            a = b;
            b = temp;
        }
    }

    public class Hello : BaseClass, ITestInterface
    {         
        public static void elevate()
        {
            
        }

        public async Task Appreciate()
        {
            await Task.Delay(0);
        }

        public void InterfaceMethod()
        {
        }

        public override void BaseMethod()
        {
        }

        // Instance method using generics
        public List<string> GetNames()
        {
            return new List<string> { "Alice", "Bob" };
        }
    }

    public class World : ITestInterface, IAnotherInterface
    {
        public int InterfaceProperty { get; set; }

        public void shout()
        {
            
        }

        public void InterfaceMethod()
        {
        }

        private void PrivateMethod()
        {
            
        }
    }
}