using System.Diagnostics;
using System.Text.Json;
using Agent.Telephone.Abstractions.Common.Contexts;
using Agent.Telephone.Abstractions.Common.Enums;
using Agent.Telephone.Abstractions.FunctionTools;
using Agent.Telephone.Sample.Server.FunctionTools.Codex;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Agent.Telephone.Tests;

public sealed class CodexAssistantTests
{
    [Fact]
    public async Task RunCodexTaskAsync_PersistsAndResumesWithoutPhoneHistoryAsync()
    {
        using Fixture fixture = new();
        const string Prompt = "中文任务 \"quotes\" & | %PATH%\n第二行";
        var first = await fixture.CreateTool().RunCodexTaskAsync(Prompt);
        string firstId = (await fixture.Store.GetThreadIdAsync("sip:user@one.test", "100", default))!;
        Assert.Equal("FINAL:" + Prompt, first.Result);
        Assert.DoesNotContain("resume", fixture.ReadArguments());
        Assert.DoesNotContain("--ephemeral", fixture.ReadArguments());
        Assert.DoesNotContain(Prompt, fixture.ReadArguments());
        Assert.Equal(new[] { "exec", "--model", "test-model", "-c", "model_reasoning_effort='high'" }, fixture.ReadArguments().Take(5));

        // 新建存储和工具实例，模拟服务重启/下一次通话。
        var second = await fixture.CreateTool().RunCodexTaskAsync("追加要求");

        Assert.Equal("FINAL:追加要求", second.Result);
        Assert.Equal(second.Result, second.Response);
        Assert.Equal(new[] { "exec", "resume", firstId, "--model", "test-model", "-c", "model_reasoning_effort='high'" }, fixture.ReadArguments().Take(7));
        Assert.Equal("追加要求", File.ReadAllText(Path.Combine(fixture.Root, "prompt.txt")));
        Assert.DoesNotContain("--last", fixture.ReadArguments());
    }

    [Fact]
    public async Task RunCodexTaskAsync_ExplicitNewTaskReplacesOnlyCurrentMappingAsync()
    {
        using Fixture fixture = new();
        await fixture.CreateTool().RunCodexTaskAsync("first");
        string? oldId = await fixture.Store.GetThreadIdAsync("sip:user@one.test", "100", default);

        await fixture.CreateTool().RunCodexTaskAsync("new task", startNewTask: true);

        string? newId = await fixture.Store.GetThreadIdAsync("sip:user@one.test", "100", default);
        Assert.NotEqual(oldId, newId);
        Assert.DoesNotContain("resume", fixture.ReadArguments());
        await fixture.CreateTool().RunCodexTaskAsync("continue");
        Assert.Equal(newId, fixture.ReadArguments()[2]);
    }

    [Fact]
    public async Task RunCodexTaskAsync_IsolatesSipDomainsAndAssistantRolesAsync()
    {
        using Fixture fixture = new();
        await fixture.CreateTool().RunCodexTaskAsync("first");
        string? firstId = await fixture.Store.GetThreadIdAsync("sip:user@one.test", "100", default);
        await fixture.CreateTool("sip:user@two.test", "100").RunCodexTaskAsync("second");
        Assert.DoesNotContain("resume", fixture.ReadArguments());
        await fixture.CreateTool("sip:user@one.test", "200").RunCodexTaskAsync("third");
        Assert.DoesNotContain("resume", fixture.ReadArguments());
        await fixture.CreateTool().RunCodexTaskAsync("continue first");
        Assert.Equal(firstId, fixture.ReadArguments()[2]);
    }

    [Theory]
    [InlineData("fail")]
    [InlineData("nonzero")]
    [InlineData("incomplete")]
    public async Task RunCodexTaskAsync_PreservesThreadAfterFailureAndDoesNotRetryAsync(string mode)
    {
        using Fixture fixture = new(mode);
        var result = await fixture.CreateTool().RunCodexTaskAsync("test");
        Assert.Contains("任务未完成", result.Result);
        Assert.NotNull(await fixture.Store.GetThreadIdAsync("sip:user@one.test", "100", default));
        Assert.Single(File.ReadAllLines(Path.Combine(fixture.Root, "runs.txt")));
    }

