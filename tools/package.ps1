# Builds both halves of RepoCraft and packs a release into dist\:
#   RepoCraft-<version>.zip               everything, for unzipping into the R.E.P.O. game folder:
#                                         BepInEx 5, the RepoCraft plugin, and RepoCraft-Minecraft.zip
#                                         (the Minecraft it starts)
#   RepoCraft-<version>-plugin-only.zip   just BepInEx\plugins\RepoCraft, for an existing BepInEx /
#                                         mod manager install (still with the bundled Minecraft)
#   repocraft-fabric-<version>.jar        the Minecraft mod on its own (for your own launcher)
#
# RepoCraft-Minecraft.zip holds a portable Prism Launcher with a ready "RepoCraft" instance
# (Minecraft 26.3, Fabric Loader 0.19.5, Fabric API, e4mc, RepoCraft). The plugin unpacks it to
# %LOCALAPPDATA%\RepoCraft and starts it; Prism asks the player to sign in once, then downloads
# Minecraft and Java itself.
#
#   powershell -ExecutionPolicy Bypass -File tools\package.ps1 [-NoBuild]
param([switch]$NoBuild, [string]$RepoDir = "C:\Program Files (x86)\Steam\steamapps\common\REPO")
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = Split-Path -Parent $PSScriptRoot
$version = (Select-String -Path "$root\fabric\gradle.properties" -Pattern '^version=(.+)$').Matches[0].Groups[1].Value.Trim()
$tools = "$root\.tools"

# Pinned downloads (checked against these hashes).
$prismVersion = "11.1.1"
$prismZip = "PrismLauncher-Windows-MSVC-Portable-$prismVersion.zip"
$prismUrl = "https://github.com/PrismLauncher/PrismLauncher/releases/download/$prismVersion/$prismZip"
$prismSha256 = "ab35a770fb06d89d2ccc098079db5db329fb4e68f42b72babd8b095efde3d2d7"
$prismLicenseUrl = "https://raw.githubusercontent.com/PrismLauncher/PrismLauncher/$prismVersion/LICENSE"
$fabricApiJar = "fabric-api-0.161.0+26.3.jar"
$fabricApiUrl = "https://cdn.modrinth.com/data/P7dR8mSH/versions/bNnaTiuM/fabric-api-0.161.0%2B26.3.jar"
$fabricApiSha512 = "ed6b2586d6fde11fde8472f5a527c51e99b67026e46f94d4bfd85e7e28ce5ee299173ee16ad576ceb51f39f98d30a811086a6deb1a86a524859cc16e12da109d"
# e4mc: opens the lobby host's Minecraft world to the other players (multiplayer).
$e4mcJar = "e4mc-fabric-6.2.2-modern.jar"
$e4mcUrl = "https://cdn.modrinth.com/data/qANg5Jrr/versions/AouleFRY/$e4mcJar"
$e4mcSha512 = "01ef0a8c5b76e2cb0effd337bad3350d8807d100d0ec661e01b2ffb20af7b652f756c5eaa11bee233c37905bfd7b573f7a85f3d15bfd2833962c76f02cd59a86"
$bepinexVersion = "5.4.23.3"
$bepinexZip = "BepInEx_win_x64_$bepinexVersion.zip"
$bepinexUrl = "https://github.com/BepInEx/BepInEx/releases/download/v$bepinexVersion/$bepinexZip"

function Get-Pinned([string]$url, [string]$path, [string]$algorithm, [string]$hash) {
	if (-not (Test-Path $path)) {
		New-Item -ItemType Directory (Split-Path $path) -Force | Out-Null
		Invoke-WebRequest -Uri $url -OutFile $path -UseBasicParsing
	}
	if ($hash -and (Get-FileHash $path -Algorithm $algorithm).Hash -ne $hash.ToUpper()) {
		Remove-Item $path
		throw "$path doesn't match its pinned $algorithm hash"
	}
}

