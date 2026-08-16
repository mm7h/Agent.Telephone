namespace Agent.Telephone.Abstractions.Common.Enums
{
    /// <summary>
    /// 可分配给功能工具的电话键盘按键。
    /// 支持多个按键的组合，使用按位或运算符进行组合。
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
