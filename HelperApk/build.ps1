$ErrorActionPreference = "Stop"

$sdkRoot = "$env:LOCALAPPDATA\Android\Sdk"
$buildTools = "$sdkRoot\build-tools\36.1.0"
$platformsDir = "$sdkRoot\platforms"
$androidJar = (Get-ChildItem -Path $platformsDir -Filter "android.jar" -Recurse | Select-Object -Last 1).FullName

if (-not (Test-Path $androidJar)) {
    Write-Host "Cannot find android.jar"
    exit 1
}

Write-Host "1. Packaging resources with aapt..."
& "$buildTools\aapt.exe" package -f -M AndroidManifest.xml -I $androidJar -F unsigned.apk

Write-Host "2. Compiling Java code..."
javac -source 8 -target 8 -cp $androidJar HelperService.java

Write-Host "3. Creating classes.dex with d8..."
& "$buildTools\d8.bat" HelperService.class

Write-Host "4. Adding dex to APK..."
& "$buildTools\aapt.exe" add unsigned.apk classes.dex

if (-not (Test-Path "debug.keystore")) {
    Write-Host "5. Generating debug keystore..."
    keytool -genkeypair -keyalg RSA -keystore debug.keystore -alias debug -storepass android -keypass android -dname "CN=Debug" -validity 9999
}

Write-Host "6. Aligning APK..."
& "$buildTools\zipalign.exe" -f -p 4 unsigned.apk aligned.apk

Write-Host "7. Signing APK..."
& "$buildTools\apksigner.bat" sign --ks debug.keystore --ks-pass pass:android --out LengJiaoHelper.apk aligned.apk

Write-Host "Build Complete! LengJiaoHelper.apk is ready."
