# RepoCraft developer install: builds both halves and puts them where REPO and Prism Launcher
# load them from.
#
#   tools\install-dev.ps1                 build + install the REPO plugin and the Minecraft mod
#   tools\install-dev.ps1 -SkipFabric     only the REPO plugin
#   tools\install-dev.ps1 -SkipPlugin     only the Minecraft mod
#
# REPO side: BepInEx 5 (from .tools\BepInEx5) goes into the game folder if it isn't there yet, and
# RepoCraft.dll into BepInEx\plugins\RepoCraft. Minecraft side: a "RepoCraft" instance in Prism
# Launcher (Minecraft 26.3 + Fabric Loader 0.19.5) with Fabric API, e4mc and the RepoCraft mod.
param(
	[string]$RepoDir = "C:\Program Files (x86)\Steam\steamapps\common\REPO",
	[string]$PrismData = "$env:APPDATA\PrismLauncher",
	[switch]$SkipFabric,
	[switch]$SkipPlugin
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$tools = Join-Path $root ".tools"
$jdk = Get-ChildItem $tools -Directory -Filter "jdk-25*" | Select-Object -First 1

if (-not $SkipPlugin) {
	if (-not (Test-Path (Join-Path $RepoDir "winhttp.dll"))) {
		Write-Host "Installing BepInEx 5 into $RepoDir"
		Copy-Item -Recurse -Force (Join-Path $tools "BepInEx5\*") $RepoDir
	}
	# The plugin compiles against a publicized copy of REPO's Assembly-CSharp (its internals).
	$managed = Join-Path $RepoDir "REPO_Data\Managed"
	$publicized = Join-Path $tools "publicized\Assembly-CSharp.dll"
	if (-not (Test-Path $publicized) -or (Get-Item $publicized).LastWriteTime -lt (Get-Item (Join-Path $managed "Assembly-CSharp.dll")).LastWriteTime) {
		Write-Host "Publicizing REPO's Assembly-CSharp"
		dotnet build (Join-Path $root "tools\Publicizer\Publicizer.csproj") -c Release -nologo -v q
		dotnet (Join-Path $root "tools\Publicizer\bin\Release\net8.0\Publicizer.dll") (Join-Path $managed "Assembly-CSharp.dll") $publicized $managed
		if ($LASTEXITCODE -ne 0) { throw "publicizing failed" }
	}
	Write-Host "Building the REPO plugin"
	dotnet build (Join-Path $root "plugin\RepoCraft.csproj") -c Release -nologo -v q -p:RepoDir="$RepoDir"
	if ($LASTEXITCODE -ne 0) { throw "plugin build failed" }
	$plugins = Join-Path $RepoDir "BepInEx\plugins\RepoCraft"
	New-Item -ItemType Directory -Force $plugins | Out-Null
	Copy-Item -Force (Join-Path $root "plugin\bin\Release\netstandard2.1\RepoCraft.dll") $plugins
	Copy-Item -Force (Join-Path $root "plugin\bin\Release\netstandard2.1\RepoCraft.pdb") $plugins -ErrorAction SilentlyContinue
	Write-Host "  -> $plugins"
}

if (-not $SkipFabric) {
	Write-Host "Building the Minecraft mod"
	$env:JAVA_HOME = $jdk.FullName
	Push-Location (Join-Path $root "fabric")
	try {
		& .\gradlew.bat --no-daemon -q build -x test
		if ($LASTEXITCODE -ne 0) { throw "fabric build failed" }
	} finally {
		Pop-Location
	}
	$version = (Select-String -Path (Join-Path $root "fabric\gradle.properties") -Pattern '^version=(.*)$').Matches[0].Groups[1].Value
	$jar = Join-Path $root "fabric\build\libs\repocraft-$version.jar"

	$instance = Join-Path $PrismData "instances\RepoCraft"
	$mods = Join-Path $instance ".minecraft\mods"
	New-Item -ItemType Directory -Force $mods | Out-Null
	$bundle = Join-Path $root "tools\minecraft-bundle\Prism\instances\RepoCraft"
	Copy-Item -Force (Join-Path $bundle "instance.cfg") $instance
	Copy-Item -Force (Join-Path $bundle "mmc-pack.json") $instance
	Get-ChildItem $mods -Filter "repocraft-*.jar" | Remove-Item -Force
	Get-ChildItem $mods -Filter "fabric-api-*.jar" | Remove-Item -Force
	Get-ChildItem $mods -Filter "e4mc-*.jar" | Remove-Item -Force
	Copy-Item -Force $jar $mods
	Copy-Item -Force (Join-Path $tools "cache\fabric-api-0.161.0+26.3.jar") $mods
	# e4mc: multiplayer (the lobby host's world, open to the others). tools\package.ps1 downloads it.
	$e4mc = Join-Path $tools "cache\e4mc-fabric-6.2.2-modern.jar"
	if (Test-Path $e4mc) { Copy-Item -Force $e4mc $mods } else { Write-Warning "no $e4mc (run tools\package.ps1 once): no shared multiplayer world" }
	Write-Host "  -> $mods"
}
Write-Host "Done."
