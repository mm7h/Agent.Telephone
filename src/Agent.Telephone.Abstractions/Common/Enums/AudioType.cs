using System.ComponentModel;

namespace Agent.Telephone.Abstractions.Common.Enums
{
    [Flags]
    public enum AudioType
    {
        [Description("None")]
        None = 0,

        [Description("Music")]
        Music = 2,

        [Description("TTS")]
        TTS = 4,

        [Description("System Notification")]
        SystemNotification = 8,

        [Description("Other")]
        Other = 99
    }
}
