param([Parameter(Mandatory)][string]$Protoc,[string]$GameRoot='C:/Games/Sea_Power_8_3')
$ErrorActionPreference='Stop'
$source=& "$PSScriptRoot/Fetch-Modules.ps1"
$api=$source['Gateway.Api']
dotnet build "$PSScriptRoot/../SeaPower.Client.csproj" -c Release "-p:GatewayApiSource=$api" "-p:Protoc=$Protoc" "-p:GameManaged=$GameRoot/Sea Power_Data/Managed"
if($LASTEXITCODE){throw 'Provider build failed'}
dotnet run --project "$PSScriptRoot/../test/ProviderTests.csproj" -c Release "-p:GatewayApiSource=$api" "-p:Protoc=$Protoc"
if($LASTEXITCODE){throw 'Provider tests failed'}

