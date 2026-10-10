find_program(DOTNET_EXECUTABLE dotnet REQUIRED)
add_test(NAME provider
    COMMAND "${DOTNET_EXECUTABLE}" run --project "${PROJECT_SOURCE_DIR}/test/managed/ProviderTests.csproj"
        --configuration "$<CONFIG>")
