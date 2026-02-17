[System.Reflection.Assembly]::LoadFrom('C:\_innovative\_source\sbn-services\lib\AdoNetCore\AdoNetCore.AseClient.dll') | Out-Null
$asm = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'AdoNetCore.AseClient' }
# Check AseBulkCopy for any charset/conversion related properties or methods
$bcp = $asm.GetType('AdoNetCore.AseClient.AseBulkCopy')
Write-Host '=== AseBulkCopy ALL Properties ==='
$bcp.GetProperties() | ForEach-Object { Write-Host "$($_.PropertyType.Name) $($_.Name)" }
Write-Host ''
Write-Host '=== AseBulkCopy ALL Methods ==='
$bcp.GetMethods([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::DeclaredOnly) | ForEach-Object {
    $params = ($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
    Write-Host "$($_.ReturnType.Name) $($_.Name)($params)"
}
# Check for connection string builder
Write-Host ''
Write-Host '=== Types matching ConnectionString or Builder ==='
$asm.GetExportedTypes() | Where-Object { $_.Name -match 'Builder|ConnectionString' } | ForEach-Object { Write-Host $_.FullName }
