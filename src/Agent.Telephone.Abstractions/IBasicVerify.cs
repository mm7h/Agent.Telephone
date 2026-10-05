using System.Net;

namespace Agent.Telephone.Abstractions
{
    public interface IBasicVerify
    {
        /// <summary>
        /// 客户端验证。
        /// </summary>
        /// <param name="DialingNumber">来电号码</param>
        /// <param name="userEndPoint">用户登录Remote来源地址</param>
        /// <returns>是否允许设备连接到服务器</returns>
        bool Verify(string dialingNumber, IPEndPoint userEndPoint);
    }
}
