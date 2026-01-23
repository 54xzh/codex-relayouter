// FilePathMarkdownRenderer：将 Markdown 行内代码中的文件路径渲染为可点击的文件名（基于 cwd 解析相对路径）。
using CommunityToolkit.Common.Parsers.Markdown;
using CommunityToolkit.Common.Parsers.Markdown.Blocks;
using CommunityToolkit.Common.Parsers.Markdown.Inlines;
using CommunityToolkit.Common.Parsers.Markdown.Render;
using CommunityToolkit.WinUI.UI.Controls.Markdown.Render;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace codex_bridge.Markdown;

public sealed class FilePathMarkdownRenderer : MarkdownRenderer
{
    private const double InlineCodeBaselineOffset = 4d;
    private const string InlineCodePathIconGlyphMarkdown = "\uE8A5"; // Document
    private const string InlineCodePathIconGlyphCode = "\uE943"; // Code
    private const string InlineCodePathIconGlyphFolder = "\uE8B7"; // Folder
    private const string InlineCodePathIconGlyphGeneric = "\uE7C3"; // Page

    private static readonly InputCursor InlineCodeHandCursor = InputSystemCursor.Create(InputSystemCursorShape.Hand);

    private static readonly HashSet<string> InlineCodeCodeFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c",
        ".cc",
        ".cpp",
        ".cs",
        ".csproj",
        ".css",
        ".cxx",
        ".dart",
        ".fs",
        ".fsi",
        ".fsproj",
        ".fsx",
        ".go",
        ".h",
        ".hh",
        ".hpp",
        ".htm",
        ".html",
        ".hxx",
        ".ini",
        ".java",
        ".js",
        ".json",
        ".jsx",
        ".kt",
        ".kts",
        ".less",
        ".lua",
        ".m",
        ".mm",
        ".mjs",
        ".nuspec",
        ".php",
        ".props",
        ".ps1",
        ".psd1",
        ".psm1",
        ".py",
        ".rb",
        ".rs",
        ".scss",
        ".sh",
        ".sln",
        ".slnx",
        ".sql",
        ".swift",
        ".targets",
        ".toml",
        ".ts",
        ".tsx",
        ".vb",
        ".vbproj",
        ".xaml",
        ".xml",
        ".yaml",
        ".yml",
    };

    public FilePathMarkdownRenderer(
        MarkdownDocument document,
        ILinkRegister linkRegister,
        IImageResolver imageResolver,
        ICodeBlockResolver codeBlockResolver)
        : base(document, linkRegister, imageResolver, codeBlockResolver)
    {
    }

    protected override void RenderCodeRun(CodeInline element, IRenderContext context)
    {
        var localContext = context as InlineRenderContext;
        if (localContext == null)
        {
            base.RenderCodeRun(element, context);
            return;
        }

        var inlineCollection = localContext.InlineCollection;

        var collapsed = CollapseWhitespace(context, element.Text);
        var raw = (collapsed ?? string.Empty).Trim();

        var hasResolvedPath = InlineCodeFilePath.TryResolveExistingPath(raw, out var resolvedPath, out var displayName, out var displayTooltip);
        var isFileCandidate = hasResolvedPath || InlineCodeFilePath.TryGetStrongPathCandidate(raw, out displayName, out displayTooltip);

        var background = InlineCodeBackground ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 238, 242, 246));
        var foreground = InlineCodeForeground ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
        var padding = InlineCodePadding == default ? new Thickness(6, 2, 6, 2) : InlineCodePadding;
        var margin = InlineCodeMargin == default ? new Thickness(2, 0, 2, 0) : InlineCodeMargin;
        var codeFontFamily = InlineCodeFontFamily ?? new FontFamily("Consolas");

        var text = string.IsNullOrWhiteSpace(collapsed) ? raw : collapsed;
        var tooltip = string.Empty;

        if (isFileCandidate)
        {
            background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 230, 244, 255));
            foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 9, 105, 218));
            text = displayName;
            tooltip = displayTooltip;
        }

        // Avoid a crash if the current inline is inside a hyperlink.
        // This happens when using inline code blocks like [`SomeCode`](https://www.foo.bar).
        if (localContext.Parent is Hyperlink)
        {
            if (isFileCandidate)
            {
                var iconGlyph = GetInlineCodePathIconGlyph(raw, hasResolvedPath, resolvedPath, displayName);
                var iconRun = new Run
                {
                    Text = iconGlyph + " ",
                    FontFamily = new FontFamily("Segoe Fluent Icons"),
                    Foreground = foreground,
                };

                if (localContext.WithinItalics)
                {
                    iconRun.FontStyle = Windows.UI.Text.FontStyle.Italic;
                }

                if (localContext.WithinBold)
                {
                    iconRun.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
                }

                inlineCollection.Add(iconRun);
            }

            Run run = new Run
            {
                Text = text,
                FontFamily = codeFontFamily,
                Foreground = foreground,
            };

            if (localContext.WithinItalics)
            {
                run.FontStyle = Windows.UI.Text.FontStyle.Italic;
            }

            if (localContext.WithinBold)
            {
                run.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
            }

            inlineCollection.Add(run);
            return;
        }

        var textBlock = CreateTextBlock(localContext);
        textBlock.Text = text;
        textBlock.FontFamily = codeFontFamily;
        textBlock.Foreground = foreground;
        textBlock.IsHitTestVisible = false;

        if (localContext.WithinItalics)
        {
            textBlock.FontStyle = Windows.UI.Text.FontStyle.Italic;
        }

        if (localContext.WithinBold)
        {
            textBlock.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        }

        UIElement content = textBlock;
        if (isFileCandidate)
        {
            var iconGlyph = GetInlineCodePathIconGlyph(raw, hasResolvedPath, resolvedPath, displayName);
            var icon = new FontIcon
            {
                Glyph = iconGlyph,
                Foreground = foreground,
                FontSize = Math.Max(10, textBlock.FontSize - 1),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 0,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            panel.Children.Add(icon);
            panel.Children.Add(textBlock);
            content = panel;
        }

        var border = new Border
        {
            Background = background,
            BorderThickness = new Thickness(0),
            BorderBrush = null,
            Padding = padding,
            CornerRadius = new CornerRadius(4),
            Child = content,
            IsTapEnabled = false,
            IsHitTestVisible = false,
        };

        if (isFileCandidate)
        {
            var host = new HandCursorPresenter
            {
                Margin = margin,
                Child = border,
                IsTapEnabled = true,
                IsHitTestVisible = true,
            };

            // Aligns content in InlineUI, see https://social.msdn.microsoft.com/Forums/silverlight/en-US/48b5e91e-efc5-4768-8eaf-f897849fcf0b/richtextbox-inlineuicontainer-vertical-alignment-issue?forum=silverlightarchieve
            host.RenderTransform = new TranslateTransform { Y = InlineCodeBaselineOffset };

            if (!string.IsNullOrWhiteSpace(tooltip))
            {
                ToolTipService.SetToolTip(host, tooltip);
            }

            var lastTapTimestamp = 0L;
            var tapDebounceTicks = Stopwatch.Frequency / 3; // ~333ms

            host.Tapped += (sender, args) =>
            {
                args.Handled = true;

                var now = Stopwatch.GetTimestamp();
                if (lastTapTimestamp != 0 && now - lastTapTimestamp < tapDebounceTicks)
                {
                    return;
                }

                lastTapTimestamp = now;

                if (hasResolvedPath)
                {
                    WorkspaceFileOpener.TryOpenPath(resolvedPath);
                    return;
                }

                if (InlineCodeFilePath.TryResolveExistingPath(raw, out var currentResolvedPath, out _, out _))
                {
                    WorkspaceFileOpener.TryOpenPath(currentResolvedPath);
                }
            };

            inlineCollection.Add(new InlineUIContainer { Child = host });
            return;
        }

        border.Margin = margin;

        // Aligns content in InlineUI, see https://social.msdn.microsoft.com/Forums/silverlight/en-US/48b5e91e-efc5-4768-8eaf-f897849fcf0b/richtextbox-inlineuicontainer-vertical-alignment-issue?forum=silverlightarchieve
        border.RenderTransform = new TranslateTransform { Y = InlineCodeBaselineOffset };

        inlineCollection.Add(new InlineUIContainer { Child = border });
    }

    private sealed class HandCursorPresenter : Panel
    {
        public HandCursorPresenter()
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            ProtectedCursor = InlineCodeHandCursor;
        }

        public UIElement? Child
        {
            get => Children.Count > 0 ? Children[0] : null;
            set
            {
                Children.Clear();
                if (value is not null)
                {
                    Children.Add(value);
                }
            }
        }

        protected override Windows.Foundation.Size MeasureOverride(Windows.Foundation.Size availableSize)
        {
            var child = Child;
            if (child is null)
            {
                return new Windows.Foundation.Size(0, 0);
            }

            child.Measure(availableSize);
            return child.DesiredSize;
        }

        protected override Windows.Foundation.Size ArrangeOverride(Windows.Foundation.Size finalSize)
        {
            var child = Child;
            if (child is not null)
            {
                child.Arrange(new Windows.Foundation.Rect(0, 0, finalSize.Width, finalSize.Height));
            }

            return finalSize;
        }
    }

    private static string GetInlineCodePathIconGlyph(string raw, bool hasResolvedPath, string resolvedPath, string displayName)
    {
        if (hasResolvedPath
            && !string.IsNullOrWhiteSpace(resolvedPath)
            && !PathUtilities.IsUncPath(resolvedPath)
            && Directory.Exists(resolvedPath))
        {
            return InlineCodePathIconGlyphFolder;
        }

        var extension = string.Empty;
        if (hasResolvedPath && !string.IsNullOrWhiteSpace(resolvedPath))
        {
            extension = Path.GetExtension(resolvedPath);
        }

        if (string.IsNullOrWhiteSpace(extension) && !string.IsNullOrWhiteSpace(displayName))
        {
            extension = Path.GetExtension(displayName);
        }

        if (string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".markdown", StringComparison.OrdinalIgnoreCase)
            || string.Equals(extension, ".mdx", StringComparison.OrdinalIgnoreCase))
        {
            return InlineCodePathIconGlyphMarkdown;
        }

        if (!string.IsNullOrWhiteSpace(extension) && InlineCodeCodeFileExtensions.Contains(extension))
        {
            return InlineCodePathIconGlyphCode;
        }

        var trimmed = TrimInlineCodePathForIcon(raw);
        if (trimmed.EndsWith("/", StringComparison.Ordinal) || trimmed.EndsWith("\\", StringComparison.Ordinal))
        {
            return InlineCodePathIconGlyphFolder;
        }

        if (!hasResolvedPath
            && (trimmed.Contains("/", StringComparison.Ordinal) || trimmed.Contains("\\", StringComparison.Ordinal))
            && string.IsNullOrWhiteSpace(Path.GetExtension(trimmed.TrimEnd('/', '\\'))))
        {
            return InlineCodePathIconGlyphFolder;
        }

        return InlineCodePathIconGlyphGeneric;
    }

    private static string TrimInlineCodePathForIcon(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var trimmed = raw.Trim();
        trimmed = trimmed.Trim('"', '\'', '(', ')', '[', ']', '{', '}', '<', '>');
        trimmed = trimmed.TrimEnd('.', ',', ';', ':', '!', '?');
        return trimmed.Trim();
    }

    protected override void RenderListElement(ListBlock element, IRenderContext context)
    {
        if (context is not UIElementCollectionRenderContext localContext)
        {
            base.RenderListElement(element, context);
            return;
        }

        var previousParagraphMargin = ParagraphMargin;
        ParagraphMargin = new Thickness(previousParagraphMargin.Left, 0, previousParagraphMargin.Right, previousParagraphMargin.Bottom);

        var elements = localContext.BlockUIElementCollection;
        var startingElementIndex = elements.Count;

        try
        {
            base.RenderListElement(element, context);
        }
        finally
        {
            ParagraphMargin = previousParagraphMargin;
        }

        for (var index = startingElementIndex; index < elements.Count; index++)
        {
            if (elements[index] is Grid grid)
            {
                ApplyInlineCodeBulletAlignment(grid, element);
            }
        }
    }

    private static void ApplyInlineCodeBulletAlignment(Grid grid, ListBlock list)
    {
        foreach (var child in grid.Children)
        {
            if (child is not TextBlock bullet)
            {
                continue;
            }

            if (Grid.GetColumn(bullet) != 0)
            {
                continue;
            }

            var rowIndex = Grid.GetRow(bullet);
            if (rowIndex < 0 || rowIndex >= list.Items.Count)
            {
                continue;
            }

            bullet.RenderTransform = ListItemStartsWithCodeInline(list.Items[rowIndex])
                ? new TranslateTransform { Y = InlineCodeBaselineOffset }
                : null;
        }
    }

    private static bool ListItemStartsWithCodeInline(ListItemBlock listItem)
    {
        if (listItem.Blocks is null || listItem.Blocks.Count == 0)
        {
            return false;
        }

        if (listItem.Blocks[0] is not ParagraphBlock { Inlines: { } inlines } || inlines.Count == 0)
        {
            return false;
        }

        for (var index = 0; index < inlines.Count; index++)
        {
            var inline = inlines[index];
            if (inline is TextRunInline textRun && string.IsNullOrWhiteSpace(textRun.Text))
            {
                continue;
            }

            return inline is CodeInline;
        }

        return false;
    }
}

