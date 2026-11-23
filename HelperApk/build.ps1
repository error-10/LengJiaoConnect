# 棱角互联 · 手机助手 APK 构建脚本
# 用法：pwsh -File build.ps1
# 产物：HelperApk\LengJiaoHelper.apk（覆盖）
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

# ---------- SDK ----------
$sdk = $env:ANDROID_SDK_ROOT
if (-not $sdk) { $sdk = $env:ANDROID_HOME }
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
if (-not (Test-Path $sdk)) { throw "找不到 Android SDK：$sdk" }

$buildTools = Get-ChildItem (Join-Path $sdk 'build-tools') -Directory |
    Sort-Object { try { [version]$_.Name } catch { [version]'0.0.0' } } -Descending |
    Select-Object -First 1
if (-not $buildTools) { throw "build-tools 目录是空的：$sdk\build-tools" }

$aapt      = Join-Path $buildTools.FullName 'aapt.exe'
$d8        = Join-Path $buildTools.FullName 'd8.bat'
$zipalign  = Join-Path $buildTools.FullName 'zipalign.exe'
$apksigner = Join-Path $buildTools.FullName 'apksigner.bat'
$androidJar = Join-Path $sdk 'platforms\android-36\android.jar'
if (-not (Test-Path $androidJar)) {
    $platform = Get-ChildItem (Join-Path $sdk 'platforms') -Directory |
        Sort-Object { try { [version]($_.Name -replace 'android-','') } catch { [version]'0.0.0' } } -Descending |
        Select-Object -First 1
    if (-not $platform) { throw "没有可用的 platform" }
    $androidJar = Join-Path $platform.FullName 'android.jar'
}

# ---------- JDK：PATH 里的 java 可能是 JRE 8，d8 会炸，这里强制指到 JDK ----------
$jdk = $env:JAVA_HOME
if (-not $jdk -or -not (Test-Path (Join-Path $jdk 'bin\javac.exe'))) {
    $candidates = @()
    $candidates += Get-ChildItem 'C:\Program Files\Eclipse Adoptium\jdk-*' -Directory -ErrorAction SilentlyContinue
    $candidates += Get-ChildItem 'C:\Program Files\Java\jdk-*' -Directory -ErrorAction SilentlyContinue
    $candidates += Get-ChildItem 'C:\Program Files\Android\Android Studio\jbr' -Directory -ErrorAction SilentlyContinue
    $jdk = ($candidates | Sort-Object Name -Descending | Select-Object -First 1).FullName
}
if (-not $jdk) { throw "找不到 JDK（需要 javac）" }
$env:JAVA_HOME = $jdk
$env:PATH = (Join-Path $jdk 'bin') + ';' + $env:PATH
$env:JAVA_EXE = Join-Path $jdk 'bin\java.exe'
Write-Host "JDK       : $jdk"
Write-Host "build-tools: $($buildTools.FullName)"
Write-Host "android.jar: $androidJar"

# ---------- 清理 ----------
$gen     = Join-Path $here 'gen'
$classes = Join-Path $here 'classes'
$dexDir  = Join-Path $here 'dex'
foreach ($dir in @($gen, $classes, $dexDir)) {
    if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
    New-Item -ItemType Directory -Path $dir | Out-Null
}
$unsigned = Join-Path $here 'unsigned.apk'
$aligned  = Join-Path $here 'aligned.apk'
$output   = Join-Path $here 'LengJiaoHelper.apk'
foreach ($f in @($unsigned, $aligned, $output)) { if (Test-Path $f) { Remove-Item $f -Force } }

# ---------- 1. 资源/清单 ----------
& $aapt package -f -M (Join-Path $here 'AndroidManifest.xml') -S (Join-Path $here 'res') -I $androidJar -F $unsigned
if ($LASTEXITCODE -ne 0) { throw "aapt package 失败" }

# ---------- 2. 编译 ----------
$sources = @(Get-ChildItem $here -Filter *.java | ForEach-Object { $_.FullName })
& (Join-Path $jdk 'bin\javac.exe') -encoding UTF-8 -source 8 -target 8 `
    -nowarn -bootclasspath $androidJar -classpath $androidJar -d $classes $sources
if ($LASTEXITCODE -ne 0) { throw "javac 失败" }

# ---------- 3. dex ----------
$classFiles = @(Get-ChildItem $classes -Recurse -Filter *.class | ForEach-Object { $_.FullName })
& $d8 --lib $androidJar --min-api 24 --output $dexDir @classFiles
if ($LASTEXITCODE -ne 0) { throw "d8 失败" }

# aapt add 的路径要相对，切到 dex 目录再塞进去
Push-Location $dexDir
try {
    & $aapt add $unsigned 'classes.dex'
    if ($LASTEXITCODE -ne 0) { throw "aapt add 失败" }
} finally {
    Pop-Location
}

# ---------- 4. 对齐 + 签名 ----------
& $zipalign -f -p 4 $unsigned $aligned
if ($LASTEXITCODE -ne 0) { throw "zipalign 失败" }

& $apksigner sign --ks (Join-Path $here 'debug.keystore') --ks-pass pass:android `
    --key-pass pass:android --ks-key-alias debug --out $output $aligned
if ($LASTEXITCODE -ne 0) { throw "apksigner 失败" }

Write-Host ""
Write-Host "构建完成：$output ($([math]::Round((Get-Item $output).Length / 1KB, 1)) KB)" -ForegroundColor Green
