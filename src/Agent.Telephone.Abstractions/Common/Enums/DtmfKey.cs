namespace Agent.Telephone.Abstractions.Common.Enums
{
    /// <summary>
    /// 可分配给功能工具的电话键盘按键。
    /// 支持多个按键的组合，使用按位或运算符进行组合。
    /// </summary>
    [Flags]
    public enum DtmfKey
    {
        /// <summary>
        /// 无
        /// </summary>
        None = 0,

        /// <summary>
        /// 按键 0
        /// </summary>
        /// 
        Zero = 1 << 0,

        /// <summary>
        /// 按键 1
        /// </summary>
        /// 
        One = 1 << 1,

        /// <summary>
        /// 按键 2
        /// </summary>
        Two = 1 << 2,

        /// <summary>
        /// 按键 3
        /// </summary>
        Three = 1 << 3,

        /// <summary>
        /// 按键 4
        /// </summary>
        Four = 1 << 4,

        /// <summary>
        /// 按键 5
        /// </summary>
        Five = 1 << 5,

        /// <summary>
        /// 按键 6
        /// </summary>
        Six = 1 << 6,

        /// <summary>
        /// 按键 7
        /// </summary>
        Seven = 1 << 7,

        /// <summary>
        /// 按键 8
        /// </summary>
        Eight = 1 << 8,

        /// <summary>
        /// 按键 9
        /// </summary>
        Nine = 1 << 9,

        /// <summary>
        /// 按键 *
        /// </summary>
        Star = 1 << 10,

        /// <summary>
        /// 按键 #
        /// </summary>
        Pound = 1 << 11,
    }
}
