namespace Agent.Telephone.Abstractions.Common.Enums
{
    /// <summary>
    /// Telephone keypad keys that can be assigned to a function tool.
    /// The values are flags rather than RTP telephone-event payload values.
    /// </summary>
    [Flags]
    public enum DtmfKey
    {
        None = 0,
        Zero = 1 << 0,
        One = 1 << 1,
        Two = 1 << 2,
        Three = 1 << 3,
        Four = 1 << 4,
        Five = 1 << 5,
        Six = 1 << 6,
        Seven = 1 << 7,
        Eight = 1 << 8,
        Nine = 1 << 9,
        Star = 1 << 10,
        Pound = 1 << 11,
    }
}
