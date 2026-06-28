# Testing the MSI Installer Fix

This document explains how to test the MSI installer fix locally.

## Problem Summary

The MSI installer was failing to open because it was missing the `_internal` directory that PyInstaller creates with all the Python dependencies. This caused the application to immediately close on launch.

## Solution Overview

1. **Created PyInstaller spec file** (`TTSMixmaster.spec`) with comprehensive dependency collection
2. **Updated GitHub workflow** to use the spec file and validate build completeness
3. **Enhanced WiX MSI generation** to use heat tool for complete file harvesting

## Local Testing

### Prerequisites
- Windows 10/11 with Python 3.8+
- PyInstaller installed (`pip install pyinstaller`)
- All project dependencies installed (`pip install .`)

### Build Testing
```bash
# Test the PyInstaller spec file
pyinstaller TTSMixmaster.spec --clean --noconfirm

# Verify the build
dir dist\TTSMixmaster
dir dist\TTSMixmaster\_internal

# The _internal directory should contain:
# - base_library.zip
# - python*.dll files  
# - Various .pyd files
# - Dependency libraries
```

### Validation Checklist
- [ ] `dist\TTSMixmaster\TTSMixmaster.exe` exists
- [ ] `dist\TTSMixmaster\_internal` directory exists
- [ ] `_internal` directory contains `base_library.zip`
- [ ] `_internal` directory contains Python DLL files
- [ ] Total build size is > 30MB (indicates dependencies are included)
- [ ] Application can start without immediate crash

## Manual MSI Testing

If you have WiX Toolset v4 installed:

```bash
# Generate file manifest
wix heat dir "dist\TTSMixmaster" -out "HarvestedFiles.wxs" -ag -cg HarvestedFiles -dr INSTALLFOLDER -srd -sfrag

# Build MSI (requires WiX setup)
wix build installer\TTSMixmaster.wxs HarvestedFiles.wxs -out TTSMixmaster-Test.msi -dSourceDir="dist\TTSMixmaster"
```

## Expected Fix Results

After the fix:
1. **PyInstaller build** creates complete `_internal` directory with all dependencies
2. **MSI installer** includes all files from PyInstaller output using WiX heat tool
3. **Installed application** launches successfully from the installation directory
4. **No missing DLL errors** or immediate crashes

## Common Issues

- **Permission errors**: Run build commands as Administrator if needed
- **Missing dependencies**: Ensure dependencies are installed (`pip install .`)
- **Path issues**: Use full paths if relative paths don't work
- **Windows Defender**: May quarantine executable during build/test

## Monitoring

The GitHub workflow now includes comprehensive validation:
- Verifies `_internal` directory existence and content
- Reports build sizes and file counts
- Lists critical dependency files
- Validates total build size is reasonable