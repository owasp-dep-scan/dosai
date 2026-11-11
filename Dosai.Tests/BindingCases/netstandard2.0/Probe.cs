using System;
using System.Text;

namespace BindingProbe
{
    public static class Probe
    {
        public static string Run()
        {
            var builder = new StringBuilder();
            builder.Append("probe");
            Console.WriteLine(builder.ToString());
            return Describe(builder);
        }

        private static string Describe(StringBuilder builder)
        {
            return builder.ToString();
        }
    }
}
