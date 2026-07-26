using Agent.Telephone.Abstractions.Common.Attributes;
using Agent.Telephone.Abstractions.Common.Contexts;
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

        [ToolBehavior(ToolAction.DirectResponse)]
        public FunctionReturn<string> GetWeatherInfo(string city)
        {
            // Here you would implement the logic to get weather information for the specified city.
            // For demonstration purposes, we'll return a mock response.
            string weatherInfo = $"The current weather in {city} is sunny with a temperature of 25°C.";
            this.Logger.LogInformation($"Retrieved weather information for {city}: {weatherInfo}");
            return new FunctionReturn<string>
            {
                Result = weatherInfo,
                Response = weatherInfo
            };
        }
    }
}
