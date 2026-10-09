NavalCommandGetDependencySource(https://github.com/NavalCommand/Gateway.Api.git apiSource)
find_package(Protobuf CONFIG REQUIRED)
find_program(DOTNET_EXECUTABLE dotnet REQUIRED)
if(PROJECT_IS_TOP_LEVEL)
    if(NOT SEAPOWER_GAME_ROOT)
        message(
            FATAL_ERROR
                "Set SEAPOWER_GAME_ROOT in Build.Local.cmake for compile-only game references")
    endif()
    add_custom_target(
        SeaPower.Client.Managed ALL
        COMMAND
            "${DOTNET_EXECUTABLE}" build "${PROJECT_SOURCE_DIR}/SeaPower.Client.csproj" -c Release
            "-p:GatewayApiSource=${apiSource}" "-p:Protoc=$<TARGET_FILE:protobuf::protoc>"
            "-p:GameManaged=${SEAPOWER_GAME_ROOT}/Sea Power_Data/Managed"
        VERBATIM)
endif()
