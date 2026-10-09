NavalCommandGetDependencySource(https://github.com/NavalCommand/Gateway.Api.git apiSource)
find_package(Protobuf CONFIG REQUIRED)
find_program(DOTNET_EXECUTABLE dotnet REQUIRED)
add_test(NAME provider COMMAND "${DOTNET_EXECUTABLE}" run --project "${PROJECT_SOURCE_DIR}/test/ProviderTests.csproj" -c Release "-p:GatewayApiSource=${apiSource}" "-p:Protoc=$<TARGET_FILE:protobuf::protoc>")
