using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.CodeAnalysis.Text;

namespace SharpTools.Tools.Services;

public class DocumentOperationsService : IDocumentOperationsService {
    private readonly ISolutionManager _solutionManager;
    private readonly ICodeModificationService _modificationService;
    private readonly ILogger<DocumentOperationsService> _logger;

    // Extensions for common code file types that can be formatted
    private static readonly HashSet<string> CodeFileExtensions = new(StringComparer.OrdinalIgnoreCase) {
        ".cs", ".csproj", ".sln", ".css", ".js", ".ts", ".jsx", ".tsx", ".html", ".cshtml", ".razor", ".yml", ".yaml",
        ".json", ".xml", ".config", ".md", ".fs", ".fsx", ".fsi", ".vb"
    };

    /// <summary>
    /// Phase 2 Optimization: File size threshold for streaming vs in-memory processing
    /// Files larger than this will be processed using streaming to avoid memory spikes
    /// </summary>
    private const long StreamingThresholdBytes = 50 * 1024 * 1024; // 50MB

    private static readonly HashSet<string> UnsafeDirectories = new(StringComparer.OrdinalIgnoreCase) {
        ".git", ".vs", "bin", "obj", "node_modules"
    };

    public DocumentOperationsService(
        ISolutionManager solutionManager,
        ICodeModificationService modificationService,
        ILogger<DocumentOperationsService> logger) {
        _solutionManager = solutionManager;
        _modificationService = modificationService;
        _logger = logger;
    }

    public async Task<(string contents, int lines)> ReadFileAsync(string filePath, bool omitLeadingSpaces, CancellationToken cancellationToken) {
        // Use FileInfo to avoid synchronous I/O blocking
        var fileInfo = new FileInfo(filePath);
        if (!fileInfo.Exists) {
            throw new FileNotFoundException($"File not found: {filePath}");
        }

        if (!IsPathReadable(filePath)) {
            throw new UnauthorizedAccessException($"Reading from this path is not allowed: {filePath}");
        }

        // Phase 2 Optimization: Use streaming for large files to prevent memory spikes
        var streamingFileInfo = new FileInfo(filePath);
        if (streamingFileInfo.Length > StreamingThresholdBytes) {
            _logger.LogDebug("Using streaming file processing for large file: {FilePath} ({FileSize:N0} bytes)", filePath, fileInfo.Length);
            return await ReadLargeFileStreamingAsync(filePath, omitLeadingSpaces, cancellationToken).ConfigureAwait(false);
        }

        // For smaller files, use the optimized in-memory approach
        string content = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        if (omitLeadingSpaces) {
            // Check cancellation before processing lines
            cancellationToken.ThrowIfCancellationRequested();

            for (int i = 0; i < lines.Length; i++) {
                lines[i] = TrimLeadingSpaces(lines[i]);
            }

            content = string.Join(Environment.NewLine, lines);
        }

        return (content, lines.Length);
    }
    public async Task<bool> WriteFileAsync(string filePath, string content, bool overwriteIfExists, CancellationToken cancellationToken) {
        var pathInfo = GetPathInfo(filePath);

        if (!pathInfo.IsWritable) {
            _logger.LogWarning("Path is not writable: {FilePath}. Reason: {Reason}",
                filePath, pathInfo.WriteRestrictionReason);
            throw new UnauthorizedAccessException($"Writing to this path is not allowed: {filePath}. {pathInfo.WriteRestrictionReason}");
        }

        // Use FileInfo to avoid synchronous I/O blocking
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Exists && !overwriteIfExists) {
            _logger.LogWarning("File already exists and overwrite not allowed: {FilePath}", filePath);
            return false;
        }

