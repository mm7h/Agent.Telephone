using Agent.Telephone.Abstractions.Configs;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class TelephoneConfigValidatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-telephone-config-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void ValidConfigurationHasNoErrors()
    {
        Directory.CreateDirectory(this._root);
        var promptPath = Path.Combine(this._root, "busy.wav");
        File.WriteAllBytes(promptPath, [1, 2, 3]);
        TelephoneConfig config = CreateValidConfig();
        config.PromptMediaConfigs[486] = promptPath;

        Assert.Empty(TelephoneConfigValidator.Validate(config));
        TelephoneConfigValidator.ValidateAndThrow(config);
    }

    [Fact]
    public void EmptyAndDuplicateDialingNumbersAreRejected()
    {
        TelephoneConfig config = CreateValidConfig();
        config.AssistantConfigs.Add(CreateAssistant("  "));
        config.AssistantConfigs.Add(CreateAssistant("10086"));

        IReadOnlyList<string> errors = TelephoneConfigValidator.Validate(config);

        Assert.Contains(errors, error => error.Contains("DialingNumber is required", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("duplicated", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("VAD")]
    [InlineData("ASR")]
    [InlineData("Intent")]
    [InlineData("LLM")]
    [InlineData("TTS")]
    [InlineData("Memory")]
    public void UnknownProviderIsRejected(string providerKind)
    {
        TelephoneConfig config = CreateValidConfig();
        AssistantConfig assistant = config.AssistantConfigs[0];
        SetProvider(assistant, providerKind, "MissingProvider");

        IReadOnlyList<string> errors = TelephoneConfigValidator.Validate(config);

        Assert.Contains(
            errors,
            error => error.Contains($"unknown {providerKind} provider", StringComparison.Ordinal));
    }

    [Fact]
    public void UnknownAllowedToolIsRejectedAgainstAvailableNames()
    {
        TelephoneConfig config = CreateValidConfig();
        config.AssistantConfigs[0].AllowedTools = ["SwitchAssistantAsync", "MissingTool"];

        IReadOnlyList<string> errors = TelephoneConfigValidator.ValidateAllowedTools(
            config.AssistantConfigs,
            ["SwitchAssistantAsync"]);

        string error = Assert.Single(errors);
        Assert.Contains("MissingTool", error, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyAssistantUnavailablePromptMediaPathIsAllowed()
    {
        TelephoneConfig config = CreateValidConfig();
        config.PromptMediaConfigs[480] = string.Empty;

        Assert.Empty(TelephoneConfigValidator.Validate(config));
    }

    [Fact]
    public void NonEmptyMissingAssistantUnavailablePromptMediaPathIsRejected()
    {
        TelephoneConfig config = CreateValidConfig();
        config.PromptMediaConfigs[480] = Path.Combine(this._root, "missing.wav");

        IReadOnlyList<string> errors = TelephoneConfigValidator.Validate(config);

        Assert.Contains(
            errors,
            error => error.Contains("PromptMediaConfigs[480]", StringComparison.Ordinal));
    }

    [Fact]
    public void AssistantSwitchingRingbackIsRequiredWhenAssistantSwitchingIsEnabled()
    {
        TelephoneConfig config = CreateValidConfig();
        config.AssistantConfigs[0].AllowedTools = ["SwitchAssistantAsync"];

        IReadOnlyList<string> errors = TelephoneConfigValidator.Validate(config);

        Assert.Contains(
            errors,
            error => error.Contains(
                "PromptMediaConfigs[180] (AssistantSwitchingRingback) is required",
                StringComparison.Ordinal));
    }

    [Fact]
    public void ExistingAssistantSwitchingRingbackIsAcceptedWhenAssistantSwitchingIsEnabled()
    {
        Directory.CreateDirectory(this._root);
        string ringbackPath = Path.Combine(this._root, "ringback.wav");
        File.WriteAllBytes(ringbackPath, [1, 2, 3]);
        TelephoneConfig config = CreateValidConfig();
        config.AssistantConfigs[0].AllowedTools = ["SwitchAssistantAsync"];
        config.PromptMediaConfigs[180] = ringbackPath;

        Assert.Empty(TelephoneConfigValidator.Validate(config));
    }

    [Fact]
    public void MissingAssistantSwitchingRingbackIsRejectedWhenAssistantSwitchingIsEnabled()
    {
        TelephoneConfig config = CreateValidConfig();
        config.AssistantConfigs[0].AllowedTools = ["SwitchAssistantAsync"];
        config.PromptMediaConfigs[180] = Path.Combine(this._root, "missing.wav");

        IReadOnlyList<string> errors = TelephoneConfigValidator.Validate(config);

        Assert.Contains(
            errors,
            error => error.Contains("PromptMediaConfigs[180]", StringComparison.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(this._root))
        {
            Directory.Delete(this._root, true);
        }
    }

    private static TelephoneConfig CreateValidConfig()
    {
        var configuredSettings = new Dictionary<string, Dictionary<string, Dictionary<string, string>>>
        {
            ["VAD"] = CreateProviderSettings("VadProvider"),
            ["ASR"] = CreateProviderSettings("AsrProvider"),
            ["Intent"] = CreateProviderSettings("IntentProvider"),
            ["LLM"] = CreateProviderSettings("LlmProvider"),
            ["TTS"] = CreateProviderSettings("TtsProvider"),
            ["Memory"] = CreateProviderSettings("MemoryProvider")
        };

        return new TelephoneConfig
        {
            SIPConfig = new SIPConfig(),
            ModelConfig = new ModelConfig
            {
                ConfiguredSettings = configuredSettings
            },
            AssistantConfigs = [CreateAssistant("10086")],
            PromptMediaConfigs = new Dictionary<int, string>()
        };
    }

    private static Dictionary<string, Dictionary<string, string>> CreateProviderSettings(string name) =>
        new()
        {
            [name] = []
        };

    private static AssistantConfig CreateAssistant(string dialingNumber) => new()
    {
        DialingNumber = dialingNumber,
        Name = "Assistant",
        VAD = "VadProvider",
        ASR = "AsrProvider",
        Intent = "IntentProvider",
        LLM = "LlmProvider",
        TTS = "TtsProvider",
        Memory = "MemoryProvider"
    };

    private static void SetProvider(
        AssistantConfig assistant,
        string providerKind,
        string value)
    {
        switch (providerKind)
        {
            case "VAD":
                assistant.VAD = value;
                break;
            case "ASR":
                assistant.ASR = value;
                break;
            case "Intent":
                assistant.Intent = value;
                break;
            case "LLM":
                assistant.LLM = value;
                break;
            case "TTS":
                assistant.TTS = value;
                break;
            case "Memory":
                assistant.Memory = value;
                break;
        }
    }
}
