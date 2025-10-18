# WreckfestController Tests

This test project contains unit tests for the Wreckfest Server Controller API.

## Test Coverage

### ServerManagerTests
Tests for the `ServerManager` service that handles process management:

- **GetStatus_WhenServerNotStarted_ReturnsNotRunning** - Verifies status when server hasn't been started
- **IsRunning_WhenServerNotStarted_ReturnsFalse** - Verifies IsRunning property when server is stopped
- **StartServerAsync_WhenServerPathDoesNotExist_ReturnsFailure** - Validates error handling for invalid paths
- **StartServerAsync_WhenServerPathIsEmpty_ReturnsFailure** - Validates error handling for empty configuration
- **StopServerAsync_WhenServerNotRunning_ReturnsFailure** - Ensures proper error when stopping a stopped server
- **SendCommandAsync_WhenServerNotRunning_ReturnsFailure** - Ensures commands can't be sent to stopped server
- **SubscribeToOutput_DoesNotThrowException** - Verifies subscription mechanism works
- **UnsubscribeFromOutput_DoesNotThrowException** - Verifies unsubscription mechanism works

### ServerControllerTests
Tests for the `ServerController` REST API endpoints:

- **GetStatus_ReturnsOkResultWithStatus** - Verifies status endpoint returns proper data
- **StartServer_WhenSuccessful_ReturnsOkResult** - Tests successful server start
- **StartServer_WhenFailed_ReturnsBadRequest** - Tests failed server start (e.g., already running)
- **StopServer_WhenSuccessful_ReturnsOkResult** - Tests successful server stop
- **StopServer_WhenFailed_ReturnsBadRequest** - Tests failed server stop (e.g., not running)
- **RestartServer_WhenSuccessful_ReturnsOkResult** - Tests successful server restart
- **RestartServer_WhenFailed_ReturnsBadRequest** - Tests failed server restart
- **SendCommand_WithValidCommand_ReturnsOkResult** - Tests sending valid commands
- **SendCommand_WithEmptyCommand_ReturnsBadRequest** - Validates empty command rejection
- **SendCommand_WithWhitespaceCommand_ReturnsBadRequest** - Validates whitespace-only command rejection
- **SendCommand_WhenServerNotRunning_ReturnsBadRequest** - Ensures commands require running server

## Running the Tests

From the solution root:
```bash
dotnet test
```

From this directory:
```bash
dotnet test
```

With detailed output:
```bash
dotnet test --verbosity normal
```

Run specific test class:
```bash
dotnet test --filter "FullyQualifiedName~ServerControllerTests"
```

Run specific test:
```bash
dotnet test --filter "FullyQualifiedName~StartServer_WhenSuccessful_ReturnsOkResult"
```

## Test Framework

- **xUnit** - Testing framework
- **Moq** - Mocking library for creating test doubles
- **Microsoft.AspNetCore.Mvc.Testing** - For integration testing support

## Notes

- Tests use mocked dependencies to avoid actual process creation
- ServerManager tests validate error conditions without starting actual processes
- Controller tests verify HTTP status codes and proper service interaction
