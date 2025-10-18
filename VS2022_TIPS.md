# Visual Studio 2022 Tips & Features

## Essential Keyboard Shortcuts

### Debugging
- **F5** - Start debugging
- **Ctrl+F5** - Start without debugging
- **F9** - Toggle breakpoint
- **F10** - Step over
- **F11** - Step into
- **Shift+F11** - Step out
- **Shift+F5** - Stop debugging

### Testing
- **Ctrl+E, T** - Open Test Explorer
- **Ctrl+R, A** - Run all tests
- **Ctrl+R, T** - Run tests in current context

### Code Navigation
- **F12** - Go to definition
- **Ctrl+F12** - Go to implementation
- **Ctrl+-** - Navigate backward
- **Ctrl+Shift+-** - Navigate forward
- **Ctrl+,** - Go to all (search files, types, members)

### Building
- **Ctrl+Shift+B** - Build solution
- **F6** - Build project

## IntelliSense Features

### Code Completion
- **Ctrl+Space** - Trigger IntelliSense
- **Ctrl+Shift+Space** - Parameter info
- **Tab** - Accept suggestion

### Code Actions
- **Ctrl+.** - Quick actions and refactorings (very useful!)
- Examples:
  - Add using statement
  - Generate constructor
  - Extract method
  - Implement interface

## Live Testing

Visual Studio 2022 supports **Live Unit Testing**:
1. Go to **Test > Live Unit Testing > Start**
2. See real-time test results as you type
3. Green/red indicators show test coverage in the editor

## Debugging Features

### Breakpoint Tips
- **Conditional breakpoints**: Right-click breakpoint > Conditions
- **Log points**: Right-click breakpoint > Actions (logs without stopping)
- **Tracepoints**: Add messages to Output window

### Windows to Use
- **Locals** - View local variables
- **Autos** - View variables in current/previous statements
- **Watch** - Monitor specific expressions
- **Call Stack** - See execution path
- **Output** - View debug messages and server logs

### Hot Reload
When debugging:
- Make code changes while running
- Save the file (Ctrl+S)
- Changes apply automatically (most of the time!)

## Swagger Integration

The project automatically opens Swagger UI when you run it:
1. Press F5
2. Browser opens to `https://localhost:5001/swagger`
3. Test all endpoints directly in the UI

## Running Multiple Startup Projects

To debug both the API and tests simultaneously:
1. Right-click solution > Properties
2. Select **Multiple startup projects**
3. Set both projects to **Start**

## NuGet Package Management

- **Ctrl+Q** then type "nuget" to open Package Manager
- Or: **Tools > NuGet Package Manager > Manage NuGet Packages for Solution**

## Code Cleanup

Visual Studio 2022 includes code cleanup:
1. **Ctrl+K, Ctrl+E** - Run code cleanup
2. Applies formatting, removes unused usings, etc.
3. Configure: **Tools > Options > Text Editor > Code Cleanup**

## Git Integration

- **View > Git Changes** (or Ctrl+0, Ctrl+G)
- Stage, commit, push directly from VS
- View branches, merge, resolve conflicts

## Productivity Tips

### Use the Search Box
- **Ctrl+Q** - Search everything (commands, settings, files)

### Task List
- Add `// TODO: your note` comments
- View all TODOs: **View > Task List**

### Multi-caret Editing
- **Alt+Click** - Add cursor
- **Ctrl+Alt+Click** - Add rectangular selection

### Bookmark Lines
- **Ctrl+K, Ctrl+K** - Toggle bookmark
- **Ctrl+K, Ctrl+N** - Next bookmark
- **Ctrl+K, Ctrl+P** - Previous bookmark

## Testing Features

### Test Explorer
- Right-click test > **Debug**
- Group tests by project, namespace, class
- Filter tests: Failed, Passed, Not Run

### Code Coverage
1. Run tests with coverage: **Test > Analyze Code Coverage for All Tests**
2. View colored indicators in code editor
3. See coverage percentages

## Performance Profiling

- **Alt+F2** - Open Performance Profiler
- Profile CPU usage, memory, etc.
- Useful for optimizing ServerManager performance

## Recommended Extensions

- **GitHub Copilot** - AI pair programmer
- **ReSharper** - Advanced refactoring (paid)
- **Visual Studio IntelliCode** - AI-assisted IntelliSense (built-in)
- **Web Essentials** - Better web development experience

## Configuration Files

The project includes:
- **.editorconfig** - Ensures consistent code style across team
- **launchSettings.json** - Defines run profiles (http/https)
- Both work automatically in VS 2022!

## API Testing in Visual Studio

Instead of external tools, you can use:
1. **Swagger UI** (opens automatically)
2. **Endpoints Explorer**: View > Other Windows > Endpoints Explorer
3. Test HTTP requests directly from VS 2022 17.6+

## Solution Explorer Tips

- **Ctrl+;** - Focus Solution Explorer search
- **Scope to This** - Right-click folder to hide others
- **Sync with Active Document** - Toolbar button to track open file
