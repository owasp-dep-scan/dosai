using System.Globalization;
using System.Reflection;
using System.Threading.Tasks;

namespace HelloWorld
{
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