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
   dotnet test tests/TTSMixmaster.Core.Tests

   # Run the app (in Windows Sandbox, see AGENTS.md)
   cd src/TTSMixmaster.App
   winapp run . --on sandbox --detach
   ```
4. **Commit your changes**:
   ```bash
   git add .
   git commit -m "Add: Brief description of your changes"
   ```
5. **Push to your fork**:
   ```bash
   git push origin feature/your-feature-name
   ```
6. **Create a Pull Request** on GitHub

## Coding Guidelines

### Python Code Style
- Follow [PEP 8](https://pep8.org/) style guidelines
- Use type hints for all function parameters and return values
- Use dataclasses for data structures where appropriate
- Include docstrings for all classes and functions
- Use meaningful variable and function names

### Code Organization
- Follow the existing module structure in `src/`
- Keep related functionality together in appropriate modules
- Use the service abstraction layer for new music service integrations
- Handle exceptions gracefully with proper error messages
- Use logging for debugging and information messages

### Example Code Style
```python
from typing import List, Optional
from dataclasses import dataclass

@dataclass
class Track:
    """Represents a music track with metadata."""
    artist: str
    title: str
    duration: Optional[int] = None
    url: Optional[str] = None

def fetch_tracks(service_name: str, limit: int = 50) -> List[Track]:
    """
    Fetch tracks from a music service.
    
    Args:
        service_name: Name of the music service
        limit: Maximum number of tracks to fetch
        
    Returns:
        List of Track objects
        
    Raises:
        ServiceError: If the service is unavailable
    """
    try:
        # Implementation here
        pass
    except Exception as e:
        logger.error(f"Failed to fetch tracks from {service_name}: {e}")
        raise ServiceError(f"Service {service_name} unavailable") from e
```

### GUI Development
- Use CustomTkinter for UI components
- Follow the existing tab structure and layout patterns
- Include progress indicators for long-running operations
- Provide clear error messages and user feedback
- Test UI changes on different screen sizes and themes

### API Integration
- Follow the service abstraction pattern in `src/api/base_service.py`
- Implement proper error handling and rate limiting
- Use environment variables for API credentials
- Include connection testing functionality
- Document API requirements and setup steps

## Testing

### Running Tests
```powershell
# All Core tests
dotnet test tests/TTSMixmaster.Core.Tests

# One test class or method
dotnet test tests/TTSMixmaster.Core.Tests --filter "FullyQualifiedName~YourServiceTests"
```

CI runs the same tests before every release; a failing test blocks it.

### Writing Tests
- Tests use **MSTest** and live in `tests/TTSMixmaster.Core.Tests`
- Test Core services through their constructor-injected seams (HTTP, process launch,
  filesystem roots, clock) instead of calling real services
- Cover error conditions and edge cases, not just the happy path
- Use descriptive test names that state the behavior being checked

## Documentation

### Code Documentation
- Include docstrings for all public functions and classes
- Use Google-style docstrings for consistency
- Document complex algorithms and business logic
- Include examples in docstrings where helpful

### User Documentation
- Update relevant documentation in the `docs/` directory
- Include setup instructions for new features
- Add troubleshooting information for common issues
- Update API references for new integrations

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
- Added `AppleMusicService` class implementing the base service interface
- Updated service manager to include Apple Music
- Added Apple Music configuration options to GUI
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
- Added Apple Music section to API_REFERENCE.md
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
