using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
using System.ComponentModel;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.FunctionTools;
using Microsoft.Extensions.Logging;

namespace Agent.Telephone.Sample.Server.FunctionTools
{
    internal class GetWeather : PrivateFunctionTool
    {
        public override ValueTask OnFunctionToolInitializedAsync()
        {
            this.Logger.LogInformation("GetWeather function tool initialized.");
            return base.OnFunctionToolInitializedAsync();
        }
        public override ValueTask OnFunctionToolReleasedAsync()
        {
            this.Logger.LogInformation("GetWeather function tool released.");
            return base.OnFunctionToolReleasedAsync();
        }
        public override ValueTask OnDeviceConnectedAsync()
        {
            this.Logger.LogInformation("Session connected to GetWeather function tool.");
            return base.OnDeviceConnectedAsync();
        }
        public override ValueTask OnDeviceClosedAsync()
        {
            this.Logger.LogInformation("Session closed from GetWeather function tool.");
            return base.OnDeviceClosedAsync();
        }

        [Description("查询指定城市的天气。调用前先告知用户：确认查询请按1，取消请按2，重新播报菜单请按星号键，结束本次操作请按井号键。")]
        [ToolBehavior(ToolAction.DirectResponse,
            DtmfKeys = DtmfKey.One | DtmfKey.Two | DtmfKey.Star | DtmfKey.Pound)]
        public FunctionReturn<string> GetWeatherInfo(string city, DtmfInputResult dtmfInput)
        {
            if (dtmfInput.SelectedKey != DtmfKey.One)
            {
                string response = dtmfInput.Status switch
                {
                    DtmfInputStatus.TimedOut => "您的按键已超时，请重新再试。",
                    DtmfInputStatus.Accepted when dtmfInput.SelectedKey == DtmfKey.Two => "已取消查询天气。",
                    DtmfInputStatus.Accepted when dtmfInput.SelectedKey == DtmfKey.Star => "查询天气请按1，取消请按2，重新播报菜单请按星号键，结束请按井号键。",
                    DtmfInputStatus.Accepted when dtmfInput.SelectedKey == DtmfKey.Pound => "本次天气查询已结束。",
                    _ => dtmfInput.Message ?? "当前无法查询天气。",
                };
                return new FunctionReturn<string>
                {
                    Result = response,
                    Response = response,
                };
            }

            // Here you would implement the logic to get weather information for the specified city.
            // For demonstration purposes, we'll return a mock response.
            string weatherInfo = $"The current weather in {city} is sunny with a temperature of 25°C.";
            this.Logger.LogInformation("Retrieved weather information for {City}: {WeatherInfo}", city, weatherInfo);
            return new FunctionReturn<string>
            {
                Result = weatherInfo,
                Response = weatherInfo
            };
        }
    }
}