internal static class PathUtilities
{
    public static bool IsUncPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var trimmed = path.Trim();

        if (trimmed.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmed.StartsWith(@"\\", StringComparison.Ordinal) && !trimmed.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }
}

internal static class InlineCodeFilePath
{
    private const string FileNameCacheAmbiguous = "__AMBIGUOUS__";
    private static readonly Dictionary<string, string?> FileNameResolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object FileNameResolutionCacheLock = new();

    public static bool TryResolveExistingPath(string raw, out string resolvedPath, out string displayName, out string tooltip)
    {
        resolvedPath = string.Empty;
        displayName = string.Empty;
        tooltip = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = TrimCommonWrappers(raw.Trim());
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
        {
            return false;
        }

        trimmed = TrimCommonTrailingPunctuation(trimmed);

        var pathText = trimmed;
        int? line = null;
        int? column = null;

        if (TryParseFileReferenceSuffix(trimmed, out var suffixPath, out var suffixLine, out var suffixColumn))
        {
            pathText = suffixPath;
            line = suffixLine;
            column = suffixColumn;
        }

        if (TryResolveExistingFullPath(pathText, out var resolvedFullPath))
        {
            resolvedPath = resolvedFullPath;
            displayName = GetFileOrDirectoryName(resolvedFullPath);
            tooltip = string.IsNullOrWhiteSpace(line?.ToString())
                ? resolvedFullPath
                : column is null
                    ? $"{resolvedFullPath}:{line}"
                    : $"{resolvedFullPath}:{line}:{column}";

            return !string.IsNullOrWhiteSpace(displayName);
        }

        return false;
    }

