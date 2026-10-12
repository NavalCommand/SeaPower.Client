find_program(DOTNET_EXECUTABLE dotnet REQUIRED)
add_test(NAME NavalCommand.SeaPower.Client.ManagedTest
    COMMAND "${DOTNET_EXECUTABLE}" run --project "${CMAKE_CURRENT_SOURCE_DIR}/managed/ProviderTests.csproj" --configuration "$<CONFIG>")
