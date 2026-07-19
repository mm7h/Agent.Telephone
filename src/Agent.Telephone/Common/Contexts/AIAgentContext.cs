using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agent.Telephone.Common.Contexts
{
    internal class AIAgentContext
    {
        public AIAgentContext()
        {
            
        }

        public string? CurrentPhoneNumber { get; set; }
        public string? LastCalledPhoneNumber { get; set; }


    }
}
