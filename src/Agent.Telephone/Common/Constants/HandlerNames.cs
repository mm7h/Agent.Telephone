using Agent.Telephone.Handlers.SIPHandlers;
using Agent.Telephone.Handlers.AIAdapterHandlers;

namespace Agent.Telephone.Common.Constants
{
    internal static class HandlerNames
    {
        public const string ActiveCallHandlerName = nameof(ActiveCallHandler);
        public const string RTPHandlerName = nameof(RTPHandler);

        public const string AudioReceivedHandlerName = nameof(AudioReceivedHandler);
        public const string Audio2TextHandlerName = nameof(Audio2TextHandler);
        public const string DialogueHandlerName = nameof(DialogueHandler);
        public const string Text2AudioHandlerName = nameof(Text2AudioHandler);
        public const string AudioSendHandlerName = nameof(AudioSendHandler);
    }
}