    [Fact]
    public async Task RunCodexTaskAsync_CancellationKillsChildAndPreservesThreadAsync()
    {
        using Fixture fixture = new("wait");
        using CancellationTokenSource cancellation = new(TimeSpan.FromSeconds(10));
        Task<FunctionReturn<string>> execution = fixture.CreateTool().RunCodexTaskAsync("test", cancellationToken: cancellation.Token);
        while (await fixture.Store.GetThreadIdAsync("sip:user@one.test", "100", cancellation.Token) is null)
        {
            await Task.Delay(25, cancellation.Token);
        }
        using Process child = Process.GetProcessById(int.Parse(File.ReadAllText(Path.Combine(fixture.Root, "pid.txt"))));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
        Assert.True(child.HasExited);
        Assert.NotNull(await fixture.Store.GetThreadIdAsync("sip:user@one.test", "100", default));
    }

    [Fact]
    public async Task RunCodexTaskAsync_ConcurrentInstancesResumeSequentiallyAsync()
    {
        using Fixture fixture = new("slow");
        Task<FunctionReturn<string>> first = fixture.CreateTool().RunCodexTaskAsync("one");
        Task<FunctionReturn<string>> second = fixture.CreateTool().RunCodexTaskAsync("two");
        await Task.WhenAll(first, second);
        Assert.DoesNotContain("任务未完成", second.Result.Result);
        Assert.Contains("resume", fixture.ReadArguments());
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

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "telephone-codex-test-" + Guid.NewGuid().ToString("N"));
        public CodexThreadStore Store => new(Path.Combine(this.Root, "messages.db"));

        public Fixture(string mode = "success")
        {
            Directory.CreateDirectory(this.Root);
            string script = """
                [Console]::InputEncoding = [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
                $cliArguments = @([regex]::Matches($env:CODEX_TEST_ARGUMENTS, '"([^"\r\n]*)"|(\S+)') | ForEach-Object { if ($_.Groups[1].Success) { $_.Groups[1].Value } else { $_.Groups[2].Value } })
                $encoding = [System.Text.UTF8Encoding]::new($false)
                [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'args.json'), (ConvertTo-Json -InputObject @($cliArguments) -Compress), $encoding)
                [IO.File]::AppendAllText((Join-Path $PSScriptRoot 'runs.txt'), "run`n", $encoding)
                [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'pid.txt'), [string]$PID, $encoding)
                $prompt = [Console]::In.ReadToEnd()
                [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'prompt.txt'), $prompt, $encoding)
                $id = if ($cliArguments[1] -eq 'resume') { $cliArguments[2] } else { [Guid]::NewGuid().ToString() }
                @{type='thread.started';thread_id=$id} | ConvertTo-Json -Compress
                $mode = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'mode.txt'))
                if ($mode -eq 'wait') { Start-Sleep -Seconds 60 }
                if ($mode -eq 'slow') { Start-Sleep -Milliseconds 400 }
                if ($mode -eq 'fail') { @{type='turn.failed'} | ConvertTo-Json -Compress; exit 1 }
                if ($mode -eq 'incomplete') { exit 0 }
                $outputIndex = [Array]::IndexOf($cliArguments, '--output-last-message')
                [IO.File]::WriteAllText($cliArguments[$outputIndex + 1], 'FINAL:' + $prompt, $encoding)
                @{type='item.completed';item=@{type='agent_message';text='not the final output file'}} | ConvertTo-Json -Compress
                @{type='turn.completed'} | ConvertTo-Json -Compress
                if ($mode -eq 'nonzero') { exit 7 }
                """;
            File.WriteAllText(Path.Combine(this.Root, "fixture.ps1"), script, new System.Text.UTF8Encoding(true));
            File.WriteAllText(Path.Combine(this.Root, "mode.txt"), mode);
            string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            File.WriteAllText(Path.Combine(this.Root, "codex.cmd"), $"@echo off\r\nset \"CODEX_TEST_ARGUMENTS=%*\"\r\n\"{powershell}\" -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"%~dp0fixture.ps1\"\r\nexit /b %errorlevel%\r\n");
        }

        public CodexAssistant CreateTool(string? userAor = "sip:user@one.test", string assistantNumber = "100")
        {
            return new CodexAssistant(
                Path.Combine(this.Root, "codex.cmd"),
                this.Root,
                this.Store,
                "test-model",
                "high")
            {
                Logger = NullLogger.Instance,
                CallControl = new Control(userAor, assistantNumber),
            };
        }

        public string[] ReadArguments() => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(this.Root, "args.json")))!;

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