# Zip entries named explicitly with forward slashes, as the zip format (and every mod manager)
# expects; Windows PowerShell's own zipping writes backslashes.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
function New-Zip([string]$path, [System.Collections.IDictionary]$entries) {
	$zip = [System.IO.Compression.ZipFile]::Open($path, [System.IO.Compression.ZipArchiveMode]::Create)
	try {
		foreach ($name in $entries.Keys) {
			[System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entries[$name], $name, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
		}
	} finally { $zip.Dispose() }
}
function Add-Folder([System.Collections.IDictionary]$entries, [string]$folder, [string]$prefix) {
	$base = (Resolve-Path $folder).Path.TrimEnd('\') + '\'
	Get-ChildItem $folder -Recurse -File -Force | Sort-Object FullName | ForEach-Object {
		$entries[$prefix + $_.FullName.Substring($base.Length).Replace('\', '/')] = $_.FullName
	}
}

if (-not $NoBuild) {
	# The plugin compiles against a publicized copy of REPO's Assembly-CSharp (its internals).
	$managed = Join-Path $RepoDir "REPO_Data\Managed"
	$publicized = "$tools\publicized\Assembly-CSharp.dll"
	if (-not (Test-Path $publicized)) {
		dotnet build "$root\tools\Publicizer\Publicizer.csproj" -c Release -nologo -v q
		dotnet "$root\tools\Publicizer\bin\Release\net8.0\Publicizer.dll" "$managed\Assembly-CSharp.dll" $publicized $managed
	}
	dotnet build "$root\plugin\RepoCraft.csproj" -c Release -nologo -v q -p:RepoDir="$RepoDir"
	if ($LASTEXITCODE) { throw "the REPO plugin didn't build" }
	$env:JAVA_HOME = (Get-ChildItem $tools -Directory -Filter "jdk-25*" | Select-Object -First 1).FullName
	Push-Location "$root\fabric"
	try {
		.\gradlew.bat --no-daemon -q build -x test --no-configuration-cache
		if ($LASTEXITCODE) { throw "the Fabric mod didn't build" }
	} finally { Pop-Location }
}

$dll = "$root\plugin\bin\Release\netstandard2.1\RepoCraft.dll"
$jar = "$root\fabric\build\libs\repocraft-$version.jar"
foreach ($f in @($dll, $jar)) {
	if (-not (Test-Path $f)) { throw "missing $f (build first, or drop -NoBuild)" }
}
$cache = "$tools\cache"
Get-Pinned $prismUrl "$cache\$prismZip" SHA256 $prismSha256
Get-Pinned $fabricApiUrl "$cache\$fabricApiJar" SHA512 $fabricApiSha512
Get-Pinned $e4mcUrl "$cache\$e4mcJar" SHA512 $e4mcSha512
Get-Pinned $prismLicenseUrl "$cache\PrismLauncher-$prismVersion-LICENSE.txt" "" ""
Get-Pinned $bepinexUrl "$cache\$bepinexZip" "" ""

$dist = "$root\dist"
New-Item -ItemType Directory $dist -Force | Out-Null
Get-ChildItem $dist | Remove-Item -Recurse -Force

# The bundled Minecraft: Prism (portable), the RepoCraft instance, its mods, Prism's default settings.
$bundle = "$dist\bundle"
Copy-Item -Recurse "$root\tools\minecraft-bundle" $bundle
Expand-Archive "$cache\$prismZip" "$bundle\Prism" -Force
Copy-Item "$cache\PrismLauncher-$prismVersion-LICENSE.txt" "$bundle\Prism\LICENSE-PrismLauncher.txt"
(Get-Content "$bundle\Prism\THIRD-PARTY.txt" -Raw).Replace("{PRISM_VERSION}", $prismVersion) | Set-Content "$bundle\Prism\THIRD-PARTY.txt" -NoNewline
$mods = "$bundle\Prism\instances\RepoCraft\.minecraft\mods"
New-Item -ItemType Directory $mods -Force | Out-Null
Copy-Item "$cache\$fabricApiJar" $mods
Copy-Item "$cache\$e4mcJar" $mods
Copy-Item $jar "$mods\repocraft-$version.jar"
Set-Content "$bundle\bundle-version.txt" "RepoCraft $version, Prism Launcher $prismVersion, $fabricApiJar, $e4mcJar" -NoNewline
$bundleEntries = [ordered]@{}
Add-Folder $bundleEntries $bundle ""
New-Zip "$dist\RepoCraft-Minecraft.zip" $bundleEntries
Remove-Item -Recurse -Force $bundle

$plugin = [ordered]@{
	"BepInEx/plugins/RepoCraft/RepoCraft.dll" = $dll
	"BepInEx/plugins/RepoCraft/RepoCraft-Minecraft.zip" = "$dist\RepoCraft-Minecraft.zip"
	"BepInEx/plugins/RepoCraft/README.md" = "$root\README.md"
	"BepInEx/plugins/RepoCraft/LICENSE.txt" = "$root\LICENSE"
	"BepInEx/plugins/RepoCraft/THIRD-PARTY-NOTICES.md" = "$root\THIRD-PARTY-NOTICES.md"
}
New-Zip "$dist\RepoCraft-$version-plugin-only.zip" $plugin

# Everything for the game folder: BepInEx 5 (unmodified), then the plugin.
$bep = "$dist\bepinex"
Expand-Archive "$cache\$bepinexZip" $bep -Force
$full = [ordered]@{}
Add-Folder $full $bep ""
foreach ($k in $plugin.Keys) { $full[$k] = $plugin[$k] }
$full["RepoCraft-INSTALL.txt"] = "$root\tools\INSTALL.txt"
New-Zip "$dist\RepoCraft-$version.zip" $full
Remove-Item -Recurse -Force $bep
Copy-Item $jar "$dist\repocraft-fabric-$version.jar"
Remove-Item "$dist\RepoCraft-Minecraft.zip"

Get-ChildItem $dist | ForEach-Object { "{0,-42} {1,14:N0} bytes" -f $_.Name, $_.Length }
