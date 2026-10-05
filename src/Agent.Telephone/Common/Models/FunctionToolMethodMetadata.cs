using System.Reflection;
using Agent.Telephone.Abstractions.Common.Attributes;

namespace Agent.Telephone.Common.Models
{
    internal class FunctionToolMethodMetadata
    {
        public string FunctionName { get; set; } = string.Empty;
        public string? Description { get; set; }
        public ToolBehaviorAttribute? Behavior { get; set; }
        public Type[] ParameterTypes { get; set; } = Array.Empty<Type>();
        public Type ReturnType { get; set; } = typeof(void);
        public MethodInfo Method { get; set; } = null!;
    }
}
