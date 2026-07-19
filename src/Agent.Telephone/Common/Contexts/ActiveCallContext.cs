using SIPSorcery.Media;
using SIPSorcery.Net;
using SIPSorcery.SIP;
using SIPSorcery.SIP.App;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Agent.Telephone.Common.Contexts
{
    internal class ActiveCallContext
    {
        public ActiveCallContext(SIPTransport sipTransport)
        {
            this.UserAgent = new SIPUserAgent(sipTransport, SIPEndPoint.Empty, true);
            this.VoIPRTP = new VoIPMediaSession();
        }



        public SIPUserAgent UserAgent { get; }

        public VoIPMediaSession VoIPRTP { get; }
    }
}
