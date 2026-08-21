using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ManiaMapAnalyzerOverlay.Avalonia.Services;

namespace ManiaMapAnalyzerOverlay.Avalonia.Views;

public partial class DocumentationDialog : Window
{
    private readonly DocumentationService _service = new();
    private string _currentId = "overview";

    public DocumentationDialog()
    {
        InitializeComponent();
        ApplyLanguage();
        BuildNavigation();
        NavigateTo("overview");
    }

    public DocumentationDialog(string initialId)
        : this()
    {
        NavigateTo(initialId);
    }

    private void ApplyLanguage()
    {
        Title = L("mapping.help_title");
        HeadingText.Text = L("documentation.title");
    }

    private string L(string key) => ManiaMapAnalyzerOverlay.UiText.Get(key);

    private void BuildNavigation()
    {
        var items = _service.Entries.Select(entry => new NavItem
        {
            Id = entry.Id,
            Title = _service.GetTitle(entry.Id)
        }).ToList();
        NavList.ItemsSource = items;
    }

    private void NavList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is NavItem item)
        {
            NavigateTo(item.Id);
        }
    }

    private void NavigateTo(string id)
    {
        var entry = _service.Find(id);
        if (entry is null)
        {
            RenderMarkdown($"# Not found\n\nDocument '{id}' not found.");
            return;
        }

        _currentId = entry.Id;
        var navItems = NavList.ItemsSource as List<NavItem>;
        if (navItems is not null)
        {
            var selected = navItems.FirstOrDefault(item => string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (selected is not null)
            {
                NavList.SelectedItem = selected;
            }
        }

        var content = _service.LoadContent(entry.Id);
        RenderMarkdown(content);
        ContentScroll.Offset = new Vector(0, 0);
    }

    private void RenderMarkdown(string markdown)
    {
        ContentPanel.Children.Clear();
        if (string.IsNullOrWhiteSpace(markdown))
        {
            ContentPanel.Children.Add(new TextBlock { Text = "(empty)", Opacity = 0.6 });
            return;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var inCodeBlock = false;
        var codeBuilder = new System.Text.StringBuilder();
        StackPanel? listPanel = null;

        void FlushList()
        {
            if (listPanel is not null)
            {
                ContentPanel.Children.Add(listPanel);
                listPanel = null;
            }
        }

        void FlushCode()
        {
            if (codeBuilder.Length > 0)
            {
                var border = new Border
                {
                    Background = new SolidColorBrush(Color.Parse("#1A1E2A")),
                    BorderBrush = new SolidColorBrush(Color.Parse("#2A2E3A")),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(10),
                    Margin = new Thickness(0, 4, 0, 4)
                };
                var block = new TextBlock
                {
                    Text = codeBuilder.ToString().TrimEnd(),
                    FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap
                };
                border.Child = block;
                ContentPanel.Children.Add(border);
                codeBuilder.Clear();
            }
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();

            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeBlock)
                {
                    FlushCode();
                    inCodeBlock = false;
                }
                else
                {
                    FlushList();
                    inCodeBlock = true;
                    codeBuilder.Clear();
                }

                continue;
            }

            if (inCodeBlock)
            {
                codeBuilder.AppendLine(rawLine);
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                FlushList();
                continue;
            }

            if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                FlushList();
                ContentPanel.Children.Add(new TextBlock
                {
                    Text = line[2..].Trim(),
                    FontSize = 22,
                    FontWeight = FontWeight.Bold,
                    Margin = new Thickness(0, 12, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                });
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushList();
                ContentPanel.Children.Add(new TextBlock
                {
                    Text = line[3..].Trim(),
                    FontSize = 17,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 10, 0, 4),
                    TextWrapping = TextWrapping.Wrap
                });
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                FlushList();
                ContentPanel.Children.Add(new TextBlock
                {
                    Text = line[4..].Trim(),
                    FontSize = 13,
                    FontWeight = FontWeight.SemiBold,
                    Margin = new Thickness(0, 8, 0, 2),
                    TextWrapping = TextWrapping.Wrap
                });
                continue;
            }

            if (line.TrimStart().StartsWith("- ", StringComparison.Ordinal) ||
                line.TrimStart().StartsWith("* ", StringComparison.Ordinal))
            {
                var text = line.TrimStart()[2..].Trim();
                listPanel ??= new StackPanel { Spacing = 4, Margin = new Thickness(0, 4, 0, 4) };
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                row.Children.Add(new TextBlock { Text = "•", Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top });
                var content = CreateInlinePanel(text);
                row.Children.Add(content);
                listPanel.Children.Add(row);
                continue;
            }

            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                FlushList();
                var border = new Border
                {
                    BorderBrush = new SolidColorBrush(Color.Parse("#3A4156")),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(8, 4, 4, 4),
                    Margin = new Thickness(0, 4, 0, 4)
                };
                border.Child = CreateInlinePanel(line[2..].Trim());
                ContentPanel.Children.Add(border);
                continue;
            }

            if (line.Trim() == "---")
            {
                FlushList();
                ContentPanel.Children.Add(new Border
                {
                    Height = 1,
                    Background = new SolidColorBrush(Color.Parse("#2A2E3A")),
                    Margin = new Thickness(0, 8, 0, 8)
                });
                continue;
            }

            FlushList();
            ContentPanel.Children.Add(CreateInlinePanel(line));
        }

        FlushList();
        FlushCode();
    }

    private Control CreateInlinePanel(string text)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };

        // Regex for links, bold, italic, code
        // We parse sequentially: links first, then inline code, then bold/italic
        var linkPattern = new Regex(@"\[(.+?)\]\((.+?)\)", RegexOptions.Compiled);
        var codePattern = new Regex(@"`(.+?)`", RegexOptions.Compiled);
        var boldPattern = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Compiled);

        // Tokenize by links first
        var segments = new List<InlineSegment> { new InlineSegment { Text = text, Type = InlineType.Plain } };
        segments = SplitByPattern(segments, linkPattern, match => new InlineSegment
        {
            Text = match.Groups[1].Value,
            Url = match.Groups[2].Value,
            Type = InlineType.Link
        });
        segments = SplitByPattern(segments, codePattern, match => new InlineSegment
        {
            Text = match.Groups[1].Value,
            Type = InlineType.Code
        });
        segments = SplitByPattern(segments, boldPattern, match => new InlineSegment
        {
            Text = match.Groups[1].Value,
            Type = InlineType.Bold
        });

        foreach (var segment in segments)
        {
            switch (segment.Type)
            {
                case InlineType.Link:
                    var linkUrl = segment.Url ?? string.Empty;
                    var linkButton = new Button
                    {
                        Content = segment.Text,
                        Padding = new Thickness(0),
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Foreground = new SolidColorBrush(Color.Parse("#6EA8FF")),
                        Cursor = new Cursor(StandardCursorType.Hand),
                        FontSize = 12
                    };
                    linkButton.Click += (_, _) => HandleLinkClick(linkUrl);
                    // Underline
                    var linkText = linkButton.Content as string;
                    if (linkText is not null)
                    {
                        linkButton.Content = new TextBlock
                        {
                            Text = linkText,
                            TextDecorations = TextDecorations.Underline,
                            Foreground = new SolidColorBrush(Color.Parse("#6EA8FF"))
                        };
                    }

                    panel.Children.Add(linkButton);
                    break;
                case InlineType.Code:
                    var codeBorder = new Border
                    {
                        Background = new SolidColorBrush(Color.Parse("#242836")),
                        CornerRadius = new CornerRadius(3),
                        Padding = new Thickness(4, 1, 4, 1),
                        Margin = new Thickness(2, 0, 2, 0)
                    };
                    codeBorder.Child = new TextBlock
                    {
                        Text = segment.Text,
                        FontFamily = new FontFamily("Consolas,Menlo,monospace"),
                        FontSize = 11
                    };
                    panel.Children.Add(codeBorder);
                    break;
                case InlineType.Bold:
                    panel.Children.Add(new TextBlock
                    {
                        Text = segment.Text,
                        FontWeight = FontWeight.Bold,
                        FontSize = 12,
                        Margin = new Thickness(0, 0, 2, 0)
                    });
                    break;
                default:
                    panel.Children.Add(new TextBlock
                    {
                        Text = segment.Text,
                        FontSize = 12,
                        Margin = new Thickness(0, 0, 2, 0)
                    });
                    break;
            }
        }

        return panel;
    }

    private void HandleLinkClick(string url)
    {
        if (DocumentationService.IsDocumentationLink(url))
        {
            var id = DocumentationService.ExtractDocId(url);
            if (!string.IsNullOrWhiteSpace(id))
            {
                NavigateTo(id);
                return;
            }
        }

        try
        {
            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (url.StartsWith("doc://", StringComparison.OrdinalIgnoreCase))
            {
                var id = DocumentationService.ExtractDocId(url);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    NavigateTo(id);
                }
            }
        }
        catch (Exception exception)
        {
            // Log but do not crash viewer
            ManiaMapAnalyzerOverlay.Avalonia.Services.AppLogger.Warning("Opening documentation link", $"Could not open '{url}': {exception.Message}", exception);
        }
    }

    private List<InlineSegment> SplitByPattern(List<InlineSegment> segments, Regex pattern, Func<Match, InlineSegment> creator)
    {
        var result = new List<InlineSegment>();
        foreach (var segment in segments)
        {
            if (segment.Type != InlineType.Plain)
            {
                result.Add(segment);
                continue;
            }

            var text = segment.Text;
            var lastIndex = 0;
            foreach (Match match in pattern.Matches(text))
            {
                if (match.Index > lastIndex)
                {
                    result.Add(new InlineSegment { Text = text.Substring(lastIndex, match.Index - lastIndex), Type = InlineType.Plain });
                }

                result.Add(creator(match));
                lastIndex = match.Index + match.Length;
            }

            if (lastIndex < text.Length)
            {
                result.Add(new InlineSegment { Text = text.Substring(lastIndex), Type = InlineType.Plain });
            }
        }

        return result;
    }

    private sealed class NavItem
    {
        public string Id { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
    }

    private enum InlineType
    {
        Plain,
        Bold,
        Code,
        Link
    }

    private sealed class InlineSegment
    {
        public string Text { get; init; } = string.Empty;
        public string? Url
        {
            get; init;
        }
        public InlineType Type
        {
            get; init;
        }
    }
}
