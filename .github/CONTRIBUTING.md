# Contributing to TTSMixmaster

Thank you for your interest in contributing to TTSMixmaster! We welcome contributions from the community and are grateful for any help you can provide.

## Areas for Improvement

We're actively looking for contributions in the following areas:

- **Additional Audio Sources**: Integration with new music streaming services or audio platforms
- **Search Engine Enhancements**: Improved search algorithms and additional search engines for audio downloading
- **TTS Features**: New Tabletop Simulator features, enhanced object customization, and advanced playlist controls
- **Error Handling**: Better error messages, recovery mechanisms, and user feedback
- **Performance Optimizations**: Faster downloads, more efficient processing, and reduced memory usage
- **UI/UX Improvements**: Enhanced user interface, better accessibility, and improved user experience
- **Cloud Storage Providers**: Integration with additional cloud storage services beyond Azure
- **Music Service Integration**: Support for additional music streaming platforms
- **Testing**: Expanded test coverage and automated testing improvements
- **Documentation**: Improved guides, examples, and API documentation

## Development Setup

### Prerequisites
- Windows 10 1809+ or Windows 11 (x64 or ARM64)
- .NET 10 SDK
- Windows Developer Mode (Settings → System → For developers)
- WinApp CLI: `winget install --id Microsoft.WinAppCli --source winget`
- Git, plus Visual Studio 2026 or VS Code with the C# Dev Kit

### Getting Started
1. **Fork the repository** on GitHub
2. **Clone your fork**:
   ```powershell
   git clone https://github.com/yourusername/TTSMixmaster.git
   cd TTSMixmaster
   ```
3. **Build** (the solution has no AnyCPU configuration, so always pass a platform):
   ```powershell
   dotnet build TTSMixmaster.slnx -p:Platform=x64
   ```
4. **Fetch yt-dlp and ffmpeg** for local runs:
   ```powershell
   ./scripts/fetch-tools.ps1 -OutputFolder "$env:LOCALAPPDATA\TTSMixmaster\tools"   # add -Arch arm64 on ARM
   ```
5. **Run the app**: `cd src/TTSMixmaster.App; winapp run .` (or F5 in Visual Studio). Enter test credentials in the app's Settings page; nothing secret goes in the repo.

See [AGENTS.md](../AGENTS.md) for the project layout and conventions, and [docs/TESTING.md](../docs/TESTING.md) for the test layers.

### Development Workflow

1. **Create a feature branch**:
   ```bash
   git checkout -b feature/your-feature-name
   ```
2. **Make your changes** following the coding guidelines below
3. **Test your changes**:
   ```powershell
   dotnet test --project tests/TTSMixmaster.Core.Tests

   # Run the app (in Windows Sandbox, see AGENTS.md)
   cd src/TTSMixmaster.App
   winapp run . --on sandbox --detach
   ```
4. **Commit your changes**:
   ```bash
   git add .
   git commit -m "feat: brief description of your changes"   # conventional commits
   ```
5. **Push to your fork**:
   ```bash
   git push origin feature/your-feature-name
   ```
6. **Create a Pull Request** on GitHub

## Coding Guidelines

The full conventions are in [AGENTS.md](../AGENTS.md); `.editorconfig` and the .NET analyzers enforce
the mechanical ones during the build, which must stay at zero warnings.

### C# style
- File-scoped namespaces, nullable reference types on, `_camelCase` private fields
- `Async` suffix on async methods; `Is`/`Has`/`Can` prefixes on booleans
- Long-running work is `async` with `CancellationToken` (and `IProgress<T>` where it reports progress);
  never block on `Task.Result` or `.Wait()`
- Constructor injection; add an interface only where there's a real seam (HTTP, process launch,
  filesystem roots, clock). No static singletons
- Prefer simple, reliable code over clever code, with one obvious place for each concern

### Project structure
- `src/TTSMixmaster.Core`: everything that isn't UI (services, downloader, uploader, TTS formatter,
  configuration, file layout). It has no WinUI references, so it's unit-testable
- `src/TTSMixmaster.App`: the WinUI 3 app, MVVM with CommunityToolkit.Mvvm
- New music services implement the service contracts in `src/TTSMixmaster.Core/Contracts.cs` and register
  in `ServiceCatalog`; credentials come from Settings, never environment variables or source

