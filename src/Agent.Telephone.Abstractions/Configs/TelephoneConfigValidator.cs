namespace Agent.Telephone.Abstractions.Configs
{
    public static class TelephoneConfigValidator
    {
        private const int AssistantSwitchingRingbackCode = 180;

        private static readonly string[] ProviderKinds =
        [
            "VAD",
            "ASR",
            "Intent",
            "LLM",
            "TTS",
            "Memory"
        ];

        public static IReadOnlyList<string> Validate(TelephoneConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);

            var errors = new List<string>();
            ValidateSip(config.SIPConfig, errors);
            ValidateMessageStore(config.MessageStoreConfig, errors);
            ValidatePromptMedia(config.PromptMediaConfigs, config.AssistantConfigs, errors);
            ValidateAssistants(config.AssistantConfigs, config.ModelConfig, errors);
            return errors;
        }

        public static IReadOnlyList<string> ValidateAllowedTools(
            IEnumerable<AssistantConfig> assistants,
            IEnumerable<string> availableTools)
        {
            ArgumentNullException.ThrowIfNull(assistants);
            ArgumentNullException.ThrowIfNull(availableTools);

            var available = availableTools
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var errors = new List<string>();
            foreach (AssistantConfig? assistant in assistants)
            {
                if (assistant is null)
                {
                    continue;
                }

                foreach (string? allowedTool in assistant.AllowedTools ?? [])
                {
                    if (string.IsNullOrWhiteSpace(allowedTool) || !available.Contains(allowedTool))
                    {
                        errors.Add(
                            $"Assistant '{assistant.DialingNumber}' references unknown tool '{allowedTool}'.");
                    }
                }
            }

            return errors;
        }

        public static void ValidateAndThrow(TelephoneConfig config)
        {
            IReadOnlyList<string> errors = Validate(config);
            if (errors.Count > 0)
            {
                throw new TelephoneConfigValidationException(errors);
            }
        }

        private static void ValidateSip(SIPConfig? config, ICollection<string> errors)
        {
            if (config is null)
            {
                errors.Add("SIPConfig is required.");
                return;
            }

            if (config.Port is < 1 or > 65535)
            {
                errors.Add("SIPConfig.Port must be between 1 and 65535.");
            }
            ValidatePositive(config.HangUpTimeoutSeconds, "SIPConfig.HangUpTimeoutSeconds", errors);
            ValidatePositive(
                config.AgentInitializationTimeoutSeconds,
                "SIPConfig.AgentInitializationTimeoutSeconds",
                errors);
            ValidatePositive(config.CallbackTimeoutSeconds, "SIPConfig.CallbackTimeoutSeconds", errors);
        }

        private static void ValidateMessageStore(
            MessageStoreConfig? config,
            ICollection<string> errors)
        {
            if (config is null)
            {
                errors.Add("MessageStoreConfig is required.");
                return;
            }

            if (string.IsNullOrWhiteSpace(config.RootPath))
            {
                errors.Add("MessageStoreConfig.RootPath is required.");
            }
            else
            {
                try
                {
                    _ = Path.GetFullPath(config.RootPath);
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                        or NotSupportedException
                        or PathTooLongException)
                {
                    errors.Add("MessageStoreConfig.RootPath contains an invalid directory path.");
                }
            }
            ValidatePositive(config.RetentionDays, "MessageStoreConfig.RetentionDays", errors);
            ValidatePositive(
                config.MaxMessagesPerConversation,
                "MessageStoreConfig.MaxMessagesPerConversation",
                errors);
            ValidatePositive(
                config.RecentConversationTurns,
                "MessageStoreConfig.RecentConversationTurns",
                errors);
        }

        private static void ValidatePromptMedia(
            IDictionary<int, string>? config,
            IEnumerable<AssistantConfig>? assistants,
            ICollection<string> errors)
        {
            if (config is null)
            {
                errors.Add("PromptMediaConfigs is required.");
                return;
            }

            foreach (KeyValuePair<int, string> entry in config)
            {
                if (entry.Key is < 100 or > 699)
                {
                    errors.Add(
                        $"PromptMediaConfigs contains an invalid SIP response code '{entry.Key}'.");
                    continue;
                }

                ValidateOptionalFile(
                    entry.Value,
                    $"PromptMediaConfigs[{entry.Key}]",
                    errors);
            }

            if (UsesAssistantSwitching(assistants))
            {
                bool hasRingback = config.TryGetValue(
                    AssistantSwitchingRingbackCode,
                    out string? ringbackPath);
                ValidateRequiredFile(
                    hasRingback ? ringbackPath : null,
                    $"PromptMediaConfigs[{AssistantSwitchingRingbackCode}] (AssistantSwitchingRingback)",
                    errors);
            }
        }

        private static bool UsesAssistantSwitching(IEnumerable<AssistantConfig>? assistants)
        {
            return assistants?.Any(assistant =>
                assistant?.AllowedTools?.Any(allowedTool =>
                    string.Equals(
                        allowedTool?.Trim(),
                        "SwitchAssistantAsync",
                        StringComparison.OrdinalIgnoreCase)) == true) == true;
        }

        private static void ValidateAssistants(
            IEnumerable<AssistantConfig>? assistants,
            ModelConfig? modelConfig,
            ICollection<string> errors)
        {
            if (assistants is null)
            {
                errors.Add("AssistantConfigs is required.");
                return;
            }
            if (modelConfig is null)
            {
                errors.Add("ModelConfig is required.");
                return;
            }

            var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (AssistantConfig? assistant in assistants)
            {
                if (assistant is null)
                {
                    errors.Add($"AssistantConfigs[{index}] cannot be null.");
                    index++;
                    continue;
                }

                string dialingNumber = assistant.DialingNumber?.Trim() ?? string.Empty;
                if (dialingNumber.Length == 0)
                {
                    errors.Add($"AssistantConfigs[{index}].DialingNumber is required.");
                }
                else if (!numbers.Add(dialingNumber))
                {
                    errors.Add($"Assistant DialingNumber '{dialingNumber}' is duplicated.");
                }

                foreach (string providerKind in ProviderKinds)
                {
                    string providerName = GetProviderName(assistant, providerKind);
                    ValidateProvider(
                        dialingNumber,
                        providerKind,
                        providerName,
                        modelConfig,
                        errors);
                }

                index++;
            }
        }

        private static void ValidateProvider(
            string dialingNumber,
            string providerKind,
            string providerName,
            ModelConfig modelConfig,
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(providerName))
            {
                errors.Add(
                    $"Assistant '{dialingNumber}' must configure {providerKind}.");
                return;
            }

            if (modelConfig.ConfiguredSettings is null
                || !modelConfig.ConfiguredSettings.TryGetValue(providerKind, out var providers)
                || providers is null
                || !providers.ContainsKey(providerName))
            {
                errors.Add(
                    $"Assistant '{dialingNumber}' references unknown {providerKind} provider '{providerName}'.");
            }
        }

        private static string GetProviderName(AssistantConfig assistant, string providerKind) =>
            providerKind switch
            {
                "VAD" => assistant.VAD,
                "ASR" => assistant.ASR,
                "Intent" => assistant.Intent,
                "LLM" => assistant.LLM,
                "TTS" => assistant.TTS,
                "Memory" => assistant.Memory,
                _ => string.Empty
            };

        private static void ValidateOptionalFile(
            string? path,
            string propertyName,
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                if (!File.Exists(Path.GetFullPath(path)))
                {
                    errors.Add($"{propertyName} must reference an existing file.");
                }
            }
            catch (Exception exception) when (
                exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
            {
                errors.Add($"{propertyName} contains an invalid file path.");
            }
        }

        private static void ValidateRequiredFile(
            string? path,
            string propertyName,
            ICollection<string> errors)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                errors.Add($"{propertyName} is required when SwitchAssistantAsync is enabled.");
                return;
            }

            ValidateOptionalFile(path, propertyName, errors);
        }

        private static void ValidatePositive(
            int value,
            string propertyName,
            ICollection<string> errors)
        {
            if (value <= 0)
            {
                errors.Add($"{propertyName} must be greater than zero.");
            }
        }
    }
}
