[System.Reflection.Assembly]::LoadFrom('C:\_innovative\_source\sbn-services\lib\AdoNetCore\AdoNetCore.AseClient.dll') | Out-Null
$t = [AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'AdoNetCore.AseClient' } | ForEach-Object { $_.GetType('AdoNetCore.AseClient.AseBulkCopy') }
Write-Host '--- Properties ---'
$t.GetProperties() | ForEach-Object { Write-Host "$($_.PropertyType.Name) $($_.Name)" }
Write-Host '--- Methods ---'
$t.GetMethods([System.Reflection.BindingFlags]::Public -bor [System.Reflection.BindingFlags]::Instance -bor [System.Reflection.BindingFlags]::DeclaredOnly) | ForEach-Object {
    $params = ($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
    Write-Host "$($_.ReturnType.Name) $($_.Name)($params)"
}
Write-Host '--- Constructors ---'
$t.GetConstructors() | ForEach-Object {
    $params = ($_.GetParameters() | ForEach-Object { "$($_.ParameterType.Name) $($_.Name)" }) -join ', '
    Write-Host "ctor($params)"
}
