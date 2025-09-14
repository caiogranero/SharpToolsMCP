# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

SharpTools is a Model Context Protocol (MCP) server that provides advanced C# code analysis and modification capabilities using Roslyn. It consists of three main projects:

- **SharpTools.Tools**: Core library with all analysis and modification services
- **SharpTools.SseServer**: HTTP server exposing tools via Server-Sent Events
- **SharpTools.StdioServer**: Standard I/O server for MCP client communication

The system is built around Fully Qualified Names (FQNs) for precise symbol navigation and uses Git integration for all code modifications.

## Build Commands

```bash
# Build the entire solution
dotnet build SharpTools.sln

# Build specific project
dotnet build SharpTools.Tools/SharpTools.Tools.csproj

# Build in Release configuration
dotnet build SharpTools.sln -c Release
```

## Running the Servers

### SSE Server (for remote clients)
```bash
cd SharpTools.SseServer
dotnet run

# With custom port and logging
dotnet run -- --port 3005 --log-file ./logs/server.log --log-level Debug
```

### Stdio Server (for MCP clients)
```bash
cd SharpTools.StdioServer
dotnet run

# With logging
dotnet run -- --log-directory /var/log/sharptools/ --log-level Debug
```

## Architecture

### Core Services (SharpTools.Tools/Services/)
- **SolutionManager**: Manages Roslyn workspace and solution loading
- **CodeAnalysisService**: Provides symbol analysis, references, implementations
- **CodeModificationService**: Handles all code changes with Git integration
- **GitService**: Manages automated branching and commits for modifications
- **DocumentOperationsService**: File operations with solution context
- **ComplexityAnalysisService**: Analyzes code complexity metrics

### Tool Categories (SharpTools.Tools/Mcp/Tools/)
- **SolutionTools**: `LoadSolution`, `LoadProject` - Entry points for workspace initialization
- **AnalysisTools**: Symbol inspection, references, implementations, complexity analysis
- **DocumentTools**: File reading/writing operations
- **ModificationTools**: Code changes (add/overwrite/rename/move members, find/replace)

### Key Design Principles
- All modifications create timestamped `sharptools/` Git branches
- Every code change is automatically committed with descriptive messages
- Token-efficient operation by omitting indentation in returned code
- FQN-based navigation minimizes reading unrelated code
- Comprehensive source resolution (local files, SourceLink, embedded PDBs, decompilation)

### Entry Point Workflow
1. Always start with `SharpTool_LoadSolution` using the .sln file path
2. Use `SharpTool_LoadProject` to get detailed project structure overview
3. Navigate using FQNs and SharpTools analysis rather than file-based exploration

## EditorConfig Support

The project respects `.editorconfig` settings for consistent formatting. The solution includes comprehensive editor configuration for C# code style.

## Git Integration

All code modifications automatically:
- Create dedicated branches under `sharptools/` namespace
- Commit changes with descriptive messages
- Provide undo functionality via `SharpTool_Undo`

The system ensures clean separation of AI-generated changes from main development branches.