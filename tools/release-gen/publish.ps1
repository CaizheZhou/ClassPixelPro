param($is_trim, $arch, $os)

$ErrorActionPreference = "Stop"

$PUBLISH_TARGET = "..\out\ClassPixelPro"

if ($(Test-Path ./out) -eq $false) {
    mkdir out
} else {
    rm out/* -Recurse -Force
}
#dotnet clean

./tools/release-gen/generate-secrets.ps1


$os_rid = ''
if ($os -eq 'windows') {
    $os_rid = 'win'
}
if ($os -eq 'linux') {
    $os_rid = 'linux'
}

Write-Host "Publish parameters: TrimAssets=$is_trim, Platform=$arch, OS=$os, OS_RID=$os_rid" 
dotnet publish .\ClassPixelPro.Desktop\ClassPixelPro.Desktop.csproj -c Release -p:PublishProfile=FolderProfile -p:PublishDir=$PUBLISH_TARGET -p:TrimAssets=$is_trim -p:ClassPixelPro_PlatformTarget=$arch -p:RuntimeIdentifier="${os_rid}-${arch}" -p:PublishBuilding=true -p:PublishPlatform=$os

Write-Host "Packaging..." -ForegroundColor Cyan

rm ./out/ClassPixelPro/*.xml -ErrorAction Continue
7z a ./out/${env:artifact_name}.zip ./out/ClassPixelPro/* -r

Write-Host "Successfully published to $PUBLISH_TARGET" -ForegroundColor Green