    public static bool TryGetStrongPathCandidate(string raw, out string displayName, out string tooltip)
    {
        displayName = string.Empty;
        tooltip = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        var trimmed = TrimCommonWrappers(raw.Trim());
        if (string.IsNullOrWhiteSpace(trimmed) || trimmed.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
        {
            return false;
        }

        trimmed = TrimCommonTrailingPunctuation(trimmed);

        var pathText = trimmed;
        if (TryParseFileReferenceSuffix(trimmed, out var suffixPath, out _, out _))
        {
            pathText = suffixPath;
        }

        if (!IsStrongPathText(pathText))
        {
            return false;
        }

        displayName = GetFileOrDirectoryName(pathText);
        tooltip = trimmed;
        return !string.IsNullOrWhiteSpace(displayName);
    }

    private static bool TryResolveExistingFullPath(string pathText, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(pathText))
        {
            return false;
        }

        var candidates = GetCandidatePaths(pathText);
        var workingDirectory = codex_bridge.App.ConnectionService.WorkingDirectory;
        var sessionCwd = codex_bridge.App.SessionState.CurrentSessionCwd;
        var gitRoot = PathUtilities.IsUncPath(workingDirectory) ? null : TryFindGitRoot(workingDirectory);
        var sessionGitRoot = PathUtilities.IsUncPath(sessionCwd) ? null : TryFindGitRoot(sessionCwd);

        foreach (var candidate in candidates)
        {
            if (TryResolveCandidateFullPath(candidate, workingDirectory, gitRoot, sessionCwd, sessionGitRoot, out var resolved))
            {
                fullPath = resolved;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> GetCandidatePaths(string pathText)
    {
        var trimmed = TrimCommonWrappers(pathText.Trim());
        trimmed = TrimCommonTrailingPunctuation(trimmed);

        if (trimmed.Length >= 2
            && (trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal)))
        {
            return new[] { trimmed, trimmed[2..] };
        }

        if (trimmed.Length >= 2
            && (trimmed.StartsWith("a\\", StringComparison.Ordinal) || trimmed.StartsWith("b\\", StringComparison.Ordinal)))
        {
            return new[] { trimmed, trimmed[2..] };
        }

        return new[] { trimmed };
    }

    private static bool TryResolveCandidateFullPath(string candidatePath, string? workingDirectory, string? gitRoot, string? sessionCwd, string? sessionGitRoot, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(candidatePath))
        {
            return false;
        }

        if (candidatePath.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (candidatePath.StartsWith("--", StringComparison.Ordinal))
        {
            return false;
        }

        if (TryExpandHomePath(candidatePath, out var expandedHomePath)
            && TryResolveAndValidateFullPath(expandedHomePath, baseDirectory: null, out var expandedHomeFullPath))
        {
            fullPath = expandedHomeFullPath;
            return true;
        }

        if (Path.IsPathRooted(candidatePath)
            && TryResolveAndValidateFullPath(candidatePath, baseDirectory: null, out var rootedFullPath))
        {
            fullPath = rootedFullPath;
            return true;
        }

        if (TryResolveAndValidateFullPath(candidatePath, workingDirectory, out var workingDirectoryFullPath))
        {
            fullPath = workingDirectoryFullPath;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(gitRoot)
            && !string.Equals(gitRoot, workingDirectory, StringComparison.OrdinalIgnoreCase)
            && TryResolveAndValidateFullPath(candidatePath, gitRoot, out var gitRootFullPath))
        {
            fullPath = gitRootFullPath;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(sessionCwd)
            && !string.Equals(sessionCwd, workingDirectory, StringComparison.OrdinalIgnoreCase)
            && TryResolveAndValidateFullPath(candidatePath, sessionCwd, out var sessionCwdFullPath))
        {
            fullPath = sessionCwdFullPath;
            return true;
        }

        if (!string.IsNullOrWhiteSpace(sessionGitRoot)
            && !string.Equals(sessionGitRoot, workingDirectory, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(sessionGitRoot, gitRoot, StringComparison.OrdinalIgnoreCase)
            && TryResolveAndValidateFullPath(candidatePath, sessionGitRoot, out var sessionGitRootFullPath))
        {
            fullPath = sessionGitRootFullPath;
            return true;
        }

        if (TryResolveBareFileName(candidatePath, out var resolvedByName, gitRoot, workingDirectory, sessionGitRoot, sessionCwd))
        {
            fullPath = resolvedByName;
            return true;
        }

        return false;
    }

    private static bool TryResolveBareFileName(string value, out string fullPath, params string?[] roots)
    {
        fullPath = string.Empty;

        if (!IsBareFileNameCandidate(value, out var fileName))
        {
            return false;
        }

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || PathUtilities.IsUncPath(root) || !Directory.Exists(root))
            {
                continue;
            }

            if (TryFindUniqueFileByName(root, fileName, out var foundFullPath))
            {
                fullPath = foundFullPath;
                return true;
            }
        }

        return false;
    }

    private static bool IsBareFileNameCandidate(string value, out string fileName)
    {
        fileName = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Length < 3 || trimmed.Length > 128)
        {
            return false;
        }

        if (trimmed.Contains(' ', StringComparison.Ordinal)
            || trimmed.Contains('/', StringComparison.Ordinal)
            || trimmed.Contains('\\', StringComparison.Ordinal)
            || trimmed.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        var dotIndex = trimmed.LastIndexOf('.');
        if (dotIndex <= 0 || dotIndex >= trimmed.Length - 1)
        {
            return false;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return false;
        }

        fileName = trimmed;
        return true;
    }

    private static bool TryFindUniqueFileByName(string root, string fileName, out string fullPath)
    {
        fullPath = string.Empty;

        var cacheKey = $"{root}|{fileName}";
        lock (FileNameResolutionCacheLock)
        {
            if (FileNameResolutionCache.TryGetValue(cacheKey, out var cached))
            {
                if (string.Equals(cached, FileNameCacheAmbiguous, StringComparison.Ordinal))
                {
                    return false;
                }

                if (!string.IsNullOrWhiteSpace(cached) && File.Exists(cached))
                {
                    fullPath = cached;
                    return true;
                }

                if (cached is null)
                {
                    return false;
                }
            }
        }

        string? match = null;
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();
            if (ShouldSkipDirectory(dir))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(dir, fileName, SearchOption.TopDirectoryOnly))
                {
                    if (match is null)
                    {
                        match = file;
                    }
                    else if (!string.Equals(match, file, StringComparison.OrdinalIgnoreCase))
                    {
                        CacheFileNameResolution(cacheKey, FileNameCacheAmbiguous);
                        return false;
                    }
                }

                foreach (var subDir in Directory.EnumerateDirectories(dir, "*", SearchOption.TopDirectoryOnly))
                {
                    stack.Push(subDir);
                }
            }
            catch
            {
            }
        }

        if (match is null || !File.Exists(match))
        {
            CacheFileNameResolution(cacheKey, null);
            return false;
        }

        CacheFileNameResolution(cacheKey, match);
        fullPath = match;
        return true;
    }

    private static void CacheFileNameResolution(string cacheKey, string? value)
    {
        lock (FileNameResolutionCacheLock)
        {
            FileNameResolutionCache[cacheKey] = value;
        }
    }

    private static bool ShouldSkipDirectory(string fullPath)
    {
        try
        {
            var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(fullPath));
            return string.Equals(name, ".git", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, ".vs", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "node_modules", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveAndValidateFullPath(string path, string? baseDirectory, out string fullPath)
    {
        fullPath = string.Empty;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (path.IndexOfAny(new[] { '\r', '\n', '\t' }) >= 0)
        {
            return false;
        }

        try
        {
            fullPath = string.IsNullOrWhiteSpace(baseDirectory)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(path, baseDirectory);
        }
        catch
        {
            return false;
        }

        if (PathUtilities.IsUncPath(fullPath))
        {
            return true;
        }

        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            return false;
        }

        var baseName = GetFileOrDirectoryName(baseDirectory);
        if (string.IsNullOrWhiteSpace(baseName))
        {
            return false;
        }

        if (path.Length <= baseName.Length + 1)
        {
            return false;
        }

        if (!path.StartsWith(baseName + "/", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWith(baseName + "\\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var stripped = path[(baseName.Length + 1)..];
        try
        {
            fullPath = Path.GetFullPath(stripped, baseDirectory);
        }
        catch
        {
            return false;
        }

        if (PathUtilities.IsUncPath(fullPath))
        {
            return true;
        }

        return File.Exists(fullPath) || Directory.Exists(fullPath);
    }

    private static string GetFileOrDirectoryName(string fullPath)
    {
        try
        {
            var trimmed = Path.TrimEndingDirectorySeparator(fullPath);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? trimmed : name;
        }
        catch
        {
            return fullPath;
        }
    }

    private static bool TryExpandHomePath(string value, out string expanded)
    {
        expanded = string.Empty;

        if (string.IsNullOrWhiteSpace(value) || value.Length < 2 || value[0] != '~')
        {
            return false;
        }

        var separator = value[1];
        if (separator != '/' && separator != '\\')
        {
            return false;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(home))
        {
            return false;
        }

        expanded = Path.Combine(home, value[2..].Replace('/', Path.DirectorySeparatorChar));
        return true;
    }

    private static string? TryFindGitRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
        {
            return null;
        }

        if (PathUtilities.IsUncPath(startDirectory))
        {
            return null;
        }

        try
        {
            var current = new DirectoryInfo(startDirectory);
            while (current is not null)
            {
                var gitDir = Path.Combine(current.FullName, ".git");
                if (Directory.Exists(gitDir) || File.Exists(gitDir))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }
        }
        catch
        {
        }

        return null;
    }

    private static string TrimCommonTrailingPunctuation(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.TrimEnd('.', ',', ';', ')', ']', '}', '>', '，', '。', '；', '）', '】', '〕', '》');
    }

    private static string TrimCommonWrappers(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        var trimmed = value.Trim();
        var changed = true;

        while (changed && trimmed.Length >= 2)
        {
            changed = false;
            trimmed = TrimQuotes(trimmed);

            if (trimmed.Length < 2)
            {
                break;
            }

            var first = trimmed[0];
            var last = trimmed[^1];
            if (IsWrapperPair(first, last))
            {
                trimmed = trimmed[1..^1].Trim();
                changed = true;
            }
        }

        trimmed = trimmed
            .TrimStart('(', '[', '{', '<', '（', '【', '〔', '《', '“', '‘')
            .TrimEnd(')', ']', '}', '>', '）', '】', '〕', '》', '”', '’');

        return trimmed;
    }

    private static bool IsWrapperPair(char first, char last) =>
        (first == '(' && last == ')')
        || (first == '[' && last == ']')
        || (first == '{' && last == '}')
        || (first == '<' && last == '>')
        || (first == '（' && last == '）')
        || (first == '【' && last == '】')
        || (first == '〔' && last == '〕')
        || (first == '《' && last == '》')
        || (first == '“' && last == '”')
        || (first == '‘' && last == '’');

    private static string TrimQuotes(string value)
    {
        if (value.Length < 2)
        {
            return value;
        }

        if ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
        {
            return value[1..^1];
        }

        return value;
    }

    private static bool IsStrongPathText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        if (LooksLikeHttpRequestLine(trimmed))
        {
            return false;
        }

        if (trimmed.StartsWith("~/", StringComparison.Ordinal) || trimmed.StartsWith("~\\", StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.StartsWith("./", StringComparison.Ordinal) || trimmed.StartsWith(".\\", StringComparison.Ordinal)
            || trimmed.StartsWith("../", StringComparison.Ordinal) || trimmed.StartsWith("..\\", StringComparison.Ordinal))
        {
            return true;
        }

        if (Path.IsPathRooted(trimmed))
        {
            return true;
        }

        var separatorCount = 0;
        foreach (var ch in trimmed)
        {
            if (ch == '/' || ch == '\\')
            {
                separatorCount++;
            }
        }

        if (separatorCount == 0)
        {
            return false;
        }

        if (trimmed.EndsWith("/", StringComparison.Ordinal) || trimmed.EndsWith("\\", StringComparison.Ordinal))
        {
            return true;
        }

        if (separatorCount >= 2)
        {
            return true;
        }

        var lastSegment = trimmed.TrimEnd('/', '\\');
        var fileName = Path.GetFileName(lastSegment);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return fileName.Contains('.', StringComparison.Ordinal);
    }

    private static bool LooksLikeHttpRequestLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var spaceIndex = trimmed.IndexOf(' ');
        if (spaceIndex <= 0)
        {
            return false;
        }

        var method = trimmed[..spaceIndex];
        if (!IsHttpMethodToken(method))
        {
            return false;
        }

        var rest = trimmed[(spaceIndex + 1)..].TrimStart();
        if (string.IsNullOrWhiteSpace(rest))
        {
            return false;
        }

        // Examples: "GET /api/v1/sessions", "POST /v1/login HTTP/1.1"
        return rest.StartsWith("/", StringComparison.Ordinal) || rest.StartsWith("\\", StringComparison.Ordinal);
    }

    private static bool IsHttpMethodToken(string value) =>
        string.Equals(value, "GET", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "POST", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "PUT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "DELETE", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "PATCH", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "HEAD", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "OPTIONS", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "CONNECT", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "TRACE", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseFileReferenceSuffix(string value, out string path, out int? line, out int? column)
    {
        path = value;
        line = null;
        column = null;

        var hashIndex = value.LastIndexOf("#L", StringComparison.OrdinalIgnoreCase);
        if (hashIndex >= 0)
        {
            var pathPart = value[..hashIndex];
            var rest = value[(hashIndex + 2)..];
            if (TryParseLineColumn(rest, out var parsedLine, out var parsedColumn))
            {
                path = pathPart;
                line = parsedLine;
                column = parsedColumn;
                return true;
            }
        }

        var lastColon = value.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == value.Length - 1)
        {
            return false;
        }

        if (value.Length >= 2 && value[1] == ':' && lastColon == 1)
        {
            return false;
        }

        var possibleColumn = value[(lastColon + 1)..];
        if (!int.TryParse(possibleColumn, out var colOrLine))
        {
            return false;
        }

        var beforeColumn = value[..lastColon];
        var secondColon = beforeColumn.LastIndexOf(':');
        if (secondColon > 0 && !(beforeColumn.Length >= 2 && beforeColumn[1] == ':' && secondColon == 1))
        {
            var possibleLine = beforeColumn[(secondColon + 1)..];
            if (int.TryParse(possibleLine, out var parsedLine))
            {
                path = beforeColumn[..secondColon];
                line = parsedLine;
                column = colOrLine;
                return true;
            }
        }

        path = beforeColumn;
        line = colOrLine;
        column = null;
        return true;
    }

    private static bool TryParseLineColumn(string value, out int? line, out int? column)
    {
        line = null;
        column = null;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var columnIndex = trimmed.IndexOf('C');
        if (columnIndex > 0)
        {
            if (!int.TryParse(trimmed[..columnIndex], out var parsedLine))
            {
                return false;
            }

            if (!int.TryParse(trimmed[(columnIndex + 1)..], out var parsedColumn))
            {
                return false;
            }

            line = parsedLine;
            column = parsedColumn;
            return true;
        }

        if (!int.TryParse(trimmed, out var onlyLine))
        {
            return false;
        }

        line = onlyLine;
        return true;
    }
}

internal static class WorkspaceFileOpener
{
    public static bool TryOpenPath(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
        {
            return false;
        }

        if (Directory.Exists(fullPath))
        {
            return TryOpenInExplorer(fullPath);
        }

        if (!File.Exists(fullPath))
        {
            return false;
        }

        if (TryShellOpenFile(fullPath))
        {
            return true;
        }

        return TrySelectInExplorer(fullPath);
    }

    private static bool TryOpenInExplorer(string directoryPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{directoryPath}\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TrySelectInExplorer(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{filePath}\"",
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryShellOpenFile(string filePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = filePath,
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