        // Ensure directory exists using DirectoryInfo
        string? directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) {
            var dirInfo = new DirectoryInfo(directory);
            if (!dirInfo.Exists) {
                dirInfo.Create();
            }
        }

        // Write the content to the file
        await File.WriteAllTextAsync(filePath, content, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("File {Operation} at {FilePath}",
            fileInfo.Exists ? "overwritten" : "created", filePath);


        // Find the most appropriate project for this file path
        var bestProject = FindMostAppropriateProject(filePath);
        if (!pathInfo.IsFormattable || bestProject is null || string.IsNullOrWhiteSpace(bestProject.FilePath)) {
            _logger.LogWarning("Added non-code file: {FilePath}", filePath);
            return true;
        }

        Project? legacyProject = null;
        bool isSdkStyleProject = await IsSDKStyleProjectAsync(bestProject.FilePath, cancellationToken).ConfigureAwait(false);
        if (isSdkStyleProject) {
            _logger.LogInformation("File added to SDK-style project: {ProjectPath}. Reloading Solution to pick up changes.", bestProject.FilePath);
            await _solutionManager.ReloadSolutionFromDiskAsync(cancellationToken).ConfigureAwait(false);
        } else {
            legacyProject = await TryAddFileToLegacyProjectAsync(filePath, bestProject, cancellationToken).ConfigureAwait(false);
        }
        var newSolution = legacyProject?.Solution ?? _solutionManager.CurrentSolution;
        var documentId = newSolution?.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();
        if (documentId is null) {
            _logger.LogWarning("Mystery file was not added to any project: {FilePath}", filePath);
            return false;
        }
        var document = newSolution?.GetDocument(documentId);
        if (document is null) {
            _logger.LogWarning("Document not found in solution: {FilePath}", filePath);
            return false;
        }
        // If it's a code file, try to format it
        if (await TryFormatFileAsync(document, cancellationToken).ConfigureAwait(false)) {
            _logger.LogInformation("File formatted: {FilePath}", filePath);
            return true;
        } else {
            _logger.LogWarning("Failed to format file: {FilePath}", filePath);
        }
        return true;
    }

    private async Task<Project?> TryAddFileToLegacyProjectAsync(string filePath, Project project, CancellationToken cancellationToken) {
        // Use FileInfo to avoid synchronous I/O blocking
        var fileInfo = new FileInfo(filePath);
        if (!_solutionManager.IsSolutionLoaded || !fileInfo.Exists) {
            return null;
        }

        try {
            // Get the document ID if the file is already in the solution
            var documentId = _solutionManager.CurrentSolution!.GetDocumentIdsWithFilePath(filePath).FirstOrDefault();

            // If the document is already in the solution, no need to add it again
            if (documentId != null) {
                _logger.LogInformation("File is already part of project: {FilePath}", filePath);
                return null;
            }

            // The file exists on disk but is not part of the project yet - add it to the solution in memory
            var fileName = Path.GetFileName(filePath);

            // Determine appropriate folder path relative to the project
            var projectDir = Path.GetDirectoryName(project.FilePath);
            var relativePath = string.Empty;
            var folders = Array.Empty<string>();

            if (!string.IsNullOrEmpty(projectDir)) {
                relativePath = Path.GetRelativePath(projectDir, filePath);
                var folderPath = Path.GetDirectoryName(relativePath);

                if (!string.IsNullOrEmpty(folderPath) && folderPath != ".") {
                    folders = folderPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }
            }

            _logger.LogInformation("Adding file to {ProjectName}: {FilePath}", project.Name, filePath);

            // Create SourceText from file content
            var fileContent = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);

            // Check cancellation before creating SourceText
            cancellationToken.ThrowIfCancellationRequested();

            var sourceText = SourceText.From(fileContent);

            // Add the document to the project in memory
            return project.AddDocument(fileName, sourceText, folders, filePath).Project;
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to add file {FilePath} to project", filePath);
            return null;
        }
    }

    private async Task<bool> IsSDKStyleProjectAsync(string projectFilePath, CancellationToken cancellationToken) {
        try {
            var content = await File.ReadAllTextAsync(projectFilePath, cancellationToken).ConfigureAwait(false);

            // Use XmlDocument for proper parsing
            var xmlDoc = new XmlDocument();
            xmlDoc.LoadXml(content);

            var projectNode = xmlDoc.DocumentElement;

            // Primary check - Look for Sdk attribute on Project element
            if (projectNode?.Attributes?["Sdk"] != null) {
                _logger.LogDebug("Project {ProjectPath} is SDK-style (has Sdk attribute)", projectFilePath);
                return true;
            }

            // Secondary check - Look for TargetFramework instead of TargetFrameworkVersion
            var targetFrameworkNode = xmlDoc.SelectSingleNode("//TargetFramework");
            if (targetFrameworkNode != null) {
                _logger.LogDebug("Project {ProjectPath} is SDK-style (uses TargetFramework)", projectFilePath);
                return true;
            }

            _logger.LogDebug("Project {ProjectPath} is classic-style (no SDK indicators found)", projectFilePath);
            return false;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Error determining project style for {ProjectPath}, assuming classic format", projectFilePath);
            return false;
        }
    }

    private Microsoft.CodeAnalysis.Project? FindMostAppropriateProject(string filePath) {
        if (!_solutionManager.IsSolutionLoaded) {
            return null;
        }

        var projects = _solutionManager.GetProjects().ToList();
        if (!projects.Any()) {
            return null;
        }

        // Find projects where the file path is under the project directory
        var projectsWithPath = new List<(Microsoft.CodeAnalysis.Project Project, int DirectoryLevel)>();

        foreach (var project in projects) {
            if (string.IsNullOrEmpty(project.FilePath)) {
                continue;
            }

            var projectDir = Path.GetDirectoryName(project.FilePath);
            if (string.IsNullOrEmpty(projectDir)) {
                continue;
            }

            if (filePath.StartsWith(projectDir, StringComparison.OrdinalIgnoreCase)) {
                // Calculate how many directories deep this file is from the project root
                var relativePath = filePath.Substring(projectDir.Length).TrimStart(Path.DirectorySeparatorChar);
                var directoryLevel = relativePath.Count(c => c == Path.DirectorySeparatorChar);

                projectsWithPath.Add((project, directoryLevel));
            }
        }

        // Return the project where the file is closest to the root
        // (smallest directory level means closer to project root)
        return projectsWithPath.OrderBy(p => p.DirectoryLevel).FirstOrDefault().Project;
    }

    public bool FileExists(string filePath) {
        // Use FileInfo to avoid synchronous I/O blocking
        return new FileInfo(filePath).Exists;
    }

    public bool IsPathReadable(string filePath) {
        var pathInfo = GetPathInfo(filePath);
        return pathInfo.IsReadable;
    }

    public bool IsPathWritable(string filePath) {
        var pathInfo = GetPathInfo(filePath);
        return pathInfo.IsWritable;
    }
    public bool IsCodeFile(string filePath) {
        if (string.IsNullOrEmpty(filePath)) {
            return false;
        }

        // First check if file exists but is not part of the solution - use FileInfo to avoid blocking
        var fileInfo = new FileInfo(filePath);
        if (fileInfo.Exists && !IsReferencedBySolution(filePath)) {
            return false;
        }

        // Check by extension
        var extension = Path.GetExtension(filePath);
        return !string.IsNullOrEmpty(extension) && CodeFileExtensions.Contains(extension);
    }
    public PathInfo GetPathInfo(string filePath) {
        if (string.IsNullOrEmpty(filePath)) {
            return new PathInfo {
                FilePath = filePath,
                Exists = false,
                IsWithinSolutionDirectory = false,
                IsReferencedBySolution = false,
                IsFormattable = false,
                WriteRestrictionReason = "Path is empty or null"
            };
        }

        // Use FileInfo to avoid synchronous I/O blocking
        var fileInfo = new FileInfo(filePath);
        bool exists = fileInfo.Exists;
        bool isWithinSolution = IsPathWithinSolutionDirectory(filePath);
        bool isReferenced = IsReferencedBySolution(filePath);
        bool isFormattable = IsCodeFile(filePath);
        string? projectId = FindMostAppropriateProject(filePath)?.Id.Id.ToString();

        string? writeRestrictionReason = null;

        // Check for unsafe directories
        if (ContainsUnsafeDirectory(filePath)) {
            writeRestrictionReason = "Path contains a protected directory (bin, obj, .git, etc.)";
        }

        // Check if file is outside solution
        if (!isWithinSolution) {
            writeRestrictionReason = "Path is outside the solution directory";
        }

        // Check if directory is read-only using DirectoryInfo to avoid blocking
        try {
            var directoryPath = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directoryPath)) {
                var dirInfo = new DirectoryInfo(directoryPath);
                if (dirInfo.Exists && (dirInfo.Attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly) {
                    writeRestrictionReason = "Directory is read-only";
                }
            }
        } catch {
            writeRestrictionReason = "Cannot determine directory permissions";
        }

        return new PathInfo {
            FilePath = filePath,
            Exists = exists,
            IsWithinSolutionDirectory = isWithinSolution,
            IsReferencedBySolution = isReferenced,
            IsFormattable = isFormattable,
            ProjectId = projectId,
            WriteRestrictionReason = writeRestrictionReason
        };
    }

    private bool IsPathWithinSolutionDirectory(string filePath) {
        if (!_solutionManager.IsSolutionLoaded) {
            return false;
        }

        string? solutionDirectory = Path.GetDirectoryName(_solutionManager.CurrentSolution?.FilePath);

        if (string.IsNullOrEmpty(solutionDirectory)) {
            return false;
        }

        return filePath.StartsWith(solutionDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private bool IsReferencedBySolution(string filePath) {
        // Use FileInfo to avoid synchronous I/O blocking
        var fileInfo = new FileInfo(filePath);
        if (!_solutionManager.IsSolutionLoaded || !fileInfo.Exists) {
            return false;
        }

        // Check if the file is directly referenced by a document in the solution
        if (_solutionManager.CurrentSolution!.GetDocumentIdsWithFilePath(filePath).Any()) {
            return true;
        }

        // TODO: Implement proper reference checking for assemblies, resources, etc.
        // This would require deeper MSBuild integration

        return false;
    }

    private bool ContainsUnsafeDirectory(string filePath) {
        // Check if the path contains any unsafe directory segments
        var normalizedPath = filePath.Replace('\\', '/');
        var pathSegments = normalizedPath.Split('/');

        return pathSegments.Any(segment => UnsafeDirectories.Contains(segment));
    }
    private async Task<bool> TryFormatFileAsync(Document document, CancellationToken cancellationToken) {
        try {
            var formattedDocument = await _modificationService.FormatDocumentAsync(document, cancellationToken).ConfigureAwait(false);
            // Apply the formatting changes
            var newSolution = formattedDocument.Project.Solution;
            await _modificationService.ApplyChangesAsync(newSolution, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Document {FilePath} formatted successfully", document.FilePath);
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to format file {FilePath}", document.FilePath);
            return false;
        }
    }

    /// <summary>
    /// Phase 2 Optimization: Stream large files to prevent memory spikes
    /// Processes files line-by-line instead of loading entire content into memory
    /// </summary>
    private async Task<(string contents, int lines)> ReadLargeFileStreamingAsync(string filePath, bool omitLeadingSpaces, CancellationToken cancellationToken) {
        using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
        using var reader = new StreamReader(fileStream, detectEncodingFromByteOrderMarks: true, bufferSize: 65536);

        var contentBuilder = new StringBuilder();
        var lineCount = 0;
        const int batchSize = 1000; // Process in batches for cancellation checking

        string? line;
        while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
            cancellationToken.ThrowIfCancellationRequested();

            // Apply leading space trimming if requested
            if (omitLeadingSpaces) {
                line = TrimLeadingSpaces(line);
            }

            // Add line to content
            if (lineCount > 0) {
                contentBuilder.AppendLine();
            }
            contentBuilder.Append(line);

            lineCount++;

            // Check cancellation periodically during large file processing
            if (lineCount % batchSize == 0) {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogTrace("Streaming file processing progress: {LineCount} lines processed from {FilePath}", lineCount, filePath);
            }
        }

        var content = contentBuilder.ToString();
        _logger.LogDebug("Completed streaming file processing: {FilePath} ({LineCount} lines, {ContentLength:N0} characters)", filePath, lineCount, content.Length);

        return (content, lineCount);
    }

    private static string TrimLeadingSpaces(string line) {
        int i = 0;
        while (i < line.Length && char.IsWhiteSpace(line[i])) {
            i++;
        }

        return i > 0 ? line.Substring(i) : line;
    }
}