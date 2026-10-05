using System.Diagnostics;
using System.Text.Json;
using Agent.Telephone;
using Agent.Telephone.Abstractions;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Codex.AppServer;
using Agent.Telephone.Codex.Abstractions.Common.Configs;
using Agent.Telephone.Codex.Abstractions.Common.Enums;
using Agent.Telephone.Codex.Abstractions.Common.Models;
using Agent.Telephone.Codex.Abstractions.Functions;
using Agent.Telephone.Codex.FunctionTools;
using Agent.Telephone.Codex.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class CodexAssistantTests
{
    [Fact]
    public void CodexAssistantOptions_UsesPortableDefaultCommandAndPaths()
    {
        CodexAssistantOptions options = new();

        if (OperatingSystem.IsWindows() && options.ExecutablePath != "codex")
        {
            Assert.True(File.Exists(options.ExecutablePath));
            Assert.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin"), options.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            Assert.Equal("codex", options.ExecutablePath);
        }
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "data", "codex"), options.WorkingDirectory);
        Assert.Equal(Path.Combine(Environment.CurrentDirectory, "data", "codex", "threads.db"), options.ThreadDatabasePath);
    }

    [Fact]
    public async Task RunCodexTaskAsync_PersistsAndResumesWithoutPhoneHistoryAsync()
    {
        using Fixture fixture = new();
        const string Prompt = "中文任务 \"quotes\" & | %PATH%\n第二行";

        FunctionReturn<string> first = await fixture.CreateTool().RunCodexTaskAsync(Prompt);
        CodexConversationId firstId = (await fixture.Store.GetConversationIdAsync("sip:user@one.test", "100", default))!;

        Assert.Equal("FINAL:" + Prompt, first.Result);
        Assert.DoesNotContain("thread/resume", fixture.ReadMethods());
        Assert.DoesNotContain("--ephemeral", fixture.ReadArguments());
        Assert.DoesNotContain(Prompt, fixture.ReadArguments());
        Assert.Equal(new[] { "app-server" }, fixture.ReadArguments());
        JsonElement start = fixture.ReadRequest("thread/start");
        Assert.Equal("test-model", start.GetProperty("params").GetProperty("model").GetString());
        Assert.Equal("never", start.GetProperty("params").GetProperty("approvalPolicy").GetString());
        Assert.Equal("read-only", start.GetProperty("params").GetProperty("sandbox").GetString());
        Assert.False(start.GetProperty("params").GetProperty("ephemeral").GetBoolean());
        Assert.Equal(Path.GetFullPath(fixture.Root), start.GetProperty("params").GetProperty("cwd").GetString());
        JsonElement firstTurn = fixture.ReadRequest("turn/start");
        Assert.Equal("high", firstTurn.GetProperty("params").GetProperty("effort").GetString());

        FunctionReturn<string> second = await fixture.CreateTool().RunCodexTaskAsync("追加要求");

        Assert.Equal("FINAL:追加要求", second.Result);
        Assert.Equal(second.Result, second.Response);
        JsonElement resume = fixture.ReadRequest("thread/resume");
        Assert.Equal(firstId.Value, resume.GetProperty("params").GetProperty("threadId").GetString());
        Assert.Equal("test-model", resume.GetProperty("params").GetProperty("model").GetString());
        Assert.Equal(Path.GetFullPath(fixture.Root), resume.GetProperty("params").GetProperty("cwd").GetString());
        Assert.Equal("追加要求", File.ReadAllText(Path.Combine(fixture.Root, "prompt.txt")));
        Assert.DoesNotContain("--last", fixture.ReadArguments());
    }

    [Fact]
    public async Task ExecuteAsync_UsesNormalizedConfiguredWorkingDirectoryAsync()
    {
        using Fixture fixture = new();
        CodexAssistantOptions options = fixture.CreateOptions();
        options.WorkingDirectory = Path.GetRelativePath(Environment.CurrentDirectory, fixture.Root);
        options.Validate();
        CodexAppServerFunction function = new(options);

        CodexFunctionResult result = await function.ExecuteAsync(new CodexFunctionRequest("test", "ignored"));

        Assert.Equal(CodexExecutionStatus.Succeeded, result.Status);
        Assert.Equal(Path.GetFullPath(fixture.Root), fixture.ReadRequest("thread/start").GetProperty("params").GetProperty("cwd").GetString());
    }

    [Fact]
    public void WithCodexAssistant_UsesRuntimeDataCodexDirectoryAndRegistersTool()
    {
        IServerBuilder builder = ServerBuilder.CreateServerBuilder(new HostBuilder());
        builder.WithCodexAssistant(options =>
        {
            options.ExecutablePath = "codex";
            options.ThreadDatabasePath = Path.Combine(Path.GetTempPath(), "codex-extension-tests", Guid.NewGuid().ToString("N"), "threads.db");
            options.ModelName = "test-model";
            options.ReasoningEffort = "medium";
        });

        using IHost host = builder.HostBuilder.Build();
        CodexAssistantOptions options = host.Services.GetRequiredService<CodexAssistantOptions>();

        Assert.Equal(Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "data", "codex")), options.WorkingDirectory);
        Assert.IsType<CodexAppServerFunction>(host.Services.GetRequiredService<ICodexFunction>());
        Assert.IsType<CodexAssistant>(host.Services.GetRequiredService<IEnumerable<IPrivateFunctionTool>>().Single());
    }

    [Fact]
    public async Task RunCodexTaskAsync_ExplicitNewTaskReplacesOnlyCurrentMappingAsync()
    {
        using Fixture fixture = new();
        await fixture.CreateTool().RunCodexTaskAsync("first");
        CodexConversationId? oldId = await fixture.Store.GetConversationIdAsync("sip:user@one.test", "100", default);

        await fixture.CreateTool().RunCodexTaskAsync("new task", startNewTask: true);

        CodexConversationId? newId = await fixture.Store.GetConversationIdAsync("sip:user@one.test", "100", default);
        Assert.NotEqual(oldId, newId);
        Assert.DoesNotContain("thread/resume", fixture.ReadMethods());
        await fixture.CreateTool().RunCodexTaskAsync("continue");
        Assert.Equal(newId!.Value, fixture.ReadRequest("thread/resume").GetProperty("params").GetProperty("threadId").GetString());
    }

    [Fact]
    public async Task RunCodexTaskAsync_IsolatesSipDomainsAndAssistantRolesAsync()
    {
        using Fixture fixture = new();
        await fixture.CreateTool().RunCodexTaskAsync("first");
        CodexConversationId? firstId = await fixture.Store.GetConversationIdAsync("sip:user@one.test", "100", default);
        await fixture.CreateTool("sip:user@two.test", "100").RunCodexTaskAsync("second");
        Assert.DoesNotContain("thread/resume", fixture.ReadMethods());
        await fixture.CreateTool("sip:user@one.test", "200").RunCodexTaskAsync("third");
        Assert.DoesNotContain("thread/resume", fixture.ReadMethods());
        await fixture.CreateTool().RunCodexTaskAsync("continue first");
        Assert.Equal(firstId!.Value, fixture.ReadRequest("thread/resume").GetProperty("params").GetProperty("threadId").GetString());
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("nonzero")]
    [InlineData("incomplete")]
    public async Task RunCodexTaskAsync_PreservesThreadAfterFailureAndDoesNotRetryAsync(string mode)
    {
        using Fixture fixture = new(mode);
        FunctionReturn<string> result = await fixture.CreateTool().RunCodexTaskAsync("test");

        Assert.Contains("任务未完成", result.Result);
        Assert.NotNull(await fixture.Store.GetConversationIdAsync("sip:user@one.test", "100", default));
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Root, "runs.txt")));
    }

    [Fact]
    public async Task RunCodexTaskAsync_CancellationKillsChildAndPreservesThreadAsync()
    {
        using Fixture fixture = new("wait");
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        Task<FunctionReturn<string>> execution = fixture.CreateTool().RunCodexTaskAsync("test", cancellationToken: cancellation.Token);
        while (await fixture.Store.GetConversationIdAsync("sip:user@one.test", "100", cancellation.Token) is null)
        {
            await Task.Delay(25, cancellation.Token);
        }

        using Process child = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Root, "pid.txt"))));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(child.HasExited);
        Assert.NotNull(await fixture.Store.GetConversationIdAsync("sip:user@one.test", "100", default));
    }

    [Fact]
    public async Task RunCodexTaskAsync_ConcurrentInstancesResumeSequentiallyAsync()
    {
        using Fixture fixture = new("slow");
        Task<FunctionReturn<string>> first = fixture.CreateTool().RunCodexTaskAsync("one");
        Task<FunctionReturn<string>> second = fixture.CreateTool().RunCodexTaskAsync("two");

        FunctionReturn<string>[] results = await Task.WhenAll(first, second);

        Assert.DoesNotContain("任务未完成", results[1].Result);
        Assert.Contains("thread/resume", fixture.ReadMethods());
        Assert.Equal(2, File.ReadAllLines(Path.Combine(fixture.Root, "runs.txt")).Length);
    }

    [Fact]
    public async Task RunCodexTaskAsync_EmptyPromptOrUnknownIdentityDoesNotExecuteAsync()
    {
        using Fixture fixture = new();

        Assert.Contains("没有收到", (await fixture.CreateTool().RunCodexTaskAsync(" ")).Result);
        Assert.Contains("未执行", (await fixture.CreateTool(null, "100").RunCodexTaskAsync("test")).Result);
        Assert.False(File.Exists(Path.Combine(fixture.Root, "runs.txt")));
    }

    [Fact]
    public async Task RunCodexTaskAsync_LogsCodexInvocationWithoutTaskPromptAsync()
    {
        using Fixture fixture = new();
        CapturingLogger logger = new();
        const string TaskPrompt = "不得写入日志的任务文本";

        await fixture.CreateTool(logger: logger).RunCodexTaskAsync(TaskPrompt);

        Assert.Equal("[CodexInvocation] 开始调用 Codex CLI，任务模式：新建。", logger.Message);
        Assert.DoesNotContain(TaskPrompt, logger.Message);
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "telephone-codex-test-" + Guid.NewGuid().ToString("N"));

        public Fixture(string mode = "success")
        {
            if (!OperatingSystem.IsWindows())
            {
                throw Xunit.Sdk.SkipException.ForSkip("The fake Codex app-server fixture uses Windows PowerShell.");
            }

            Directory.CreateDirectory(this.Root);
            string script = """
                [Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
                $cliArguments = @([regex]::Matches($env:CODEX_TEST_ARGUMENTS, '"([^"\r\n]*)"|(\S+)') | ForEach-Object { if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value } })
                $encoding = [System.Text.UTF8Encoding]::new($false)
                [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'args.json'), (ConvertTo-Json -InputObject @($cliArguments) -Compress), $encoding)
                [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'runs.txt'), "run`n", $encoding)
                [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'pid.txt'), [string]$PID, $encoding)
                $mode = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'mode.txt'))
                $threadId = $null
                while (($line = [Console]::In.ReadLine()) -ne $null) {
                    [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'requests.jsonl'), $line + "`n", $encoding)
                    $message = $line | ConvertFrom-Json
                    if ($message.method -eq 'initialize') {
                        @{id=$message.id;result=@{}} | ConvertTo-Json -Compress -Depth 10
                        continue
                    }
                    if ($message.method -eq 'initialized') {
                        continue
                    }
                    if ($message.method -eq 'thread/start') {
                        $threadId = 'thr_' + [Guid]::NewGuid().ToString('N')
                        @{id=$message.id;result=@{thread=@{id=$threadId}}} | ConvertTo-Json -Compress -Depth 10
                        continue
                    }
                    if ($message.method -eq 'thread/resume') {
                        $threadId = $message.params.threadId
                        @{id=$message.id;result=@{thread=@{id=$threadId}}} | ConvertTo-Json -Compress -Depth 10
                        continue
                    }
                    if ($message.method -ne 'turn/start') {
                        continue
                    }
                    $prompt = $message.params.input[0].text
                    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'prompt.txt'), $prompt, $encoding)
                    $turnId = 'turn'
                    @{id=$message.id;result=@{turn=@{id=$turnId;status='inProgress'}}} | ConvertTo-Json -Compress -Depth 10
                    if ($mode -eq 'wait') { Start-Sleep -Seconds 60 }
                    if ($mode -eq 'slow') { Start-Sleep -Milliseconds 400 }
                    if ($mode -eq 'fail') {
                        @{method='turn/completed';params=@{threadId=$threadId;turn=@{id=$turnId;status='failed'}}} | ConvertTo-Json -Compress -Depth 10
                        exit 1
                    }
                    if ($mode -eq 'incomplete') { exit 0 }
                    @{method='item/completed';params=@{threadId=$threadId;turnId=$turnId;completedAtMs=0;item=@{id='answer';type='agentMessage';text='FINAL:' + $prompt}}} | ConvertTo-Json -Compress -Depth 10
                    @{method='turn/completed';params=@{threadId=$threadId;turn=@{id=$turnId;status='completed'}}} | ConvertTo-Json -Compress -Depth 10
                    if ($mode -eq 'nonzero') { exit 7 }
                }
                """;
            File.WriteAllText(Path.Combine(this.Root, "fixture.ps1"), script, new System.Text.UTF8Encoding(true));
            File.WriteAllText(Path.Combine(this.Root, "mode.txt"), mode);
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            File.WriteAllText(Path.Combine(this.Root, "codex.cmd"), $"@echo off\r\nset \"CODEX_TEST_ARGUMENTS=%*\"\r\n\"{powershell}\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0fixture.ps1\"\r\nexit /b %errorlevel%\r\n");
        }

        public CodexThreadStore Store => new(this.CreateOptions());

        public CodexAssistantOptions CreateOptions()
        {
            CodexAssistantOptions options = new()
            {
                ExecutablePath = Path.Combine(this.Root, "codex.cmd"),
                ThreadDatabasePath = Path.Combine(this.Root, "messages.db"),
                ModelName = "test-model",
                ReasoningEffort = "high",
                WorkingDirectory = this.Root,
            };
            options.Validate();
            return options;
        }

        public CodexAssistant CreateTool(string? userAor = "sip:user@one.test", string assistantNumber = "100", ILogger? logger = null)
        {
            CodexAssistantOptions options = this.CreateOptions();
            return new CodexAssistant(new CodexAppServerFunction(options), new CodexThreadStore(options), options)
            {
                Logger = logger ?? NullLogger.Instance,
                CallControl = new Control(userAor, assistantNumber),
            };
        }

        public string[] ReadArguments()
        {
            return JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(this.Root, "args.json")))!;
        }

        public string[] ReadMethods()
        {
            return File.ReadAllLines(Path.Combine(this.Root, "requests.jsonl"))
                .Select(static line => JsonDocument.Parse(line))
                .Select(static document =>
                {
                    using (document)
                    {
                        return document.RootElement.GetProperty("method").GetString()!;
                    }
                })
                .ToArray();
        }

        public JsonElement ReadRequest(string method)
        {
            JsonElement request = File.ReadAllLines(Path.Combine(this.Root, "requests.jsonl"))
                .Select(static line => JsonDocument.Parse(line))
                .Select(static document =>
                {
                    using (document)
                    {
                        return document.RootElement.Clone();
                    }
                })
                .LastOrDefault(document => document.GetProperty("method").GetString() == method);
            if (request.ValueKind == JsonValueKind.Undefined)
            {
                throw new Xunit.Sdk.XunitException($"未找到 {method} 请求。");
            }

            return request;
        }

        public void Dispose()
        {
            SqliteConnectionStringBuilder builder = new()
            {
                DataSource = Path.Combine(this.Root, "messages.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
                Cache = SqliteCacheMode.Shared,
                Pooling = true,
                DefaultTimeout = 5,
            };
            using SqliteConnection connection = new(builder.ConnectionString);
            SqliteConnection.ClearPool(connection);
            for (int attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    Directory.Delete(this.Root, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 19)
                {
                    Thread.Sleep(100);
                }
            }
        }
    }

    private sealed class CapturingLogger : ILogger
    {
        public string? Message { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Information)
            {
                this.Message = formatter(state, exception);
            }
        }
    }

    private sealed class Control(string? userAor, string assistantNumber) : IAssistantControl
    {
        public string? UserAor => userAor;
        public string? CallerNumber => "user";
        public string? AssistantNumber => assistantNumber;
        public bool IsCallActive => true;
        public void HangupCurrentCall() => throw new NotSupportedException();
        public Task<AssistantSwitchResult> SwitchAssistantAsync(string targetAssistantNumber, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DtmfInputResult> RequestDtmfInputAsync(DtmfKey keys, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