### UI (WinUI 3)
- Read the `winui-design` skill (`.claude/skills/`, `.agents/skills/`) before writing XAML
- `[ObservableProperty]` / `[RelayCommand]`, `x:Bind` (not `Binding`), and
  `AutomationProperties.AutomationId` on every interactive control
- Show progress for long-running operations and readable, actionable errors
- Check light, dark and high-contrast themes; run the app in Windows Sandbox (see AGENTS.md)

### Dependencies
- Package versions live in `Directory.Packages.props` (Central Package Management); project files
  reference packages without versions
- Prefer first-party (Microsoft) libraries; explain any community package in the PR

## Testing

### Running Tests
```powershell
# All Core tests
dotnet test --project tests/TTSMixmaster.Core.Tests

# One test class or method
dotnet test --project tests/TTSMixmaster.Core.Tests -- --filter "FullyQualifiedName~YourServiceTests"
```

CI runs the same tests before every release; a failing test blocks it.

### Writing Tests
- Tests use **MSTest** (never xUnit or NUnit). Unit tests live in `tests/TTSMixmaster.Core.Tests`; see
  [docs/TESTING.md](../docs/TESTING.md) for the integration, App UI-thread and end-to-end layers
- Test Core services through their constructor-injected seams (HTTP, process launch,
  filesystem roots, clock) instead of calling real services
- Cover error conditions and edge cases, not just the happy path
- Use descriptive test names that state the behavior being checked

## Documentation

### Code Documentation
- XML doc comments (`/// <summary>`) on public types and members whose purpose isn't obvious
- Comments explain why, not what; document non-obvious decisions and external behavior
- Record significant design decisions as ADRs in `docs/adr/`

### User Documentation
- Update relevant documentation in the `docs/` directory
- Include setup instructions for new features
- Add troubleshooting information for common issues
- Update the service setup steps in INSTALLATION.md for new integrations

## Pull Request Guidelines

### Before Submitting
- Ensure your code follows the style guidelines
- Add or update tests for your changes
- Update documentation as needed
- Test your changes thoroughly
- Ensure all existing tests still pass

### Pull Request Description
Please include the following in your pull request description:

1. **Summary**: Brief description of what your PR does
2. **Motivation**: Why is this change needed?
3. **Changes**: List of specific changes made
4. **Testing**: How did you test your changes?
5. **Screenshots**: If UI changes, include before/after screenshots
6. **Breaking Changes**: List any breaking changes
7. **Documentation**: Note any documentation updates needed

### Example PR Template
```markdown
## Summary
Add support for Apple Music playlists

## Motivation
Users have requested Apple Music integration to complement existing Last.fm, YouTube, and Spotify support.

## Changes
- Added `AppleMusicService` implementing the Core service contracts
- Registered it in `ServiceCatalog`
- Added Apple Music settings to the Settings page
- Updated documentation with Apple Music setup instructions

## Testing
- Added unit tests for Apple Music service
- Tested integration with existing multi-service workflow
- Verified configuration and connection testing

## Screenshots
[Include screenshots if applicable]

## Breaking Changes
None

## Documentation
- Updated INSTALLATION.md with Apple Music API setup
```

## Code Review Process

### What to Expect
1. **Initial Review**: A maintainer will review your PR within a few days
2. **Feedback**: You may receive requests for changes or clarifications
3. **Iteration**: Make requested changes and push updates to your branch
4. **Approval**: Once approved, your PR will be merged

### Review Criteria
- Code quality and style compliance
- Test coverage and functionality
- Documentation completeness
- Backward compatibility
- Performance considerations
- Security implications

## Community Guidelines

### Communication
- Be respectful and constructive in all interactions
- Ask questions if you're unsure about anything
- Provide helpful feedback in code reviews
- Share knowledge and help other contributors

### Getting Help
- Check existing issues and documentation first
- Create detailed bug reports with reproduction steps
- Ask questions in GitHub discussions or issues
- Join community discussions about new features

## Recognition

Contributors will be recognized in the following ways:
- Listed in the project's contributor documentation
- Mentioned in release notes for significant contributions
- Given credit in commit messages and pull requests

## License

By contributing to TTSMixmaster, you agree that your contributions will be licensed under the same license as the project.

## Questions?

If you have any questions about contributing, please:
1. Check the documentation in the `docs/` directory
2. Search existing GitHub issues
3. Create a new issue with the "question" label
4. Reach out to the maintainers

Thank you for contributing to TTSMixmaster! 🎵
