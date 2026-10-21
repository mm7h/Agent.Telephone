#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64')]
    [string] $RuntimeIdentifier,
    [Parameter(Mandatory)] [string] $Version,
    [string] $OutputDirectory = (Join-Path $PSScriptRoot '../../../artifacts/release'),
    [string] $SourceDirectory = (Join-Path $PSScriptRoot '../../..'),
    [string] $ReleaseTag = $Version,
    [string] $SourceCommit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath($SourceDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
function ConvertTo-ReleaseVersion([string] $Value) {
    if ($Value -cnotmatch '^v?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?\z') {
        throw '版本必须是 [v]major.minor.patch[-prerelease]，例如 v0.2.0 或 0.2.0-rc.1。'
    }
    foreach ($part in @($Matches[1], $Matches[2], $Matches[3])) {
        if ($part.Length -gt 5 -or [int]$part -gt 65534) { throw '程序集版本数值段不得超过 65534。' }
    }
    if ($Matches[4]) {
        foreach ($part in $Matches[4].Split('.')) {
            if ($part -match '^0[0-9]+$') { throw '预发布数字段不能有前导零。' }
        }
    }
    return $Value -creplace '^v', ''
}
$Version = ConvertTo-ReleaseVersion $Version
if ((ConvertTo-ReleaseVersion $ReleaseTag) -ne $Version) { throw '版本与 tag 不一致。' }
$assemblyVersion = ($Version.Split('-')[0]) + '.0'
$actualCommit = & git -C $repositoryRoot rev-parse 'HEAD^{commit}'
if ($LASTEXITCODE -ne 0) { throw '无法读取源码提交。' }
if (-not $SourceCommit) { $SourceCommit = $actualCommit }
if ($SourceCommit -cne $actualCommit) { throw '源码提交与发布声明不一致。' }
$machineRid = if ($IsWindows) { 'win-' } elseif ($IsLinux) { 'linux-' } else { 'unsupported-' }
$machineRid += [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($machineRid -ne $RuntimeIdentifier) { throw "发布必须在对应系统与架构运行：$machineRid / $RuntimeIdentifier" }

$archiveName = "Agent.Telephone-$Version-$RuntimeIdentifier.zip"
$archivePath = Join-Path $OutputDirectory $archiveName
if (Test-Path -LiteralPath $archivePath) { throw "运行包已存在，请选择新的输出目录：$archivePath" }
$buildDirectory = Join-Path $OutputDirectory ('.build-' + [Guid]::NewGuid().ToString('N'))
$publishDirectory = Join-Path $buildDirectory 'publish'
$packageDirectory = Join-Path $buildDirectory 'package'
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
$versionArguments = @('-p:ReleasePackage=true', "-p:Version=$Version", "-p:AssemblyVersion=$assemblyVersion",
    "-p:FileVersion=$assemblyVersion", "-p:InformationalVersion=$Version",
    '-p:IncludeSourceRevisionInInformationalVersion=false')
$project = Join-Path $repositoryRoot 'demo/Agent.Telephone.Sample.Server/Agent.Telephone.Sample.Server.csproj'
& dotnet publish $project -c Release -r $RuntimeIdentifier @versionArguments -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw '电话服务发布失败。' }

# Walk the actual host graph and inspect managed assemblies before single-file bundling.
$assemblyRecords = @()
$projectPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$pending = [Collections.Generic.Queue[string]]::new()
$pending.Enqueue($project)
while ($pending.Count) {
    $projectPath = [IO.Path]::GetFullPath($pending.Dequeue())
    if (-not $projectPaths.Add($projectPath)) { continue }
    [xml] $projectXml = Get-Content -LiteralPath $projectPath -Raw
    foreach ($reference in $projectXml.SelectNodes('//ProjectReference')) {
        $pending.Enqueue([IO.Path]::GetFullPath((Join-Path (Split-Path $projectPath) ($reference.Include -replace '\\', '/'))))
    }
    $name = [IO.Path]::GetFileNameWithoutExtension($projectPath)
    $compiled = Join-Path (Split-Path $projectPath) "obj/Release/net8.0/$RuntimeIdentifier/$name.dll"
    # SDK project references normally build libraries without the host's RID.
    if (-not (Test-Path -LiteralPath $compiled -PathType Leaf)) {
        $compiled = Join-Path (Split-Path $projectPath) "obj/Release/net8.0/$name.dll"
    }
    if (-not (Test-Path -LiteralPath $compiled -PathType Leaf)) { throw "未找到编译程序集：$compiled" }
    $identity = [Reflection.AssemblyName]::GetAssemblyName($compiled)
    $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($compiled)
    if ($identity.Version.ToString() -ne $assemblyVersion -or $info.FileVersion -ne $assemblyVersion -or $info.ProductVersion -ne $Version) {
        throw "程序集版本与 tag 不一致：$name"
    }
    $assemblyRecords += @{ Name = $identity.Name; AssemblyVersion = $identity.Version.ToString(); FileVersion = $info.FileVersion; InformationalVersion = $info.ProductVersion }
}
ConvertTo-Json -InputObject @($assemblyRecords | Sort-Object Name) -Depth 3 |
    Set-Content -LiteralPath (Join-Path $packageDirectory 'assembly-versions.json') -Encoding utf8NoBOM

$extension = if ($IsWindows) { '.exe' } else { '' }

# Copy only public release inputs. Never recursively copy a developer's runtime directory.
$program = "Agent.Telephone.Sample.Server$extension"
Copy-Item -LiteralPath (Join-Path $publishDirectory $program) -Destination $packageDirectory
$configRoot = Join-Path $publishDirectory 'configs'
New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'configs/AssistantPrompts') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $configRoot 'config.json') -Destination (Join-Path $packageDirectory 'configs/config.json')
$promptFiles = & git -C $repositoryRoot ls-files -- 'demo/Agent.Telephone.Sample.Server/configs/AssistantPrompts/*.md'
if ($LASTEXITCODE -ne 0) { throw '无法读取提示词文件清单。' }
foreach ($file in $promptFiles) {
    $relative = $file -replace '^demo/Agent.Telephone.Sample.Server/', ''
    $destination = Join-Path $packageDirectory $relative
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $publishDirectory $relative) -Destination $destination
}
$documentation = & git -C $repositoryRoot -c core.quotepath=false ls-files -- docs
if ($LASTEXITCODE -ne 0) { throw '无法读取文档文件清单。' }
foreach ($file in $documentation) {
    if ([IO.Path]::GetExtension($file) -notin @('.md', '.png', '.jpg', '.jpeg')) { continue }
    $destination = Join-Path $packageDirectory $file
    New-Item -ItemType Directory -Path (Split-Path $destination) -Force | Out-Null
    Copy-Item -LiteralPath (Join-Path $repositoryRoot $file) -Destination $destination
}
foreach ($directory in @('ffmpeg', 'models', 'data', 'logs')) {
    New-Item -ItemType Directory -Path (Join-Path $packageDirectory $directory) | Out-Null
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination $packageDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README-runtime.md') -Destination (Join-Path $packageDirectory 'README.md')
@{ Version = $Version; RuntimeIdentifier = $RuntimeIdentifier; Tag = $ReleaseTag; SourceCommit = $SourceCommit } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $packageDirectory 'package.json') -Encoding utf8NoBOM

# ZipFile preserves empty resource directories, unlike Compress-Archive's wildcard input.
[IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $archivePath, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $archiveName" | Set-Content -LiteralPath "$archivePath.sha256" -Encoding utf8NoBOM
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command python3 -ErrorAction Stop }
& $python.Source (Join-Path $PSScriptRoot 'release_packages.py') verify --rid $RuntimeIdentifier --directory $OutputDirectory --tag $ReleaseTag --sha $SourceCommit
if ($LASTEXITCODE -ne 0) { throw '运行包完整性校验失败。' }
Write-Host "已生成并验证：$archivePath"
