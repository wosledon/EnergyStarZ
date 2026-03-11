# EnergyStarZ 发布脚本
# 发布到根目录的 pkgs 文件夹，使用独立文件模式（无需打包运行时）

$projectPath = Join-Path $PSScriptRoot "src\EnergyStarZ\EnergyStarZ.csproj"
$outputPath = Join-Path $PSScriptRoot "pkgs"

Write-Host "正在发布 EnergyStarZ..." -ForegroundColor Cyan
Write-Host "项目路径：$projectPath"
Write-Host "输出路径：$outputPath"

# 清理旧的发布文件
if (Test-Path $outputPath) {
    Write-Host "清理旧的发布文件..." -ForegroundColor Yellow
    Remove-Item $outputPath -Recurse -Force
}

# 创建输出目录
New-Item -ItemType Directory -Path $outputPath | Out-Null

# 发布项目（独立文件模式，不打包运行时）
dotnet publish $projectPath `
    -c Release `
    -o $outputPath `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:EnableCompressionInSingleFile=true

if ($LASTEXITCODE -eq 0) {
    Write-Host "`n发布成功！" -ForegroundColor Green
    Write-Host "发布文件位于：$outputPath" -ForegroundColor Green
    
    # 列出生成的文件
    Write-Host "`n生成的文件:" -ForegroundColor Cyan
    Get-ChildItem $outputPath | ForEach-Object {
        Write-Host "  $($_.Name) - $([math]::Round($_.Length / 1KB, 2)) KB"
    }
} else {
    Write-Host "`n发布失败！" -ForegroundColor Red
    exit $LASTEXITCODE
}
