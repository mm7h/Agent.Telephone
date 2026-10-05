using System.Collections.Concurrent;
using Agent.Telephone.Common.Contexts;
using Microsoft.Extensions.AI;

namespace Agent.Telephone.Providers.LLM.Contexts
{
    internal static class ToolExecutionPromptDispatcher
    {
        private static readonly ConcurrentDictionary<string, PromptRegistration> s_registrations = new(StringComparer.Ordinal);

        public static Guid Register(string deviceId, PrivateProvider privateProvider, Func<string, CancellationToken, Task> callback)
        {
            Dictionary<string, string> prompts = new(StringComparer.OrdinalIgnoreCase);
            foreach (AIFunction function in privateProvider.FunctionTools.OfType<AIFunction>())
            {
                if (privateProvider.TryGetFunctionToolRegistration(function.Name, out FunctionToolRegistration? registration)
                    && !string.IsNullOrWhiteSpace(registration?.PreExecutionPrompt))
                {
                    prompts[function.Name] = registration.PreExecutionPrompt;
                }
            }

            // 每台设备同时只允许一通活动电话；新一轮对话替换该设备的提示回调。
            Guid id = Guid.NewGuid();
            s_registrations[deviceId] = new PromptRegistration(id, prompts, callback);
            return id;
        }

        public static Task DispatchAsync(string deviceId, string functionName, CancellationToken token)
        {
            return s_registrations.TryGetValue(deviceId, out PromptRegistration? registration)
                && registration.Prompts.TryGetValue(functionName, out string? prompt)
                ? registration.Callback(prompt, token)
                : Task.CompletedTask;
        }

        public static void Unregister(string deviceId, Guid id)
        {
            if (s_registrations.TryGetValue(deviceId, out PromptRegistration? registration) && registration.Id == id)
            {
                ((ICollection<KeyValuePair<string, PromptRegistration>>)s_registrations)
                    .Remove(new KeyValuePair<string, PromptRegistration>(deviceId, registration));
            }
        }

        private sealed record PromptRegistration(Guid Id, Dictionary<string, string> Prompts, Func<string, CancellationToken, Task> Callback);
    }
}
